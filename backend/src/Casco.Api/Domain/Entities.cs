namespace Casco.Api.Domain;

public static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
}

public static class PlanKeys
{
    public const string Free = "free";
    public const string Pro = "pro";
}

public static class BillingIntervals
{
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";
}

public static class CreditBuckets
{
    public const string Plan = "plan";
    public const string Topup = "topup";
}

public static class CreditEntryTypes
{
    public const string SignupBonus = "signup_bonus";
    public const string PlanGrant = "plan_grant";
    public const string PlanExpire = "plan_expire";
    public const string Usage = "usage";
    public const string Topup = "topup";
    public const string Admin = "admin";
}

/// <summary>
/// Credits are held (reserved) while a task runs and only charged from the result, so a failed task has nothing to refund:
/// it is released. Rows are kept after closing as an audit trail; only reserved rows count against the balance.
/// </summary>
public static class ReservationStatuses
{
    public const string Reserved = "reserved";
    /// <summary>Task finished: <see cref="CreditReservation.Consumed"/> was charged and the rest released.</summary>
    public const string Settled = "settled";
    /// <summary>Nothing charged (failure, free site, server restart, or a dead task found by reconciliation).</summary>
    public const string Released = "released";
}

public static class TaskStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class TaskKinds
{
    public const string Generate = "generate";
    public const string Edit = "edit";
    public const string Fix = "fix";
}

/// <summary>Monthly hosting a published site needs: static pages only, or pages plus the Casco backend.</summary>
public static class HostingTiers
{
    public const string Static = "static";
    public const string Backend = "backend";
    /// <summary>A prebuilt React app (the dist folder), billed yearly.</summary>
    public const string React = "react";

    public static int Rank(string? tier) => tier switch { Backend => 2, Static or React => 1, _ => 0 };
}

public static class PaymentKinds
{
    public const string Subscription = "subscription";
    public const string Topup = "topup";
    public const string Hosting = "hosting";
}

public static class OrderStatuses
{
    public const string New = "new";
    public const string Confirmed = "confirmed";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Canceled = "canceled";

    public static readonly string[] All = [New, Confirmed, Shipped, Delivered, Canceled];
}

public static class BookingStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Completed = "completed";
    public const string Canceled = "canceled";

    public static readonly string[] All = [Pending, Confirmed, Completed, Canceled];
}

public static class PaymentStatuses
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

public static class WalletMethods
{
    public const string VodafoneCash = "vodafone_cash";
    public const string InstaPay = "instapay";

    public static bool IsValid(string? method) => method is VodafoneCash or InstaPay;
}

public static class WalletTransferStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Local wallet price for one month of Pro. Dollar checkout stays on the payment gateway.</summary>
public static class WalletPay
{
    public const int AmountMinor = 68000;
    public const string Currency = "EGP";
    public const string Phone = "01026159280";
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Empty for accounts created through Google/Apple sign-in.</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>"sub" claim of the linked Google account.</summary>
    public string? GoogleId { get; set; }
    /// <summary>"sub" claim of the linked Apple ID.</summary>
    public string? AppleId { get; set; }
    public string Role { get; set; } = Roles.User;
    public bool FreeSiteUsed { get; set; }
    /// <summary>Interface language (ar / en / hi), used for e-mails.</summary>
    public string Locale { get; set; } = "ar";
    /// <summary>Public code in the invite link. Empty until the user opens the share option.</summary>
    public string? ReferralCode { get; set; }
    /// <summary>The account whose invite link created this user. Set only at signup.</summary>
    public Guid? ReferredByUserId { get; set; }
    /// <summary>True after 10 invited accounts were verified and the extra prompts were granted.</summary>
    public bool ShareRewardClaimed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Subscription? Subscription { get; set; }
}

