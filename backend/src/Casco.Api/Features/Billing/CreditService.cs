using System.Collections.Concurrent;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public record CreditBalance(int Plan, int Topup, int Reserved)
{
    public int Total => Plan + Topup;
    public int Available => Math.Max(0, Total - Reserved);
}

public record CreditShortfall(int Available, int Needed);

public static class ReleaseReasons
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Free = "free";
    /// <summary>The server restarted (or the worker died) while the task was running.</summary>
    public const string Restart = "restart";
    /// <summary>Found by reconciliation: the task had already finished or was gone.</summary>
    public const string Stale = "stale";
}

public static class CreditMath
{
    /// <summary>Converts real provider cost into credits. Any non-zero cost costs at least one credit.</summary>
    public static int FromCost(decimal costUsd, decimal usdPerCredit)
    {
        if (costUsd <= 0) return 0;
        return Math.Max(1, (int)Math.Ceiling(costUsd / usdPerCredit));
    }
}

/// <summary>
/// Append-only credit ledger. Balance = sum of entries; available = balance - active reservations.
/// Single API instance assumption: per-user locks are in-process.
/// </summary>
public class CreditService(AppDbContext db, IOptions<BillingOptions> options)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private readonly BillingOptions _opt = options.Value;

    public static SemaphoreSlim LockFor(Guid userId) => Locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    public int CreditsForCost(decimal costUsd) => CreditMath.FromCost(costUsd, _opt.UsdPerCredit);

    public async Task<CreditBalance> GetBalanceAsync(Guid userId)
    {
        var sums = await db.CreditEntries.Where(e => e.UserId == userId)
            .GroupBy(e => e.Bucket)
            .Select(g => new { Bucket = g.Key, Sum = g.Sum(e => e.Amount) })
            .ToListAsync();
        var reserved = await db.CreditReservations.Where(r => r.UserId == userId && r.Status == ReservationStatuses.Reserved)
            .SumAsync(r => (int?)r.Amount) ?? 0;
        return new CreditBalance(
            sums.FirstOrDefault(s => s.Bucket == CreditBuckets.Plan)?.Sum ?? 0,
            sums.FirstOrDefault(s => s.Bucket == CreditBuckets.Topup)?.Sum ?? 0,
            reserved);
    }

    public async Task GrantAsync(Guid userId, int amount, string bucket, string type, string? reference)
    {
        if (amount == 0) return;
        db.CreditEntries.Add(new CreditEntry { UserId = userId, Amount = amount, Bucket = bucket, Type = type, Reference = reference });
        await db.SaveChangesAsync();
    }

    /// <summary>Plan credits do not roll over: zero the plan bucket before a new grant or on expiry.</summary>
    public async Task ExpirePlanCreditsAsync(Guid userId, string reference)
    {
        var planBalance = await db.CreditEntries.Where(e => e.UserId == userId && e.Bucket == CreditBuckets.Plan)
            .SumAsync(e => (int?)e.Amount) ?? 0;
        if (planBalance > 0)
            await GrantAsync(userId, -planBalance, CreditBuckets.Plan, CreditEntryTypes.PlanExpire, reference);
    }

    public Task<int> ReserveAsync(Guid userId, Guid taskId, int amount) => ReserveAsync(userId, taskId, amount, amount);

    /// <summary>
    /// Holds up to <paramref name="amount"/> credits, or whatever is left if that is at least <paramref name="minimum"/>,
    /// so the last credits of the month stay usable. Returns the amount held (the task's spending cap).
    /// </summary>
    public async Task<int> ReserveAsync(Guid userId, Guid taskId, int amount, int minimum)
    {
        var gate = LockFor(userId);
        await gate.WaitAsync();
        try
        {
            var balance = await GetBalanceAsync(userId);
            if (balance.Available < Math.Min(minimum, amount))
                throw new ApiException(402, "رصيدك من النقاط لا يكفي لتنفيذ هذا الطلب.", "insufficient_credits")
                {
                    Details = new CreditShortfall(Math.Max(0, balance.Available), Math.Min(minimum, amount))
                };
            var held = Math.Min(amount, balance.Available);
            db.CreditReservations.Add(new CreditReservation { UserId = userId, TaskId = taskId, Amount = held });
            await db.SaveChangesAsync();
            return held;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Adds up to <paramref name="extra"/> of the user's free credits to a running task's reservation (the next part of a big build).
    /// Returns what was added: 0 when fewer than <paramref name="minimum"/> credits are left.
    /// </summary>
    public async Task<int> ExtendAsync(Guid userId, Guid taskId, int extra, int minimum)
    {
        var gate = LockFor(userId);
        await gate.WaitAsync();
        try
        {
            var reservation = await ActiveAsync(taskId);
            if (reservation is null) return 0;
            var available = (await GetBalanceAsync(userId)).Available;
            if (available < Math.Min(minimum, extra)) return 0;
            var added = Math.Min(extra, available);
            reservation.Amount += added;
            await db.SaveChangesAsync();
            return added;
        }
        finally { gate.Release(); }
    }

    private Task<CreditReservation?> ActiveAsync(Guid taskId) =>
        db.CreditReservations.FirstOrDefaultAsync(r => r.TaskId == taskId && r.Status == ReservationStatuses.Reserved);

    private static void Close(CreditReservation reservation, string status, string reason, int consumed = 0)
    {
        reservation.Status = status;
        reservation.CloseReason = reason;
        reservation.Consumed = consumed;
        reservation.ClosedAt = DateTime.UtcNow;
    }

    /// <summary>Frees a task's hold without charging anything.</summary>
    public async Task ReleaseAsync(Guid taskId, string reason = ReleaseReasons.Failed)
    {
        var reservation = await ActiveAsync(taskId);
        if (reservation is null) return;
        Close(reservation, ReservationStatuses.Released, reason);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Safety net for holds that outlived their task (a crash between steps, a lost update): releases every reservation
    /// whose task has finished or no longer exists. Returns how many were released.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var orphans = await db.CreditReservations
            .Where(r => r.Status == ReservationStatuses.Reserved &&
                        !db.AgentTasks.Any(t => t.Id == r.TaskId && (t.Status == TaskStatuses.Queued || t.Status == TaskStatuses.Running)))
            .ToListAsync(ct);
        foreach (var r in orphans) Close(r, ReservationStatuses.Released, ReleaseReasons.Stale);
        if (orphans.Count > 0) await db.SaveChangesAsync(ct);
        return orphans.Count;
    }

    /// <summary>Closes the reservation and charges the actual usage, consuming plan credits first.</summary>
    /// <summary>Charges a finished desktop model call. Does not hold credits while the call is in flight.</summary>
    public async Task<CreditBalance> ChargeAsync(Guid userId, int credits, string reference)
    {
        var gate = LockFor(userId);
        await gate.WaitAsync();
        try
        {
            if (credits > 0)
            {
                var balance = await GetBalanceAsync(userId);
                var charge = Math.Min(credits, Math.Max(0, balance.Available));
                var fromPlan = Math.Min(charge, Math.Max(0, balance.Plan));
                var fromTopup = Math.Min(charge - fromPlan, Math.Max(0, balance.Topup));
                if (fromPlan > 0)
                    db.CreditEntries.Add(new CreditEntry { UserId = userId, Amount = -fromPlan, Bucket = CreditBuckets.Plan, Type = CreditEntryTypes.Usage, Reference = reference });
                if (fromTopup > 0)
                    db.CreditEntries.Add(new CreditEntry { UserId = userId, Amount = -fromTopup, Bucket = CreditBuckets.Topup, Type = CreditEntryTypes.Usage, Reference = reference });
                if (fromPlan + fromTopup > 0) await db.SaveChangesAsync();
            }
            return await GetBalanceAsync(userId);
        }
        finally { gate.Release(); }
    }

    public async Task SettleAsync(Guid userId, Guid taskId, int credits)
    {
        var gate = LockFor(userId);
        await gate.WaitAsync();
        try
        {
            var reservation = await ActiveAsync(taskId);
            if (reservation is not null) Close(reservation, ReservationStatuses.Settled, ReleaseReasons.Completed, credits);

            if (credits > 0)
            {
                var balance = await GetBalanceAsync(userId);
                var fromPlan = Math.Min(credits, Math.Max(0, balance.Plan));
                var fromTopup = Math.Min(credits - fromPlan, Math.Max(0, balance.Topup));
                var remainder = credits - fromPlan - fromTopup;
                var reference = taskId.ToString();
                if (fromPlan + remainder > 0)
                    db.CreditEntries.Add(new CreditEntry { UserId = userId, Amount = -(fromPlan + remainder), Bucket = CreditBuckets.Plan, Type = CreditEntryTypes.Usage, Reference = reference });
                if (fromTopup > 0)
                    db.CreditEntries.Add(new CreditEntry { UserId = userId, Amount = -fromTopup, Bucket = CreditBuckets.Topup, Type = CreditEntryTypes.Usage, Reference = reference });
            }
            await db.SaveChangesAsync();
        }
        finally { gate.Release(); }
    }
}
