using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Casco.Api.Domain;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Projects;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Casco.Api.Features.SiteRuntime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Agent;

public class AgentQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid taskId) => _channel.Writer.WriteAsync(taskId);
    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>Runs agent tasks in the background with a fixed number of concurrent workers.</summary>
public class AgentWorker(AgentQueue queue, IServiceScopeFactory scopes, IOptions<AgentOptions> options, LeaderElection leader, ILogger<AgentWorker> logger) : BackgroundService
{
    /// <summary>Tasks a worker of this process is executing (single API instance: anything else marked running is dead).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await leader.WaitUntilLeaderAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Nothing runs yet, so every task still marked running died with the previous process.
        await RecoverDeadTasksAsync(ReleaseReasons.Restart, stoppingToken);
        await EnqueueQueuedAsync(stoppingToken);

        var workers = Enumerable.Range(0, Math.Max(1, options.Value.Workers)).Select(worker => Task.Run(async () =>
        {
            await foreach (var taskId in queue.Reader.ReadAllAsync(stoppingToken))
            {
                _running[taskId] = 0;
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AgentTaskRunner>().RunAsync(taskId, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Agent task {TaskId} crashed", taskId);
                }
                finally
                {
                    _running.TryRemove(taskId, out _);
                }
            }
        }, stoppingToken));
        await Task.WhenAll(workers.Append(ReconcileLoopAsync(stoppingToken)));
    }

    private async Task EnqueueQueuedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var id in await db.AgentTasks.Where(t => t.Status == TaskStatuses.Queued).OrderBy(t => t.CreatedAt).Select(t => t.Id).ToListAsync(ct))
            await queue.EnqueueAsync(id);
    }

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.ReconcileMinutes)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await RecoverDeadTasksAsync(ReleaseReasons.Stale, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogError(ex, "Task reconciliation failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// A task marked running that no worker holds died mid-run: the parts it finished are kept from its checkpoint,
    /// otherwise it fails. Then every credit hold that outlived its task is released.
    /// </summary>
    private async Task RecoverDeadTasksAsync(string reason, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dead = (await db.AgentTasks.Where(t => t.Status == TaskStatuses.Running).Select(t => t.Id).ToListAsync(ct))
            .Where(id => !_running.ContainsKey(id)).ToList();
        foreach (var id in dead)
        {
            logger.LogWarning("Task {TaskId} was marked running without a worker; recovering it", id);
            using var taskScope = scopes.CreateScope();
            await taskScope.ServiceProvider.GetRequiredService<AgentTaskRunner>().SalvageAsync(id, reason);
        }
        var released = await scope.ServiceProvider.GetRequiredService<CreditService>().ReconcileAsync(ct);
        if (released > 0) logger.LogWarning("Released {Count} credit reservations whose task had already ended", released);
    }
}

/// <summary>Saved after each completed part of a multi-part build: whole parts only, so a crash never keeps half of one.</summary>
public record BuildCheckpoint(int Part, List<string> Done, string? Remaining, List<string> Changed, SortedDictionary<string, string> Files);

public enum BuildStop { None, MaxParts, OutOfCredits, Interrupted, Limit }

public partial class AgentTaskRunner(
    AppDbContext db,
    AiClient ai,
    CreditService credits,
    SubscriptionService subscriptions,
    TemplateCatalog templates,
    TaskStreamHub streams,
    IMemoryCache cache,
    IOptions<AgentOptions> options,
    IOptions<BillingOptions> billing,
    ILogger<AgentTaskRunner> logger)
{
    private readonly AgentOptions _opt = options.Value;
    private const int MaxReadRounds = 2;
    private const string CrashMessage = "توقف الخادم أثناء التنفيذ، أعد المحاولة";

    public async Task RunAsync(Guid taskId, CancellationToken stoppingToken)
    {
        var task = await db.AgentTasks.FirstOrDefaultAsync(t => t.Id == taskId, stoppingToken);
        if (task is null || task.Status != TaskStatuses.Queued) return;
        var stream = streams.GetOrCreate(task.Id, task.UserId);
        try
        {
            await RunInnerAsync(task, stream, stoppingToken);
        }
        finally
        {
            var status = await db.AgentTasks.Where(t => t.Id == taskId).Select(t => t.Status).FirstOrDefaultAsync(CancellationToken.None);
            streams.Complete(task.Id, status ?? TaskStatuses.Failed);
        }
    }

    /// <summary>Finishes a task whose worker died: keeps the parts completed before the crash, or fails it and frees its credits.</summary>
    public async Task SalvageAsync(Guid taskId, string reason)
    {
        if (!await FinishFromCheckpointAsync(taskId))
        {
            var task = await db.AgentTasks.FirstOrDefaultAsync(t => t.Id == taskId);
            if (task is null || task.Status != TaskStatuses.Running) return;
            await FailAsync(task, CrashMessage, reason);
        }
        var status = await db.AgentTasks.Where(t => t.Id == taskId).Select(t => t.Status).FirstOrDefaultAsync();
        streams.Complete(taskId, status ?? TaskStatuses.Failed);
    }

    private async Task RunInnerAsync(AgentTask task, TaskStream stream, CancellationToken stoppingToken)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, stoppingToken);
        if (project is null) { await FailAsync(task, "المشروع غير موجود"); return; }

        task.Status = TaskStatuses.Running;
        task.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(stoppingToken);

        // A repair keeps going until the errors are gone or the credits run out. This only stops a task that never returns.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromHours(3));

        try
        {
            await ExecuteAsync(task, project, stream, timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            if (!await FinishFromCheckpointAsync(task.Id))
                await FailAsync(task, "استغرق الطلب وقتاً أطول من المسموح، جرّب طلباً أصغر");
        }
        catch (ApiException ex)
        {
            await FailAsync(task, ex.Message);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Task {TaskId} failed", task.Id);
            if (!await FinishFromCheckpointAsync(task.Id))
                await FailAsync(task, "حدث خطأ أثناء تنفيذ الطلب، حاول مرة أخرى");
        }
    }

    private EditConstraints Constraints(PlanInfo plan) => plan.IsPro
        ? new EditConstraints(false, _opt.ProMaxFiles, _opt.MaxFileBytes, _opt.ProMaxTotalBytes)
        : new EditConstraints(true, _opt.MaxFiles, _opt.MaxFileBytes, _opt.MaxTotalBytes);

    private async Task ExecuteAsync(AgentTask task, Project project, TaskStream stream, CancellationToken ct)
    {
        var plan = await subscriptions.GetPlanAsync(task.UserId);
        var template = templates.Get(project.TemplateKey) ?? templates.Default;
        var isNewSite = task.Kind == TaskKinds.Generate;

        // New sites start from the raw template (identical for every user -> provider prompt cache hits);
        // edits start from the latest version.
        var currentVersion = project.CurrentVersionId is { } vid ? await db.ProjectVersions.FindAsync([vid], ct) : null;
        SortedDictionary<string, string> files = isNewSite || currentVersion is null
            ? new SortedDictionary<string, string>(template.Files.ToDictionary(), StringComparer.Ordinal)
            : SiteFiles.Parse(currentVersion.FilesJson);

        var singlePage = false;
        var constraints = Constraints(plan);
        var limits = new SiteLimits(constraints.MaxFiles, constraints.MaxTotalBytes);
        var baselineErrors = SiteValidator.Validate(files, singlePage).Select(e => e.Error).ToHashSet();

        var history = (await db.ChatMessages.Where(m => m.ProjectId == project.Id && m.TaskId != task.Id)
                .OrderByDescending(m => m.Id).Take(_opt.HistoryMessages)
                .Select(m => new { m.Role, m.Content }).ToListAsync(ct))
            .AsEnumerable().Reverse().Select(h => (h.Role, h.Content)).ToList();

        // "كمل": pick the unfinished build up where the previous task stopped, with its original request.
        var request = await RequestAsync(task);
        var locale = await db.Users.Where(u => u.Id == project.UserId).Select(u => u.Locale).FirstOrDefaultAsync(ct);
        var language = ProjectService.SiteLanguage(locale) ?? "Arabic";
        var resumeFrom = task.ContinuesTaskId is { } previousId
            ? await db.AgentTasks.Where(t => t.Id == previousId).Select(t => t.Remaining).FirstOrDefaultAsync(ct)
            : null;
        if (task.ContinuesTaskId is null) task.PagesBefore = files.Keys.Count(SiteFiles.IsHtml);

        List<ChatMessageDto> Prompt(BuildProgress? progress) => PromptBuilder.Build(files, new PromptContext(
            project.Name, template.Key, template.Modules, plan.IsPro, isNewSite, request, history, limits, progress, language), _opt.ContextBytesLimit);

        var reservation = task.IsFreeGrant ? int.MaxValue
            : await db.CreditReservations.Where(r => r.TaskId == task.Id && r.Status == ReservationStatuses.Reserved).Select(r => r.Amount).FirstOrDefaultAsync(ct);
        var cacheKey = task.IsFreeGrant ? $"casco:t:{template.Key}" : $"casco:p:{project.Id:N}";
        // A full site does not fit in one reply. The one free grant runs to the same part cap as Pro so it can finish.
        var maxParts = Math.Max(1, plan.IsPro || task.IsFreeGrant ? _opt.MaxStepsPerTask : 4);
        var topUp = _opt.ReserveCredits.GetValueOrDefault(task.Tier, 80);
        var minTopUp = _opt.MinCreditsToStart.GetValueOrDefault(task.Tier, 10);

        var done = new List<string>();
        var thoughts = new List<string>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        string? summary = null;
        string? remaining = null;
        var anyChange = false;
        var partChanged = false;
        var remainingErrors = new List<string>();
        var stop = BuildStop.None;
        var part = 1;
        var attempt = 0;
        // Rounds where the model asked to see files from the site map.
        var readRounds = 0;
        // State after the last completed part: a part that cannot finish is dropped as a whole, never kept half-applied.
        var completedParts = 0;
        var good = (Files: files, Remaining: (string?)null, Changed: new HashSet<string>(StringComparer.Ordinal));
        void DropUnfinishedPart()
        {
            if (completedParts == 0 || !partChanged) return;
            (files, remaining, changed) = (good.Files, good.Remaining, new HashSet<string>(good.Changed, StringComparer.Ordinal));
            partChanged = false;
            summary = null;
        }
        var messages = Prompt(resumeFrom is null ? null : new BuildProgress(part, [], resumeFrom));
        using var partTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        partTimeout.CancelAfter(TimeSpan.FromSeconds(_opt.TaskTimeoutSeconds));

        while (true)
        {
            attempt++;
            partTimeout.CancelAfter(TimeSpan.FromSeconds(_opt.TaskTimeoutSeconds));
            // Kill switch: never start a call beyond what is reserved. A big build tops its reservation up while credits last.
            if (credits.CreditsForCost(task.CostUsd) >= reservation)
            {
                var added = task.IsFreeGrant ? 0 : await credits.ExtendAsync(task.UserId, task.Id, topUp, minTopUp);
                if (added == 0)
                {
                    logger.LogInformation("Task {TaskId} stopped at its credit reservation ({Reserved})", task.Id, reservation);
                    DropUnfinishedPart();
                    stop = BuildStop.OutOfCredits;
                    break;
                }
                reservation += added;
            }

            stream.BeginAttempt(attempt - readRounds, part);
            AiCallResult result;
            try
            {
                result = await ai.CompleteAsync(task.Tier, new ChatRequest(messages, _opt.MaxOutputTokens, cacheKey),
                    new AiCallContext(task.UserId, project.Id, task.Id, task.Kind, plan.IsPro ? PlanKeys.Pro : PlanKeys.Free, attempt, part),
                    partTimeout.Token, stream);
            }
            catch (Exception ex) when (completedParts > 0 && !ct.IsCancellationRequested)
            {
                // Keep the parts already finished instead of failing the whole request.
                logger.LogWarning(ex, "Task {TaskId} interrupted in part {Part}; keeping {Parts} finished parts", task.Id, part, completedParts);
                DropUnfinishedPart();
                stop = BuildStop.Interrupted;
                break;
            }
            task.CostUsd += result.CostUsd;
            task.ModelUsed = result.Model;
            await db.SaveChangesAsync(ct);

            var partDone = false;
            var parsed = EditApplier.Parse(result.Content);
            if (parsed is null)
            {
                await ai.InvalidateAsync(result.CacheKey);
                remainingErrors = ["Your reply was not valid JSON in the required format."];
                messages.Add(new ChatMessageDto("assistant", Truncate(result.Content, 2000)));
                messages.Add(new ChatMessageDto("user", "Your reply was not valid JSON (if it was cut off, it was too long: do less in this reply and put the rest in \"remaining\"). Reply again with ONLY the JSON object in the required format."));
            }
            else if (QuestionJson(parsed) is { } questionsJson && (parsed.Files?.Count ?? 0) == 0 && !anyChange)
            {
                var ask = string.IsNullOrWhiteSpace(parsed.Summary) ? "محتاج أعرف التفاصيل دي قبل ما أكمّل." : parsed.Summary.Trim();
                await CompleteAsync(task, project, null, ask, plan, questionsJson);
                return;
            }
            else if (parsed.Read is { Count: > 0 } && (parsed.Files?.Count ?? 0) == 0 && readRounds < MaxReadRounds)
            {
                readRounds++;
                messages.Add(new ChatMessageDto("assistant", Truncate(result.Content, 2000)));
                messages.Add(new ChatMessageDto("user", PromptBuilder.ReadReply(parsed.Read, files, _opt.ContextBytesLimit, readRounds >= MaxReadRounds)));
                continue;
            }
            else if ((parsed.Files?.Count ?? 0) == 0 && readRounds == 0 && !anyChange && PromptBuilder.HasSiteMap(messages)
                     && !(parsed.Summary ?? "").Contains('?') && !(parsed.Summary ?? "").Contains('؟')
                     && !UserAsked(task.Prompt))
            {
                // On a big site, "no edits" without a question usually means the model skipped reading the file it needs.
                readRounds++;
                messages.Add(new ChatMessageDto("assistant", Truncate(result.Content, 2000)));
                messages.Add(new ChatMessageDto("user", PromptBuilder.NoEditsNudge));
                continue;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(parsed.Thinking))
                {
                    var idea = Truncate(parsed.Thinking.Trim(), 800);
                    if (thoughts.Count == 0 || thoughts[^1] != idea) thoughts.Add(idea);
                }
                if (!string.IsNullOrWhiteSpace(parsed.Summary)) summary = parsed.Summary.Trim();
                remaining = string.IsNullOrWhiteSpace(parsed.Remaining) ? null : parsed.Remaining.Trim();
                var applied = EditApplier.Apply(files, parsed, constraints);
                files = applied.Files;
                changed.UnionWith(applied.Changed);
                partChanged |= applied.Changed.Count > 0;
                anyChange |= applied.Changed.Count > 0;

                var newValidation = SiteValidator.Validate(files, singlePage)
                    .Where(e => !baselineErrors.Contains(e.Error))
                    .Where(e => remaining is null || !SiteValidator.IsMissingPage(e.Error))
                    .ToList();
                if (isNewSite)
                {
                    IEnumerable<string> scope = string.IsNullOrWhiteSpace(parsed.Remaining) ? files.Keys : applied.Changed;
                    newValidation.AddRange(SiteValidator.ReactShellErrors(files, scope).Select(e => (Path: "", Error: e)));
                }
                remainingErrors = applied.Errors.Concat(newValidation.Select(e => e.Error)).ToList();

                if (applied.LimitReached)
                {
                    stop = BuildStop.Limit;
                    partDone = true;
                }
                else if (remainingErrors.Count == 0 || (parsed.Files?.Count ?? 0) == 0) partDone = true;
                else
                {
                    if (!partChanged) await ai.InvalidateAsync(result.CacheKey);
                    messages.Add(new ChatMessageDto("assistant", Truncate(result.Content, 6000)));
                    messages.Add(new ChatMessageDto("user", PromptBuilder.FeedbackMessage(remainingErrors, files,
                        applied.Affected.Concat(newValidation.Select(e => e.Path)))));
                }
            }

            // Keep asking the model until this part is valid. Credits, not an attempt cap, stop the loop.
            if (!partDone) continue;
            if (!partChanged) break;

            if (summary is not null) done.Add(summary);
            summary = null;
            completedParts = part;
            partChanged = false;
            good = (files, remaining, new HashSet<string>(changed, StringComparer.Ordinal));
            if (stop != BuildStop.None || remaining is null) break;
            if (part >= maxParts)
            {
                stop = BuildStop.MaxParts;
                break;
            }

            // Checkpoint the finished part before starting the next one.
            task.Parts = part;
            task.CheckpointJson = JsonSerializer.Serialize(new BuildCheckpoint(part, done, remaining, [.. changed], files));
            await db.SaveChangesAsync(ct);

            part++;
            attempt = 0;
            readRounds = 0;
            partTimeout.CancelAfter(TimeSpan.FromSeconds(_opt.TaskTimeoutSeconds));
            messages = Prompt(new BuildProgress(part, done, remaining));
        }
        // A single-part request that ran out of fix rounds or credits keeps its applied edits (with a note).
        if (partChanged && summary is not null) done.Add(summary);
        var parts = partChanged ? part : completedParts;

        if (!anyChange)
        {
            if (stop == BuildStop.OutOfCredits)
                throw new ApiException(402, "رصيدك من النقاط انتهى قبل تنفيذ الطلب. اشحن رصيدك ثم أعد المحاولة.");
            if (stop == BuildStop.Limit) throw new ApiException(422, LimitMessage(constraints));
            if (!string.IsNullOrWhiteSpace(summary) && remainingErrors.Count == 0)
            {
                // The model asked a clarifying question instead of editing.
                await CompleteAsync(task, project, null, summary!, plan);
                return;
            }
            throw new ApiException(422, "لم أتمكن من تطبيق التعديل المطلوب. جرّب صياغة الطلب بشكل أوضح أو أصغر.");
        }

        // A later part that produced nothing still leaves the build resumable.
        if (stop == BuildStop.None && remaining is not null) stop = BuildStop.Interrupted;
        await FinishAsync(task, project, plan, files, done, remaining, stop, parts, changed, remainingErrors.Count > 0, thoughts);
    }

    private static string WithThoughts(string message, IReadOnlyList<string> thoughts)
    {
        if (thoughts.Count == 0) return message;
        var steps = string.Join("\n\n", thoughts.Select((t, i) => $"التفكير {i + 1}: {t}"));
        return steps + "\n\n" + message;
    }

    /// <summary>The request the model works on: continuations ("كمل") use the request that started the build.</summary>
    private async Task<string> RequestAsync(AgentTask task)
    {
        var current = task;
        for (var i = 0; i < 50 && current.ContinuesTaskId is { } parentId; i++)
        {
            var parent = await db.AgentTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parentId);
            if (parent is null) break;
            current = parent;
        }
        return current.Prompt;
    }

    /// <summary>Completes a task from the parts saved before it was interrupted. False when no part had finished.</summary>
    private async Task<bool> FinishFromCheckpointAsync(Guid taskId)
    {
        db.ChangeTracker.Clear();
        var task = await db.AgentTasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task is not { Status: TaskStatuses.Running, CheckpointJson: not null }) return false;
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId);
        var checkpoint = JsonSerializer.Deserialize<BuildCheckpoint>(task.CheckpointJson);
        if (project is null || checkpoint is null) return false;

        logger.LogWarning("Task {TaskId} stopped during part {Part}; keeping {Parts} finished parts", task.Id, checkpoint.Part + 1, checkpoint.Part);
        var plan = await subscriptions.GetPlanAsync(task.UserId);
        await FinishAsync(task, project, plan, new SortedDictionary<string, string>(checkpoint.Files, StringComparer.Ordinal), checkpoint.Done,
            checkpoint.Remaining, BuildStop.Interrupted, checkpoint.Part, checkpoint.Changed, false);
        return true;
    }

    private async Task FinishAsync(AgentTask task, Project project, PlanInfo plan, SortedDictionary<string, string> files, List<string> done,
        string? remaining, BuildStop stop, int parts, IReadOnlyCollection<string> changed, bool hadErrors, IReadOnlyList<string>? thoughts = null)
    {
        files = SiteFiles.ReplaceTokens(files, project.Name, project.Description);
        if (task.Kind == TaskKinds.Generate && project.Name == ProjectService.AutoName(project.Description)
            && ProjectService.NameFromTitle(files) is { } brand)
            project.Name = brand;
        var resumable = remaining is not null && stop is BuildStop.MaxParts or BuildStop.OutOfCredits or BuildStop.Interrupted;
        var request = ProjectService.WithoutImagesNote(await RequestAsync(task));

        var number = (await db.ProjectVersions.Where(v => v.ProjectId == project.Id).MaxAsync(v => (int?)v.Number) ?? 0) + 1;
        var version = new ProjectVersion
        {
            ProjectId = project.Id,
            Number = number,
            FilesJson = SiteFiles.Serialize(files),
            Summary = Truncate(done.Count > 0 ? string.Join(" ", done) : "تم التعديل", 500),
            TaskId = task.Id,
            Prompt = Truncate(task.ContinuesTaskId is null ? request : $"كمل: {request}", 1000),
            Model = task.ModelUsed,
            Parts = Math.Max(1, parts),
            FilesChanged = changed.Count,
            CreatedBy = task.UserId
        };
        db.ProjectVersions.Add(version);
        project.CurrentVersionId = version.Id;
        project.UpdatedAt = DateTime.UtcNow;

        task.Parts = Math.Max(1, parts);
        task.Remaining = resumable ? Truncate(remaining!, 2000) : null;
        task.CheckpointJson = null;

        var pagesBuilt = Math.Max(0, files.Keys.Count(SiteFiles.IsHtml) - task.PagesBefore);
        var message = WithThoughts(BuildMessage(done, stop, resumable ? remaining : null, pagesBuilt, hadErrors, Constraints(plan)), thoughts ?? []);
        if (!resumable && await ShouldExplainDashboardAsync(task, files))
        {
            var email = await db.Users.AsNoTracking().Where(u => u.Id == task.UserId).Select(u => u.Email).FirstOrDefaultAsync();
            message += "\n\n" + OwnerGuide(email, HostingService.Features(files));
        }
        await CompleteAsync(task, project, version.Id, message, plan);
        await PruneVersionsAsync(project);
    }

    [GeneratedRegex(@"^\D{0,3}?(\d{1,4})(?!\d)")] private static partial Regex LeadingNumber();

    /// <summary>Pages left according to "remaining", which the model starts with a count ("14 صفحة متبقية: ...").</summary>
    public static int PagesLeft(string? remaining) =>
        remaining is not null && LeadingNumber().Match(remaining.Trim()) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>
    /// The chat reply for a finished build. A build that stops early (part limit, credits, interruption) reads as progress,
    /// with what is built and what is left, and the "كمل البناء" button (or typing «كمل») picks it up.
    /// </summary>
    public static string BuildMessage(IReadOnlyList<string> done, BuildStop stop, string? resumable, int pagesBuilt, bool hadErrors, EditConstraints constraints)
    {
        var message = done.Count switch
        {
            0 => "تم تنفيذ التعديل",
            1 => done[0],
            _ => string.Join("\n\n", done.Select((s, i) => $"الجزء {i + 1}: {s}"))
        };
        if (resumable is not null)
        {
            var left = PagesLeft(resumable);
            var progress = pagesBuilt > 0 && left > 0 ? $"تم إنشاء {pagesBuilt} من {pagesBuilt + left} صفحة، وتبقى {left}."
                : pagesBuilt > 0 ? $"تم إنشاء {pagesBuilt} صفحة حتى الآن."
                : null;
            var reason = stop switch
            {
                BuildStop.MaxParts => "الطلب كبير فأبنيه على دفعات، وهذه الدفعة اكتملت.",
                BuildStop.OutOfCredits => "توقفت لأن رصيدك من النقاط انتهى، وكل ما تم بناؤه محفوظ. اشحن رصيدك ثم أكمل.",
                _ => "توقفت مؤقتاً هنا، وكل ما تم بناؤه محفوظ."
            };
            var lines = new[] { progress, reason, $"المتبقي: {resumable}", "اضغط «كمل البناء» أو اكتب «كمل» وسأكمل من حيث توقفت." };
            return message + "\n\n" + string.Join("\n", lines.Where(l => l is not null));
        }
        if (stop == BuildStop.Limit) return message + "\n\n" + LimitMessage(constraints);
        return hadErrors ? message + "\n\n(تم تنفيذ معظم الطلب، وقد تحتاج بعض التفاصيل لطلب إضافي.)" : message;
    }

    /// <summary>Once, at the end of a finished build that needs a backend (not on every later edit).</summary>
    private async Task<bool> ShouldExplainDashboardAsync(AgentTask task, IReadOnlyDictionary<string, string> files)
    {
        if (HostingService.RequiredTier(files) != HostingTiers.Backend) return false;
        return !await db.ChatMessages.AnyAsync(m => m.ProjectId == task.ProjectId && m.Role == "assistant" && m.Content.Contains("حساب الأدمن"));
    }

    /// <summary>Tells the owner which Casco account opens the dashboard, and how to change products or ads.</summary>
    public static string OwnerGuide(string? email, IReadOnlyCollection<string> features)
    {
        var account = string.IsNullOrWhiteSpace(email)
            ? "حساب الأدمن هو نفس حسابك في Casco."
            : $"حساب الأدمن هو حسابك في Casco بالبريد {email}. ادخل بنفس طريقة دخولك إلى Casco، ولا يوجد حساب أو كلمة مرور منفصلة للوحة الموقع.";
        var lines = new List<string>
        {
            account,
            "للدخول إلى لوحة التحكم: من أعلى المحرر اضغط «البيانات والإعدادات»، أو من صفحة مواقعي اضغط «البيانات» بجانب هذا الموقع."
        };
        if (features.Contains("store"))
            lines.Add("لتعديل المنتجات: داخل اللوحة افتح تبويب «المتجر»، ثم «+ منتج جديد» أو «تعديل» على منتج موجود. غيّر الاسم والسعر والصور والمخزون، واترك «ظاهر في المتجر» مفعّلاً ليظهر للزوار.");
        if (features.Contains("ads"))
            lines.Add("لتعديل الإعلانات: داخل اللوحة افتح تبويب «الإعلانات». الزوار يضيفون إعلاناتهم بعد تسجيل الدخول في الموقع، وأنت من اللوحة تنشر الإعلان أو ترفضه أو تحذفه. من تبويب «الإعدادات» يمكنك تفعيل «مراجعة الإعلانات قبل نشرها».");
        if (features.Contains("bookings"))
            lines.Add("للحجوزات: افتح تبويب «الحجوزات» لإضافة الخدمات ومتابعة المواعيد وتغيير حالتها.");
        if (features.Contains("courses"))
            lines.Add("للكورسات: افتح تبويب «الكورسات» لإضافة الكورس والدروس، وتبويب «الاشتراكات» لقبول الطلاب.");
        if (features.Contains("db"))
            lines.Add("لبيانات الموقع مثل التقييمات أو التسجيلات: افتح تبويب «البيانات» لإضافة الصفوف وتعديلها وحذفها.");
        return string.Join("\n", lines);
    }

    private static string LimitMessage(EditConstraints c) =>
        $"وصل موقعك للحد الأقصى ({c.MaxFiles} ملف أو {c.MaxTotalBytes / 1_000_000m:0.#} ميجابايت)، لذلك لم أستطع إضافة المزيد. " +
        "احذف صفحات لا تحتاجها أو اطلب دمج الصفحات المتشابهة في صفحة واحدة، ثم أكمل.";

    /// <summary>Each version is a full copy of the site: keep the newest ones plus whatever is live or current.</summary>
    private async Task PruneVersionsAsync(Project project)
    {
        var cutoff = await db.ProjectVersions.Where(v => v.ProjectId == project.Id).OrderByDescending(v => v.Number)
            .Skip(Math.Max(5, _opt.KeepVersions)).Select(v => (int?)v.Number).FirstOrDefaultAsync();
        if (cutoff is null) return;
        var keep = new[] { project.CurrentVersionId, project.PublishedVersionId }.Where(v => v is not null).Select(v => v!.Value).ToList();
        await db.ProjectVersions.Where(v => v.ProjectId == project.Id && v.Number <= cutoff && !keep.Contains(v.Id)).ExecuteDeleteAsync();
    }

    private async Task CompleteAsync(AgentTask task, Project project, Guid? versionId, string message, PlanInfo plan, string? questionsJson = null)
    {
        var charged = 0;
        if (task.IsFreeGrant)
        {
            await credits.ReleaseAsync(task.Id, ReleaseReasons.Free);
            var user = await db.Users.FindAsync(task.UserId);
            if (user is not null) user.FreeSiteUsed = true;
        }
        else
        {
            charged = credits.CreditsForCost(task.CostUsd);
            await credits.SettleAsync(task.UserId, task.Id, charged);
        }

        task.Status = TaskStatuses.Succeeded;
        task.CreditsCharged = charged;
        task.ResultVersionId = versionId;
        task.CompletedAt = DateTime.UtcNow;

        if (!plan.IsPro && task.Kind == TaskKinds.Generate && versionId is not null)
        {
            var b = billing.Value;
            message += $"\n\nموقعك جاهز للمعاينة! لنشره على سيرفرات Casco فعّل الاستضافة ({BillingOptions.Dollars(b.Hosting.StaticMonthlyMinor)} شهرياً، " +
                       $"أو {BillingOptions.Dollars(b.Hosting.BackendMonthlyMinor)} لو الموقع فيه باك إند مثل الحسابات أو المتجر أو الحجوزات). " +
                       $"وللتعديل عليه اشترك في Casco Pro (من {BillingOptions.Dollars(b.Pro.MonthlyPriceMinor)} شهرياً).";
            if (task.IsFreeGrant)
                message += "\n\nالطلب المجاني خلّص. للشحن ادخل بنفس حساب جوجل أو آبل من غير ما تلصق أي توكن، من صفحة الفوترة: " +
                           $"فودافون كاش أو إنستا باي {WalletPay.AmountMinor / 100} جنيه على {WalletPay.Phone} مع صورة التحويل، أو ادفع بالدولار من بوابة الدفع.";
        }

        db.ChatMessages.Add(new ChatMessage { ProjectId = project.Id, Role = "assistant", Content = message, TaskId = task.Id, Credits = charged, QuestionsJson = questionsJson });
        await db.SaveChangesAsync();
        SiteContext.Invalidate(cache, project.SiteKey);
    }

    private async Task FailAsync(AgentTask task, string error, string reason = ReleaseReasons.Failed)
    {
        db.ChangeTracker.Clear();
        var fresh = await db.AgentTasks.FindAsync(task.Id);
        if (fresh is null) return;
        fresh.Status = TaskStatuses.Failed;
        fresh.Error = error;
        fresh.CostUsd = Math.Max(fresh.CostUsd, task.CostUsd);
        fresh.ModelUsed = task.ModelUsed ?? fresh.ModelUsed;
        fresh.CheckpointJson = null;
        fresh.CompletedAt = DateTime.UtcNow;
        var charged = !fresh.IsFreeGrant && fresh.CostUsd > 0 ? credits.CreditsForCost(fresh.CostUsd) : 0;
        fresh.CreditsCharged = charged;
        var hint = fresh.Kind == TaskKinds.Generate
            ? "\n\nلا تقلق، موقعك الأساسي جاهز من القالب ويمكنك طلب التعديلات عليه."
            : "";
        db.ChatMessages.Add(new ChatMessage { ProjectId = fresh.ProjectId, Role = "assistant", Content = $"⚠️ {error}{hint}", TaskId = fresh.Id, Credits = charged });
        await db.SaveChangesAsync();
        if (charged > 0) await credits.SettleAsync(fresh.UserId, fresh.Id, charged);
        else await credits.ReleaseAsync(task.Id, reason);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static bool UserAsked(string prompt)
    {
        var text = prompt.Trim();
        if (text.Contains('?') || text.Contains('؟')) return true;
        var start = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('«', '"', '\'') ?? "";
        return start is "مين" or "ايه" or "إيه" or "ليه" or "ازاي" or "إزاي" or "هل" or "كيف" or "لماذا" or "ما" or "ايش" or "وش"
            or "what" or "who" or "why" or "how" or "when" or "where";
    }

    /// <summary>One recommended option per question. Null when the model did not ask anything usable.</summary>
    private static string? QuestionJson(AiEditResponse parsed)
    {
        var questions = new List<QuestionDto>();
        foreach (var question in parsed.Questions ?? [])
        {
            var prompt = (question.Prompt ?? "").Trim();
            var options = (question.Options ?? [])
                .Select(o => new QuestionOptionDto((o.Label ?? "").Trim(), o.Recommended))
                .Where(o => o.Label.Length > 0)
                .Take(4)
                .ToList();
            if (prompt.Length == 0 || options.Count < 2) continue;
            var recommended = false;
            for (var i = 0; i < options.Count; i++)
            {
                if (!options[i].Recommended || recommended)
                {
                    if (options[i].Recommended) options[i] = options[i] with { Recommended = false };
                    continue;
                }
                recommended = true;
            }
            if (!recommended) options[0] = options[0] with { Recommended = true };
            questions.Add(new QuestionDto(prompt, options));
            if (questions.Count == 3) break;
        }
        return questions.Count == 0 ? null : JsonSerializer.Serialize(questions, QuestionJsonOpts);
    }

    private static readonly JsonSerializerOptions QuestionJsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private record QuestionOptionDto(string Label, bool Recommended);
    private record QuestionDto(string Prompt, List<QuestionOptionDto> Options);
}