/// <summary>One computer that installed Casco Studio. The free prompt and the five free hours belong to the machine, not the account.</summary>
public class DesktopMachine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>SHA-256 of the id stored on that computer.</summary>
    public string MachineHash { get; set; } = "";
    public bool FreePromptUsed { get; set; }
    /// <summary>Bits 0–4 are the five group shares.</summary>
    public int ShareMask { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public Guid? LastUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A desktop chat prompt kept for the admin dashboard. The text is clipped.</summary>
public class DesktopChatMessage
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string MachineHash { get; set; } = "";
    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A Casco Studio desktop sign-in waiting for the website account to approve it.</summary>
public class DesktopLogin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Short code shown in the browser. Not a secret by itself.</summary>
    public string UserCode { get; set; } = "";
    /// <summary>SHA-256 of the secret the desktop polls with.</summary>
    public string DeviceCodeHash { get; set; } = "";
    public Guid? UserId { get; set; }
    /// <summary>pending / approved / consumed / denied.</summary>
    public string Status { get; set; } = "pending";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A JWT rejected after logout, until it would have expired on its own.</summary>
public class RevokedToken
{
    public string Jti { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

/// <summary>One row per e-mail that must go out only once (e.g. "hosting:{site}:{paidUntil}:7d").</summary>
public class SentNotification
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public Guid? UserId { get; set; }
    public string Kind { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Plan { get; set; } = PlanKeys.Free;
    public string? Interval { get; set; }
    public DateTime? CurrentPeriodStart { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
    public DateTime? NextCreditGrantAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsProActive(DateTime now) =>
        Plan == PlanKeys.Pro && CurrentPeriodEnd is { } end && end > now;
}

public class CreditEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public int Amount { get; set; }
    public string Bucket { get; set; } = CreditBuckets.Plan;
    public string Type { get; set; } = "";
    public string? Reference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CreditReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>Held so far (grows while a big build continues in parts).</summary>
    public int Amount { get; set; }
    public string Status { get; set; } = ReservationStatuses.Reserved;
    /// <summary>Credits actually charged when settled.</summary>
    public int Consumed { get; set; }
    /// <summary>completed / failed / free / restart / stale.</summary>
    public string? CloseReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "subscription";
    public string? Plan { get; set; }
    public string? Interval { get; set; }
    public string? TopupPackId { get; set; }
    /// <summary>Credits the pack gave at checkout, so later price edits or a deleted pack don't change what was bought.</summary>
    public int? TopupCredits { get; set; }
    /// <summary>Site whose hosting this payment renews (Kind = hosting).</summary>
    public Guid? ProjectId { get; set; }
    public string? HostingTier { get; set; }
    public int AmountMinor { get; set; }
    public string Currency { get; set; } = "USD";
    public string Provider { get; set; } = "ziina";
    public string? ProviderPaymentId { get; set; }
    public string Status { get; set; } = PaymentStatuses.Pending;
    public bool IsTest { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>A Vodafone Cash or InstaPay transfer waiting for an admin to approve the screenshot.</summary>
public class WalletTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Method { get; set; } = "";
    public int AmountMinor { get; set; } = WalletPay.AmountMinor;
    public string Currency { get; set; } = WalletPay.Currency;
    public string ProofKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Status { get; set; } = WalletTransferStatuses.Pending;
    public string? ReviewNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>JSON array of chat hints, generated once for a paying owner.</summary>
    public string? ChatHints { get; set; }
    public string TemplateKey { get; set; } = "landing";
    public string SiteKey { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? CustomDomain { get; set; }
    public bool CustomDomainVerified { get; set; }
    public bool AdsRequireApproval { get; set; }
    /// <summary>JSON of <see cref="Features.SiteRuntime.SiteSettings"/> (store, bookings, notifications).</summary>
    public string SettingsJson { get; set; } = "{}";
    public Guid? CurrentVersionId { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? HostingTier { get; set; }
    public DateTime? HostingPaidUntil { get; set; }
    /// <summary>Admin decision, independent of hosting payment: a suspended site is offline even when hosting is paid.</summary>
    public bool IsAdminSuspended { get; set; }
    public string? AdminSuspensionReason { get; set; }
    public DateTime? AdminSuspendedAt { get; set; }
    public Guid? AdminSuspendedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ProjectVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public string FilesJson { get; set; } = "{}";
    public string Summary { get; set; } = "";
    public Guid? TaskId { get; set; }
    /// <summary>The user's request that produced this version.</summary>
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public int Parts { get; set; }
    public int FilesChanged { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatMessage
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    /// <summary>JSON array of image URLs attached to a user message.</summary>
    public string? ImagesJson { get; set; }
    /// <summary>JSON questions the model asked when it needed a decision. Empty after the user answers by sending the next message.</summary>
    public string? QuestionsJson { get; set; }
    public Guid? TaskId { get; set; }
    public int? Credits { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AgentTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = TaskKinds.Edit;
    public string Prompt { get; set; } = "";
    public string Tier { get; set; } = "cheap";
    public string Status { get; set; } = TaskStatuses.Queued;
    public string? Error { get; set; }
    public string? ModelUsed { get; set; }
    public int CreditsCharged { get; set; }
    public decimal CostUsd { get; set; }
    public bool IsFreeGrant { get; set; }
    /// <summary>Set when this task continues an unfinished big build ("كمل").</summary>
    public Guid? ContinuesTaskId { get; set; }
    /// <summary>Pages the site had when the build started (carried over by continuations), for "16 of 30 pages built".</summary>
    public int PagesBefore { get; set; }
    public int Parts { get; set; }
    /// <summary>What the model said is still left to build when the task stopped (null = finished).</summary>
    public string? Remaining { get; set; }
    /// <summary>
    /// Site files and progress after the last completed part of a running multi-part build, so a crash keeps whole parts
    /// (never half of one). Cleared when the task finishes.
    /// </summary>
    public string? CheckpointJson { get; set; }
    public Guid? ResultVersionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>One row per AI provider call (including failed calls, fallbacks, fix retries and local cache hits).</summary>
public class AiUsage
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? TaskId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>generate / edit / fix.</summary>
    public string Purpose { get; set; } = "";
    /// <summary>The user's plan when the call was made (free / pro), so cost can be split per plan.</summary>
    public string? Plan { get; set; }
    public int InputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int OutputTokens { get; set; }
    /// <summary>Tokens reported by the provider priced with our model price table (what credits are charged on).</summary>
    public decimal EstimatedCostUsd { get; set; }
    /// <summary>
    /// What the provider really billed: reported by the provider when it returns a cost, 0 for local cache hits,
    /// otherwise filled when the monthly invoice is reconciled. Null = not known yet (reports fall back to the estimate).
    /// </summary>
    public decimal? ActualCostUsd { get; set; }
    /// <summary>0 for the first call of a task; each fallback model or fix round adds one.</summary>
    public int RetryCount { get; set; }
    /// <summary>Part of a multi-part build the call belonged to (1 for normal requests).</summary>
    public int Part { get; set; } = 1;
    public bool ResponseCacheHit { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A provider's real bill for one month. Kept so old rows can be copied into account entries, then left empty.</summary>
public class AiProviderInvoice
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    /// <summary>yyyy-MM (UTC).</summary>
    public string Month { get; set; } = "";
    public decimal AmountUsd { get; set; }
    public decimal EstimatedUsd { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Money the business spends: a daily, monthly, or yearly cost, or a one-time payment.</summary>
public class AccountEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal AmountUsd { get; set; }
    /// <summary>day, month, year, or once.</summary>
    public string Cadence { get; set; } = "month";
    /// <summary>First day the cost applies, or the day of a one-time payment. UTC date.</summary>
    public DateTime OnDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AiResponseCacheEntry
{
    public string Key { get; set; } = "";
    public string Model { get; set; } = "";
    public string Content { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal OriginalCostUsd { get; set; }
    public int Hits { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class FormSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string FormName { get; set; } = "contact";
    public string DataJson { get; set; } = "{}";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SiteUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Course
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ImageUrl { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Instructor { get; set; }
    public string? Category { get; set; }
    public string? PaymentLink { get; set; }
    public bool IsPublished { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Lesson> Lessons { get; set; } = [];
}

public class Lesson
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public string Title { get; set; } = "";
    public string? VideoUrl { get; set; }
    public string? Content { get; set; }
    public bool IsFreePreview { get; set; }
    public int DurationMinutes { get; set; }
    public int SortOrder { get; set; }
}

public class Enrollment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Guid SiteUserId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Ad
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid SiteUserId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "AED";
    public string? Category { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public string ImagesJson { get; set; } = "[]";
    public string Status { get; set; } = "approved";
    public int Views { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A row of a custom collection declared in the site's casco.backend.json.</summary>
public class SiteRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Collection { get; set; } = "";
    /// <summary>Site member who created the record (null for anonymous visitors, the owner or server functions).</summary>
    public Guid? SiteUserId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? ComparePrice { get; set; }
    public string? Category { get; set; }
    public string ImagesJson { get; set; } = "[]";
    /// <summary>Null means unlimited.</summary>
    public int? Stock { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public Guid? SiteUserId { get; set; }
    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Notes { get; set; }
    /// <summary>Snapshot of the ordered lines (name and price at order time).</summary>
    public string ItemsJson { get; set; } = "[]";
    public decimal Subtotal { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "AED";
    public string PaymentMethod { get; set; } = "cod";
    public string Status { get; set; } = OrderStatuses.New;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class BookingService
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DurationMinutes { get; set; } = 30;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ServiceId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public Guid? SiteUserId { get; set; }
    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = BookingStatuses.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
