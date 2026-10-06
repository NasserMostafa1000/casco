using System.Text.Json;
using System.Text.RegularExpressions;
using Casco.Api.Domain;
using Casco.Api.Features.Agent;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Projects;

public partial class ProjectService(
    AppDbContext db,
    TemplateCatalog templates,
    SubscriptionService subscriptions,
    CreditService credits,
    AgentQueue queue,
    IOptions<AgentOptions> agentOptions,
    IOptions<BillingOptions> billing,
    ILogger<ProjectService> logger)
{
    /// <summary>The interface language sent with the request, so thinking, replies, and new sites use it.</summary>
    public async Task RememberLanguageAsync(Guid userId, string? lang)
    {
        if (lang is not ("ar" or "en" or "hi")) return;
        var user = await db.Users.FindAsync(userId);
        if (user is null || user.Locale == lang) return;
        user.Locale = lang;
        await db.SaveChangesAsync();
    }

    public async Task<Project> GetOwnedAsync(Guid projectId, Guid userId) =>
        await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId)
        ?? throw ApiException.NotFound("المشروع غير موجود");

    /// <summary>
    /// Creates the project with version 1 instantiated from the template, so the user always has a working website,
    /// then queues the AI customization. Free users get their first AI generation at no credit cost.
    /// </summary>
    public async Task<(Project Project, Guid? TaskId, string? Notice)> CreateAsync(Guid userId, string name, string description, string? templateKey, string? language = null, bool generate = true)
    {
        description = Text.Truncate(description?.Trim(), 2000);
        if (description.Length < 5) throw ApiException.BadRequest("اكتب وصفاً مختصراً للموقع الذي تريده");
        name = Text.Truncate(string.IsNullOrWhiteSpace(name) ? AutoName(description) : name.Trim(), 120);
        if (name.Length < 2) throw ApiException.BadRequest("اكتب اسم الموقع أو النشاط");

        var plan = await subscriptions.GetPlanAsync(userId);
        var count = await db.Projects.CountAsync(p => p.UserId == userId);
        if (count >= plan.MaxProjects)
            throw ApiException.Payment(plan.IsPro
                ? $"وصلت للحد الأقصى ({plan.MaxProjects}) من المواقع"
                : "الخطة المجانية تتيح موقعاً واحداً. اشترك في Pro لإنشاء مواقع أكثر.");

        var template = string.IsNullOrWhiteSpace(templateKey) || templateKey == "auto"
            ? templates.Detect(description + " " + name)
            : templates.Get(templateKey) ?? throw ApiException.BadRequest("القالب غير موجود");

        string? notice = null;
        if (!SubscriptionService.CanUseTemplate(plan, template))
        {
            notice = $"قالب \"{template.Name}\" متاح في Pro، لذلك جهزنا لك نسخة من صفحة واحدة. اشترك لتحصل على المنصة الكاملة.";
            template = templates.Default;
        }

        var project = new Project
        {
            UserId = userId,
            Name = name,
            Description = description,
            TemplateKey = template.Key,
            SiteKey = Text.RandomToken(12),
            Slug = await UniqueSlugAsync(name)
        };
        var files = SiteFiles.ReplaceTokens(template.Files.ToDictionary(), name, Text.Truncate(description.Split("\n\n")[0], 160));
        var version = new ProjectVersion { ProjectId = project.Id, Number = 1, FilesJson = SiteFiles.Serialize(files), Summary = $"النسخة الأولى من قالب {template.Name}", CreatedBy = userId };
        project.CurrentVersionId = version.Id;

        db.Projects.Add(project);
        db.ProjectVersions.Add(version);
        db.ChatMessages.Add(new ChatMessage { ProjectId = project.Id, Role = "user", Content = description });
        await db.SaveChangesAsync();

        var user = await db.Users.FindAsync(userId);
        var freeGrant = !plan.IsPro && user is { FreeSiteUsed: false };
        Guid? taskId = null;
        if (generate)
        {
            try
            {
                taskId = await BeginGenerateAsync(project, userId, description, language, freeGrant, images: null);
            }
            catch (ApiException ex) when (ex.Status == 402)
            {
                notice = plan.IsPro
                    ? $"موقعك جاهز من القالب. {ex.Message}"
                    : "موقعك جاهز من القالب. رصيدك لا يكفي للتخصيص بالذكاء الاصطناعي الآن، اشترك في Pro لمتابعة التعديل.";
                db.ChatMessages.Add(new ChatMessage { ProjectId = project.Id, Role = "assistant", Content = notice });
                await db.SaveChangesAsync();
            }
        }
        return (project, taskId, notice);
    }

    /// <summary>Starts the first AI build after the project (and any photos) already exist.</summary>
    public async Task<Guid> StartInitialGenerateAsync(Guid projectId, Guid userId, IReadOnlyList<string>? images, string? language = null)
    {
        var project = await GetOwnedAsync(projectId, userId);
        if (await db.AgentTasks.AnyAsync(t => t.ProjectId == project.Id && t.Kind == TaskKinds.Generate))
            throw ApiException.Conflict("بدأ بناء الموقع بالفعل");
        var plan = await subscriptions.GetPlanAsync(userId);
        var user = await db.Users.FindAsync(userId);
        var freeGrant = !plan.IsPro && user is { FreeSiteUsed: false };
        try
        {
            return await BeginGenerateAsync(project, userId, project.Description, language, freeGrant, images);
        }
        catch (ApiException ex) when (ex.Status == 402)
        {
            var notice = plan.IsPro
                ? $"موقعك جاهز من القالب. {ex.Message}"
                : "موقعك جاهز من القالب. رصيدك لا يكفي للتخصيص بالذكاء الاصطناعي الآن، اشترك في Pro لمتابعة التعديل.";
            db.ChatMessages.Add(new ChatMessage { ProjectId = project.Id, Role = "assistant", Content = notice });
            await db.SaveChangesAsync();
            throw;
        }
    }

    private async Task<Guid> BeginGenerateAsync(Project project, Guid userId, string description, string? language, bool freeGrant, IReadOnlyList<string>? images)
    {
        var prompt = SiteLanguage(language) is { } siteLanguage ? $"{description}\n\nWebsite language: {siteLanguage}" : description;
        return await StartTaskAsync(project, userId, TaskKinds.Generate, prompt, premium: false, freeGrant: freeGrant, recordUserMessage: false, images: images);
    }

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "Arabic", ["en"] = "English", ["hi"] = "Hindi", ["ur"] = "Urdu", ["fr"] = "French", ["es"] = "Spanish",
        ["de"] = "German", ["tr"] = "Turkish", ["fa"] = "Persian", ["pt"] = "Portuguese", ["it"] = "Italian", ["ru"] = "Russian",
        ["zh"] = "Chinese", ["id"] = "Indonesian", ["bn"] = "Bengali", ["he"] = "Hebrew"
    };

    /// <summary>
    /// The language the owner picked for the site: a known code ("en") or a language name typed by the user ("Swahili").
    /// Null means "detect from the description".
    /// </summary>
    public static string? SiteLanguage(string? language)
    {
        var value = language?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (LanguageNames.TryGetValue(value, out var name)) return name;
        var cleaned = new string(value.Where(c => char.IsLetter(c) || c is ' ' or '-').ToArray()).Trim();
        return cleaned.Length is > 0 and <= 40 ? cleaned : null;
    }

    private const string ImagesNote = "\n\nImages uploaded by the user for this request";

    public static string WithoutImagesNote(string prompt) =>
        prompt.IndexOf(ImagesNote, StringComparison.Ordinal) is var i and >= 0 ? prompt[..i] : prompt;

    [GeneratedRegex(@"^\s*(كمل|كمّل|أكمل|اكمل|إكمل|استمر|كمل البناء|continue)\s*[.!؟?]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueWords();

    public static bool IsContinueRequest(string? text) => text is not null && ContinueWords().IsMatch(text);

    public async Task<Guid> StartTaskAsync(Project project, Guid userId, string kind, string prompt, bool premium, bool freeGrant = false,
        bool recordUserMessage = true, IReadOnlyList<string>? images = null, bool resume = false, string? chatMessage = null)
    {
        prompt = Text.Truncate(prompt?.Trim(), 4000);
        images ??= [];
        if (resume && prompt.Length < 2) prompt = "كمل البناء";
        if (prompt.Length < 2 && images.Count > 0) prompt = "أضف هذه الصور إلى الموقع في المكان المناسب";
        if (prompt.Length < 2) throw ApiException.BadRequest("اكتب طلبك");
        var userMessage = prompt;
        if (images.Count > 0)
            prompt += ImagesNote + " (already hosted, use these exact URLs: photos in <img src> or CSS backgrounds, .mp4/.webm/.mov videos in <video src controls playsinline>):\n"
                      + string.Join("\n", images.Select(u => "- " + u));

        if (await db.AgentTasks.AnyAsync(t => t.ProjectId == project.Id && (t.Status == TaskStatuses.Queued || t.Status == TaskStatuses.Running)))
            throw ApiException.Conflict("هناك طلب قيد التنفيذ على هذا الموقع، انتظر حتى ينتهي", "task_running");

        // The "كمل البناء" button, or typing «كمل», continues the site's unfinished big build.
        AgentTask? unfinished = null;
        if (kind == TaskKinds.Edit && images.Count == 0 && (resume || IsContinueRequest(prompt)))
        {
            unfinished = await db.AgentTasks.Where(t => t.ProjectId == project.Id).OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync();
            if (unfinished is not { Status: TaskStatuses.Succeeded, Remaining: not null }) unfinished = null;
            if (unfinished is null && resume) throw ApiException.BadRequest("لا يوجد بناء غير مكتمل لإكماله على هذا الموقع");
        }

        var plan = await subscriptions.GetPlanAsync(userId);
        if (!plan.IsPro)
        {
            if (unfinished is not null)
                freeGrant = unfinished.IsFreeGrant;
            else
            {
                var usedPrompts = await db.AgentTasks.CountAsync(t => t.UserId == userId && t.ContinuesTaskId == null);
                var bonus = await db.Users.Where(u => u.Id == userId && u.ShareRewardClaimed).AnyAsync() ? ShareReward.BonusPrompts : 0;
                if (usedPrompts >= ShareReward.FreePrompts + bonus)
                    throw ApiException.Payment("الطلب المجاني اتستخدم. سجّل الدخول بنفس حساب جوجل أو آبل واشحن حسابك من صفحة الفوترة.", "free_prompt_limit");
                freeGrant = true;
            }
        }
        if (premium && !plan.CanUsePremium) throw ApiException.Payment("الوضع القوي متاح لمشتركي Pro فقط");
        var tier = premium ? AiTiers.Premium : plan.DefaultTier;

        var task = new AgentTask
        {
            ProjectId = project.Id,
            UserId = userId,
            Kind = kind,
            Prompt = prompt,
            Tier = tier,
            IsFreeGrant = freeGrant,
            ContinuesTaskId = unfinished?.Id,
            PagesBefore = unfinished?.PagesBefore ?? 0
        };

        if (!freeGrant)
        {
            var agent = agentOptions.Value;
            var hold = agent.ReserveCredits.GetValueOrDefault(tier, 50);
            try
            {
                await credits.ReserveAsync(userId, task.Id, hold, agent.MinCreditsToStart.GetValueOrDefault(tier, hold));
            }
            catch (ApiException ex) when (ex.Details is CreditShortfall shortfall)
            {
                throw OutOfCredits(plan, premium, shortfall);
            }
        }

        db.AgentTasks.Add(task);
        if (recordUserMessage)
            db.ChatMessages.Add(new ChatMessage
            {
                ProjectId = project.Id, Role = "user", Content = string.IsNullOrWhiteSpace(chatMessage) ? userMessage : chatMessage, TaskId = task.Id,
                ImagesJson = images.Count > 0 ? JsonSerializer.Serialize(images) : null
            });
        else if (images.Count > 0)
        {
            var last = await db.ChatMessages.Where(m => m.ProjectId == project.Id && m.Role == "user").OrderByDescending(m => m.Id).FirstOrDefaultAsync();
            if (last is not null) last.ImagesJson = JsonSerializer.Serialize(images);
        }
        await db.SaveChangesAsync();
        await queue.EnqueueAsync(task.Id);
        return task.Id;
    }

    /// <summary>Pro subscribers who ran out are told to top up or wait for the refill, never to subscribe again.</summary>
    private ApiException OutOfCredits(PlanInfo plan, bool premium, CreditShortfall s)
    {
        var standardMin = agentOptions.Value.MinCreditsToStart.GetValueOrDefault(AiTiers.Standard, 10);
        var tryStandard = premium && plan.IsPro && s.Available >= standardMin;
        string message;
        if (tryStandard)
            message = $"الوضع القوي يحتاج {s.Needed} نقطة على الأقل ورصيدك {s.Available}. أرسل طلبك بالوضع العادي أو اشحن رصيدك.";
        else if (plan.IsPro)
        {
            var left = s.Available > 0 ? $" (متبقي {s.Available})" : "";
            var refill = plan.CreditsRefillAt is not { } at ? ""
                : at == plan.PeriodEnd
                    ? $"، أو تحصل على {billing.Value.Pro.MonthlyCredits} نقطة جديدة عند تجديد اشتراكك ({at:yyyy-MM-dd})"
                    : $"، أو انتظر تجديد نقاطك تلقائياً يوم {at:yyyy-MM-dd}";
            message = $"خلصت نقاط هذا الشهر{left}. اشحن رصيدك وكمّل فوراً (نقاط الشحن لا تنتهي){refill}.";
        }
        else
            message = "رصيدك من النقاط لا يكفي. اشترك في Casco Pro أو اشحن رصيدك.";

        return new ApiException(402, message, "insufficient_credits")
        {
            Details = new { available = s.Available, needed = s.Needed, isPro = plan.IsPro, tryStandard, refillAt = plan.CreditsRefillAt }
        };
    }

    /// <summary>Placeholder name when the user only describes the site: the first few words of the description.
    /// Replaced by the generated site's title after the first build (see <see cref="NameFromTitle"/>).</summary>
    public static string AutoName(string description)
    {
        var firstLine = description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var words = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5);
        var name = string.Join(' ', words).Trim(' ', '.', '،', ',', ':', '؛', ';', '!', '؟', '?', '-');
        if (name.Length > 40) name = name[..40].TrimEnd();
        return name.Length >= 2 ? name : "موقعي";
    }

    /// <summary>The brand from index.html's title ("Grill House | Best grills" → "Grill House"), or null.</summary>
    public static string? NameFromTitle(IReadOnlyDictionary<string, string> files)
    {
        if (!files.TryGetValue("index.html", out var html)) return null;
        var m = TitleTag().Match(html);
        if (!m.Success) return null;
        var title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        var brand = TitleSeparators().Split(title).Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0) ?? "";
        return brand.Length is >= 2 and <= 60 && !brand.Contains("{{") ? brand : null;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"\s+[|–—\-:·•]\s+|\s*\|\s*")]
    private static partial Regex TitleSeparators();

    /// <summary>An empty React hosting slot. It goes online only after the yearly fee and a dist upload.</summary>
    public async Task<Project> CreateReactAsync(Guid userId, string name)
    {
        name = Text.Truncate(name?.Trim(), 120);
        if (name.Length < 2) throw ApiException.BadRequest("اكتب اسم التطبيق");
        var plan = await subscriptions.GetPlanAsync(userId);
        var count = await db.Projects.CountAsync(p => p.UserId == userId);
        if (count >= plan.MaxProjects)
            throw ApiException.Payment(plan.IsPro
                ? $"وصلت للحد الأقصى ({plan.MaxProjects}) من المواقع"
                : "الخطة المجانية تتيح موقعاً واحداً. اشترك في Pro لإنشاء مواقع أكثر.");

        var project = new Project
        {
            UserId = userId,
            Name = name,
            Description = "تطبيق React مرفوع",
            TemplateKey = HostingTiers.React,
            SiteKey = Text.RandomToken(12),
            Slug = await UniqueSlugAsync(name)
        };
        var version = new ProjectVersion
        {
            ProjectId = project.Id,
            Number = 1,
            FilesJson = SiteFiles.Serialize(new Dictionary<string, string> { ["index.html"] = "<!doctype html><title>React</title>" }),
            Summary = "تطبيق React",
            CreatedBy = userId
        };
        project.CurrentVersionId = version.Id;
        db.Projects.Add(project);
        db.ProjectVersions.Add(version);
        await db.SaveChangesAsync();
        return project;
    }

    public async Task<string> UniqueSlugAsync(string name)
    {
        var baseSlug = Text.Slugify(name);
        if (!Text.IsValidSlug(baseSlug)) baseSlug = "site";
        for (var i = 0; i < 20; i++)
        {
            var candidate = i == 0 && baseSlug != "site" ? baseSlug : $"{baseSlug}-{Text.RandomToken(3)}";
            if (!await db.Projects.AnyAsync(p => p.Slug == candidate)) return candidate;
        }
        return $"site-{Text.RandomToken(6)}";
    }

    public async Task DeleteAsync(Project project, string dataPath)
    {
        await db.AgentTasks.Where(t => t.ProjectId == project.Id && (t.Status == TaskStatuses.Queued || t.Status == TaskStatuses.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatuses.Failed));
        var taskIds = await db.AgentTasks.Where(t => t.ProjectId == project.Id).Select(t => t.Id).ToListAsync();
        await db.CreditReservations.Where(r => taskIds.Contains(r.TaskId)).ExecuteDeleteAsync();

        var courseIds = await db.Courses.Where(c => c.ProjectId == project.Id).Select(c => c.Id).ToListAsync();
        await db.Enrollments.Where(e => courseIds.Contains(e.CourseId)).ExecuteDeleteAsync();
        await db.Lessons.Where(l => courseIds.Contains(l.CourseId)).ExecuteDeleteAsync();
        await db.Courses.Where(c => c.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.Ads.Where(a => a.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.SiteUsers.Where(u => u.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.FormSubmissions.Where(f => f.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.SiteRecords.Where(r => r.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.Orders.Where(o => o.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.Products.Where(p => p.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.Bookings.Where(b => b.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.BookingServices.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.ChatMessages.Where(m => m.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.ProjectVersions.Where(v => v.ProjectId == project.Id).ExecuteDeleteAsync();
        await db.AiUsages.Where(u => u.ProjectId == project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ProjectId, (Guid?)null).SetProperty(u => u.TaskId, (Guid?)null));
        await db.SentNotifications.Where(n => n.Key.StartsWith("hosting:" + project.Id)).ExecuteDeleteAsync();
        await db.AgentTasks.Where(t => t.ProjectId == project.Id).ExecuteDeleteAsync();
        db.Projects.Remove(project);
        await db.SaveChangesAsync();

        var id = project.Id.ToString("N");
        var sitesRoot = Path.Combine(dataPath, "sites");
        await DirectoryCleanup.DeleteAsync(Path.Combine(sitesRoot, id), logger);
        if (Directory.Exists(sitesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(sitesRoot))
            {
                if (Path.GetFileName(dir).Contains(id, StringComparison.OrdinalIgnoreCase))
                    await DirectoryCleanup.DeleteAsync(dir, logger);
            }
        }
    }
}
