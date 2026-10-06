using Casco.Api.Domain;
using Casco.Api.Features.Agent;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Publishing;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casco.Tests;

public class AgentTests
{
    private static readonly EditConstraints Pro = new(false, 30, 200_000, 1_000_000);
    private static readonly EditConstraints Free = new(true, 30, 200_000, 1_000_000);

    private static Dictionary<string, string> Site() => new()
    {
        ["index.html"] = "<html><head><title>X</title></head>\n<body>\n    <h1 class=\"a\">Hello</h1>\n</body></html>"
    };

    [Fact]
    public void Parses_json_inside_code_fences()
    {
        var parsed = EditApplier.Parse("```json\n{\"summary\":\"ok\",\"files\":[]}\n```");
        Assert.NotNull(parsed);
        Assert.Equal("ok", parsed!.Summary);
        Assert.Null(EditApplier.Parse("not json"));
    }

    [Fact]
    public void Applies_exact_and_whitespace_tolerant_edits()
    {
        var response = new AiEditResponse
        {
            Files = [new AiFileOp { Path = "index.html", Op = "edit", Edits =
            [
                new AiEdit { Find = "<title>X</title>", Replace = "<title>Y</title>" },
                new AiEdit { Find = "<h1  class=\"a\">\n Hello</h1>", Replace = "<h1>مرحبا</h1>" }
            ] }]
        };
        var result = EditApplier.Apply(Site(), response, Pro);
        Assert.Empty(result.Errors);
        Assert.Contains("<title>Y</title>", result.Files["index.html"]);
        Assert.Contains("<h1>مرحبا</h1>", result.Files["index.html"]);
    }

    [Fact]
    public void Reports_missing_find_text_and_keeps_other_edits()
    {
        var response = new AiEditResponse
        {
            Files = [new AiFileOp { Path = "index.html", Op = "edit", Edits =
            [
                new AiEdit { Find = "does not exist", Replace = "x" },
                new AiEdit { Find = "<title>X</title>", Replace = "<title>Z</title>" }
            ] }]
        };
        var result = EditApplier.Apply(Site(), response, Pro);
        Assert.Single(result.Errors);
        Assert.Contains("index.html", result.Affected);
        Assert.Contains("<title>Z</title>", result.Files["index.html"]);
    }

    [Fact]
    public void Free_plan_cannot_create_extra_pages_and_paths_are_sanitized()
    {
        var response = new AiEditResponse
        {
            Files =
            [
                new AiFileOp { Path = "about.html", Op = "write", Content = "<html></html>" },
                new AiFileOp { Path = "../secret.html", Op = "write", Content = "x" },
                new AiFileOp { Path = "run.exe", Op = "write", Content = "x" },
                new AiFileOp { Path = "index.html", Op = "delete" }
            ]
        };
        var result = EditApplier.Apply(Site(), response, Free);
        Assert.Equal(4, result.Errors.Count);
        Assert.Single(result.Files);
        Assert.True(result.Files.ContainsKey("index.html"));
    }

    [Fact]
    public void Site_limits_are_reported_as_limit_reached()
    {
        var small = new EditConstraints(false, 2, 200_000, 1_000);
        var tooMany = EditApplier.Apply(Site(), new AiEditResponse
        {
            Files =
            [
                new AiFileOp { Path = "a.html", Op = "write", Content = "<html></html>" },
                new AiFileOp { Path = "b.html", Op = "write", Content = "<html></html>" }
            ]
        }, small);
        Assert.True(tooMany.LimitReached);
        Assert.True(tooMany.Files.ContainsKey("a.html"));
        Assert.False(tooMany.Files.ContainsKey("b.html"));

        var tooBig = EditApplier.Apply(Site(), new AiEditResponse
        {
            Files = [new AiFileOp { Path = "big.css", Op = "write", Content = new string('x', 2_000) }]
        }, new EditConstraints(false, 30, 200_000, 1_000));
        Assert.True(tooBig.LimitReached);
        Assert.False(tooBig.Files.ContainsKey("big.css"));

        Assert.False(EditApplier.Apply(Site(), new AiEditResponse { Files = [] }, Pro).LimitReached);
    }

    [Fact]
    public void Remaining_is_parsed_and_missing_pages_are_recognized()
    {
        var parsed = EditApplier.Parse("{\"summary\":\"s\",\"files\":[],\"remaining\":\"contact.html, blog.html\"}");
        Assert.Equal("contact.html, blog.html", parsed!.Remaining);

        var errors = SiteValidator.Validate(new Dictionary<string, string> { ["index.html"] = "<html><a href=\"blog.html\">b</a></html>" }, false);
        Assert.True(SiteValidator.IsMissingPage(Assert.Single(errors).Error));
        Assert.False(SiteValidator.IsMissingPage("index.html looks truncated (missing </html>)."));
    }

    [Fact]
    public void Continuation_prompt_carries_limits_progress_and_the_last_reply()
    {
        var longReply = "تم بناء 3 صفحات. المتبقي: " + new string('ص', 900);
        var messages = PromptBuilder.Build(Site(), new PromptContext("A", "landing", ["forms"], true, false, "اعمل 20 صفحة",
            [("user", "ابدأ"), ("assistant", longReply)], new SiteLimits(200, 5_000_000), new BuildProgress(2, ["الصفحة الرئيسية وصفحة من نحن"], "blog.html, contact.html")), 60_000);
        var request = messages[2].Content;
        Assert.Contains("1 of 200 files used", request);
        Assert.Contains("## Continue (part 2)", request);
        Assert.Contains("blog.html, contact.html", request);
        Assert.Contains(longReply, request);
        Assert.True(request.IndexOf("## Request", StringComparison.Ordinal) < request.IndexOf("## Continue", StringComparison.Ordinal));
    }

    [Fact]
    public void Once_a_shared_layout_exists_every_page_must_load_it()
    {
        const string page = "<html><body><h1>x</h1></body></html>";
        const string withLayout = "<html><body><main>x</main><script src=\"assets/layout.js\"></script></body></html>";
        var noLayout = new Dictionary<string, string> { ["index.html"] = page, ["about.html"] = page };
        Assert.Empty(SiteValidator.Validate(noLayout, false));

        var site = new Dictionary<string, string>
        {
            ["index.html"] = withLayout,
            ["about.html"] = withLayout.Replace("assets/layout.js", "./assets/layout.js"),
            ["pricing.html"] = page,
            [SiteValidator.LayoutScript] = "document.body.insertAdjacentHTML('afterbegin','<nav></nav>')"
        };
        var error = Assert.Single(SiteValidator.Validate(site, false));
        Assert.Equal("pricing.html", error.Path);
        Assert.Contains(SiteValidator.LayoutScript, error.Error);
    }

    [Theory]
    [InlineData("14 صفحة متبقية: الأسعار، المدونة", 14)]
    [InlineData("25 pages left: page-6.html, page-7.html", 25)]
    [InlineData("0 صفحات، متبقي نموذج الحجز", 0)]
    [InlineData("المدونة وصفحة الأسعار", 0)]
    public void Pages_left_are_read_from_the_start_of_remaining(string remaining, int expected) =>
        Assert.Equal(expected, AgentTaskRunner.PagesLeft(remaining));

    [Fact]
    public void Unfinished_build_reads_as_progress_with_a_continue_action()
    {
        var c = new EditConstraints(false, 200, 200_000, 5_000_000);
        var message = AgentTaskRunner.BuildMessage(["الرئيسية ومن نحن", "الخدمات والأسعار"], BuildStop.MaxParts, "14 صفحة متبقية: المدونة", 16, false, c);
        Assert.Contains("الجزء 2: الخدمات والأسعار", message);
        Assert.Contains("تم إنشاء 16 من 30 صفحة، وتبقى 14.", message);
        Assert.Contains("المتبقي: 14 صفحة متبقية: المدونة", message);
        Assert.Contains("«كمل البناء»", message);
        Assert.DoesNotContain("فشل", message);

        var credits = AgentTaskRunner.BuildMessage(["جزء"], BuildStop.OutOfCredits, "المدونة", 3, false, c);
        Assert.Contains("رصيدك", credits);
        Assert.Contains("تم إنشاء 3 صفحة حتى الآن.", credits);

        var finished = AgentTaskRunner.BuildMessage(["تم"], BuildStop.None, null, 4, false, c);
        Assert.Equal("تم", finished);
    }

    [Fact]
    public void Owner_guide_explains_the_admin_account_and_how_to_edit_products_or_ads()
    {
        var guide = AgentTaskRunner.OwnerGuide("owner@shop.test", ["store", "ads"]);
        Assert.Contains("owner@shop.test", guide);
        Assert.Contains("البيانات والإعدادات", guide);
        Assert.Contains("«المتجر»", guide);
        Assert.Contains("ظاهر في المتجر", guide);
        Assert.Contains("«الإعلانات»", guide);
        Assert.Contains("مراجعة الإعلانات قبل نشرها", guide);
        Assert.DoesNotContain("الكورسات", guide);

        var bookings = AgentTaskRunner.OwnerGuide(null, ["bookings"]);
        Assert.Contains("نفس حسابك في Casco", bookings);
        Assert.Contains("الحجوزات", bookings);
        Assert.DoesNotContain("المتجر", bookings);
    }

    [Theory]
    [InlineData("كمل", true)]
    [InlineData(" كمّل! ", true)]
    [InlineData("كمل البناء", true)]
    [InlineData("Continue", true)]
    [InlineData("كمل الصفحة الرئيسية بقسم أسعار", false)]
    [InlineData("غير لون الناف بار", false)]
    public void Continue_words_are_recognized(string text, bool expected) =>
        Assert.Equal(expected, Casco.Api.Features.Projects.ProjectService.IsContinueRequest(text));

    [Fact]
    public void Provider_retries_back_off_exponentially_with_jitter()
    {
        var random = new Random(7);
        var delays = Enumerable.Range(0, 4).Select(i => AiClient.RetryDelay(i, null, 4, random)!.Value.TotalMilliseconds).ToList();
        for (var i = 0; i < 4; i++)
        {
            var ceiling = Math.Min(8000, 1000 * Math.Pow(2, i));
            Assert.InRange(delays[i], ceiling / 2, ceiling);
        }
        Assert.Null(AiClient.RetryDelay(4, null, 4));
        Assert.Equal(TimeSpan.FromSeconds(3), AiClient.RetryDelay(0, TimeSpan.FromSeconds(3), 4));
        Assert.Null(AiClient.RetryDelay(0, TimeSpan.FromMinutes(2), 4));

        Assert.True(new AiProviderException("HTTP 429: slow down", 429).Transient);
        Assert.True(new AiProviderException("Network error: reset").Transient);
        Assert.False(new AiProviderException("HTTP 400: bad", 400).Transient);
        Assert.False(new AiProviderException("Provider timeout", billable: true).Transient);
    }

    [Fact]
    public void Validator_detects_broken_links_and_truncation()
    {
        var files = new Dictionary<string, string>
        {
            ["index.html"] = "<html><body><a href=\"about.html\">a</a><a href=\"course.html?id=1\">c</a><a href=\"https://x.com/y.html\">x</a></body></html>",
            ["course.html"] = "<html><body><script>var a=1;"
        };
        var errors = SiteValidator.Validate(files, singlePage: false).Select(e => e.Error).ToList();
        Assert.Contains(errors, e => e.Contains("about.html"));
        Assert.Contains(errors, e => e.Contains("truncated"));
        Assert.Contains(errors, e => e.Contains("unbalanced"));
        Assert.DoesNotContain(errors, e => e.Contains("x.com"));
    }

    [Fact]
    public void JavaScript_modules_must_parse_and_not_be_minified()
    {
        var module = """
            import React from "https://esm.sh/react@19";
            import htm from "https://esm.sh/htm";
            const html = htm.bind(React.createElement);
            export function Header() {
              return html`<header class="p-4">Hi</header>`;
            }
            """;
        Assert.Null(SiteValidator.JavaScriptSyntax(module));
        Assert.NotNull(SiteValidator.JavaScriptSyntax("function App(){ return html`<div> }"));
        var packed = "function App(){return html`<div class=\"p-4\">" + new string('x', 700) + "</div>`}";
        var errors = SiteValidator.Validate(new Dictionary<string, string> { ["index.html"] = "<html></html>", ["assets/app.js"] = packed }, false);
        Assert.Contains(errors, e => e.Error.Contains("too few lines"));
    }

    [Fact]
    public void Injects_sdk_before_head_and_badge_before_body()
    {
        var html = HtmlInjector.Inject("<html><head><title>t</title></head><body>hi</body></html>", "https://casco.studio/", "key1", preview: false, badge: true);
        Assert.True(html.IndexOf("/sdk/casco.js", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal));
        Assert.Contains("\"siteKey\":\"key1\"", html);
        Assert.Contains("https://casco.studio/sdk/casco.js", html);
        Assert.True(html.IndexOf("Casco</a>", StringComparison.Ordinal) < html.IndexOf("</body>", StringComparison.Ordinal));
        Assert.DoesNotContain("casco-error", html);
        Assert.DoesNotContain("noindex", html);
    }

    [Theory]
    [InlineData("متجر عطور فاخرة في دبي مع سلة وطلب بالدفع عند الاستلام", "متجر عطور فاخرة في دبي")]
    [InlineData("  موقع عيادة.\nفيه حجز مواعيد", "موقع عيادة")]
    [InlineData("?!", "موقعي")]
    public void Auto_name_is_the_first_words_of_the_description(string description, string expected) =>
        Assert.Equal(expected, Casco.Api.Features.Projects.ProjectService.AutoName(description));

    [Theory]
    [InlineData("<title>Grill House | Best grills in town</title>", "Grill House")]
    [InlineData("<title>مطعم الشام - أشهى المشويات</title>", "مطعم الشام")]
    [InlineData("<title>Tom &amp; Jerry</title>", "Tom & Jerry")]
    [InlineData("<title>{{BRAND}}</title>", null)]
    [InlineData("<p>no title</p>", null)]
    public void Project_name_comes_from_the_home_page_title(string head, string? expected) =>
        Assert.Equal(expected, Casco.Api.Features.Projects.ProjectService.NameFromTitle(
            new Dictionary<string, string> { ["index.html"] = $"<html><head>{head}</head><body></body></html>" }));

    [Fact]
    public void Badge_links_to_the_frontend_when_it_is_hosted_separately()
    {
        var html = HtmlInjector.Inject("<html><head></head><body></body></html>", "https://api.casco.studio", "k", preview: false, badge: true, homeUrl: "https://casco.studio/");
        Assert.Contains("src=\"https://api.casco.studio/sdk/casco.js?v=2\"", html);
        Assert.Contains("href=\"https://casco.studio\"", html);
        Assert.True(new AppOptions { PublicUrl = "https://api.casco.studio", FrontendUrl = "https://casco.studio" }.SeparateFrontend);
        Assert.False(new AppOptions { PublicUrl = "https://casco.studio/", FrontendUrl = "" }.SeparateFrontend);
    }

    [Fact]
    public void Preview_pages_are_not_indexed()
    {
        var html = HtmlInjector.Inject("<html><head></head><body></body></html>", "https://api.casco.studio", "k", preview: true, badge: false);
        Assert.Contains("noindex, nofollow", html);
    }

    [Fact]
    public void Prompt_keeps_a_stable_prefix_for_provider_caching()
    {
        var files = Site();
        var a = PromptBuilder.Build(files, new PromptContext("A", "landing", ["forms"], false, true, "مطعم", []), 60_000);
        var b = PromptBuilder.Build(files, new PromptContext("B", "landing", ["forms"], true, false, "عيادة", [("user", "hi")]), 60_000);
        Assert.Equal("system", a[0].Role);
        Assert.Equal(a[0].Content, b[0].Content);
        Assert.Equal(a[1].Content, b[1].Content);
        Assert.NotEqual(a[2].Content, b[2].Content);
        // A fresh template is shared by every user of it, so it joins the cached system block; an edited site does not.
        Assert.Equal("system", a[1].Role);
        Assert.Equal("user", b[1].Role);
        Assert.Equal("user", a[2].Role);
        var laterPart = PromptBuilder.Build(files, new PromptContext("A", "landing", ["forms"], true, true, "مطعم", [],
            Progress: new BuildProgress(2, ["done"], "3 left")), 60_000);
        Assert.Equal("user", laterPart[1].Role);
        Assert.Equal(AiClient.ComputeCacheKey("m", new ChatRequest(a, 100)), AiClient.ComputeCacheKey("m", new ChatRequest(a, 100)));
        Assert.NotEqual(AiClient.ComputeCacheKey("m", new ChatRequest(a, 100)), AiClient.ComputeCacheKey("m", new ChatRequest(b, 100)));
    }

    private static Dictionary<string, string> BigSite()
    {
        static string Page(string title, string heading, string body) =>
            $"<!doctype html><html><head><title>{title}</title></head><body><h1>{heading}</h1><p>{body}</p></body></html>";
        var filler = new string('x', 9_000);
        var site = new Dictionary<string, string>
        {
            ["index.html"] = "<html><head><title>Home</title></head><body><nav><a href=\"pricing.html\">الأسعار</a> <a href=\"about.html\">من نحن</a></nav></body></html>",
            ["pricing.html"] = Page("Plans", "Gold &amp; Silver", filler),
            ["about.html"] = Page("About", "Our story", filler),
        };
        for (var i = 1; i <= 8; i++) site[$"blog-{i}.html"] = Page($"Post {i}", $"Post {i}", filler);
        return site;
    }

    [Theory]
    [InlineData("عدّل صفحة الأسعار وخلي الباقة الذهبية بـ 500", "pricing.html")]
    [InlineData("غير اسعار الباقات", "pricing.html")]
    [InlineData("edit the pricing page", "pricing.html")]
    [InlineData("عدل صفحة من نحن", "about.html")]
    public void Big_sites_pick_the_page_the_request_names_even_through_arabic_menu_text(string request, string expected)
    {
        var selected = PromptBuilder.SelectFiles(BigSite(), request, 25_000);
        Assert.Equal(new[] { "index.html", expected }.Order(StringComparer.Ordinal), selected.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("عدل عنوان المقال رقم 3")]
    [InlineData("عدل عنوان المقال رقم ٣")]
    public void A_page_number_in_the_request_picks_that_page(string request)
    {
        var site = BigSite();
        site["index.html"] = site["index.html"].Replace("</nav>", string.Concat(Enumerable.Range(1, 8).Select(i => $"<a href=\"blog-{i}.html\">المقال {i}</a>")) + "</nav>");
        var selected = PromptBuilder.SelectFiles(site, request, 25_000);
        Assert.Equal(new[] { "blog-3.html", "index.html" }, selected.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equally_relevant_pages_that_do_not_all_fit_are_left_to_the_site_map()
    {
        var site = BigSite();
        site["index.html"] = site["index.html"].Replace("</nav>", string.Concat(Enumerable.Range(1, 8).Select(i => $"<a href=\"blog-{i}.html\">المقال {i}</a>")) + "</nav>");
        Assert.Equal(new[] { "index.html" }, PromptBuilder.SelectFiles(site, "في مقال القهوة غير العنوان", 25_000).Keys.ToArray());
        var messages = PromptBuilder.Build(site, new PromptContext("A", "landing", ["forms"], true, false, "في مقال القهوة غير العنوان", []), 25_000);
        Assert.True(PromptBuilder.HasSiteMap(messages));
        Assert.Contains("never reply with only a plan", messages[2].Content);
        Assert.DoesNotContain("never reply with only a plan", PromptBuilder.Build(Site(), new PromptContext("A", "landing", ["forms"], true, false, "x", []), 25_000)[2].Content);
        Assert.Contains("interface language is Arabic", PromptBuilder.Build(Site(), new PromptContext("A", "landing", ["forms"], true, true, "cafe", [], Language: "Arabic"), 25_000)[2].Content);
    }

    [Fact]
    public void Pages_that_match_nothing_are_left_to_the_site_map()
    {
        var selected = PromptBuilder.SelectFiles(BigSite(), "غير لون الخط", 60_000);
        Assert.Equal(new[] { "index.html" }, selected.Keys.ToArray());
    }

    [Fact]
    public void Hidden_files_are_described_in_a_site_map_the_model_can_read_from()
    {
        var messages = PromptBuilder.Build(BigSite(), new PromptContext("A", "landing", ["forms"], true, false, "عدل صفحة الأسعار", []), 25_000);
        var files = messages[1].Content;
        Assert.Contains("<<<FILE pricing.html>>>", files);
        Assert.Contains("## Site map", files);
        Assert.Contains("- blog-3.html (9 KB) | \"Post 3\" | Post 3", files);
        Assert.Contains("\"read\"", files);
        Assert.DoesNotContain("- pricing.html (", files);

        var small = PromptBuilder.Build(Site(), new PromptContext("A", "landing", ["forms"], true, false, "x", []), 25_000);
        Assert.DoesNotContain("## Site map", small[1].Content);

        var map = PromptBuilder.Build(BigSite(), new PromptContext("A", "landing", ["forms"], true, false, "blog", []), 25_000)[1].Content;
        Assert.Contains("- pricing.html (9 KB) | \"Plans\" | Gold & Silver | linked as: الأسعار", map);
    }

    [Fact]
    public void Read_reply_sends_the_requested_files_within_the_budget()
    {
        var site = BigSite();
        var reply = PromptBuilder.ReadReply(["/blog-1.html", "blog-1.html", "nope.html", "blog-2.html", "blog-3.html"], site, 20_000, last: false);
        Assert.StartsWith(PromptBuilder.ReadReplyHeader, reply);
        Assert.Contains("<<<FILE blog-1.html>>>", reply);
        Assert.Contains("<<<FILE blog-2.html>>>", reply);
        Assert.DoesNotContain("<<<FILE blog-3.html>>>", reply);
        Assert.Contains("do not exist: nope.html", reply);
        Assert.Contains("too large: blog-3.html", reply);
        Assert.Contains("one more \"read\"", reply);
        Assert.Contains("No more files can be read", PromptBuilder.ReadReply(["blog-1.html"], site, 20_000, last: true));

        var parsed = EditApplier.Parse("""{"summary": "", "read": ["pricing.html"], "files": []}""");
        Assert.Equal(new[] { "pricing.html" }, parsed!.Read);
        Assert.Empty(parsed.Files!);
    }

    [Fact]
    public void Leading_system_messages_merge_into_one_for_providers_that_keep_only_the_first()
    {
        List<ChatMessageDto> messages = [new("system", "rules"), new("system", "files"), new("user", "request"), new("system", "late")];
        var merged = OpenAiCompatibleProvider.MergeSystemMessages(messages);
        Assert.Equal(3, merged.Count);
        Assert.Equal(new ChatMessageDto("system", "rules\n\nfiles"), merged[0]);
        Assert.Equal(new ChatMessageDto("user", "request"), merged[1]);
        var single = messages.Skip(1).ToList();
        Assert.Same(single, OpenAiCompatibleProvider.MergeSystemMessages(single));
    }

    [Fact]
    public void Cost_uses_discounted_rate_for_cached_tokens()
    {
        var model = new AiModelOptions { InputPer1M = 0.10m, CachedInputPer1M = 0.01m, OutputPer1M = 0.50m };
        var cost = CostCalculator.Compute(model, 10_000, 8_000, 2_000);
        Assert.Equal((2_000 * 0.10m + 8_000 * 0.01m + 2_000 * 0.50m) / 1_000_000m, cost);
    }
}

public class TemplateTests
{
    private static readonly TemplateCatalog Catalog = new(FindTemplatesRoot(), NullLogger.Instance);

    private static string FindTemplatesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Casco.Api", "SiteTemplates")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Casco.Api", "SiteTemplates");
    }

    [Fact]
    public void All_three_templates_load()
    {
        Assert.NotNull(Catalog.Get("landing"));
        Assert.NotNull(Catalog.Get("courses"));
        Assert.NotNull(Catalog.Get("ads"));
        Assert.Equal("free", Catalog.Get("landing")!.Plan);
    }

    [Theory]
    [InlineData("landing")]
    [InlineData("courses")]
    [InlineData("ads")]
    [InlineData("store")]
    [InlineData("booking")]
    public void Templates_are_valid_and_use_only_allowed_tokens(string key)
    {
        var template = Catalog.Get(key)!;
        Assert.True(template.Files.ContainsKey("index.html"));
        var errors = SiteValidator.Validate(template.Files, singlePage: template.Plan == "free");
        Assert.Empty(errors);
        Assert.Equal(key == "landing" ? HostingTiers.Static : HostingTiers.Backend, HostingService.RequiredTier(template.Files));

        foreach (var (path, content) in template.Files)
        {
            Assert.DoesNotContain("casco.js", content);
            var tokens = System.Text.RegularExpressions.Regex.Matches(content, @"\{\{([A-Z_]+)\}\}").Select(m => m.Groups[1].Value).Distinct();
            Assert.All(tokens, t => Assert.Contains(t, new[] { "BRAND", "DESCRIPTION", "YEAR", "STOCK" }));
            Assert.DoesNotContain("picsum.photos", content);
            if (SiteFiles.IsHtml(path))
            {
                Assert.Contains("dir=\"rtl\"", content);
                Assert.Contains("{{BRAND}}", content);
            }
        }
    }

    [Theory]
    [InlineData("أريد منصة كورسات لتعليم البرمجة", "courses")]
    [InlineData("موقع إعلانات مبوبة لبيع السيارات", "ads")]
    [InlineData("موقع لمطعم مشويات في دبي", "landing")]
    [InlineData("Landing page for a coffee shop in Dubai", "landing")]
    [InlineData("متجر عطور مع سلة وتوصيل", "store")]
    [InlineData("Perfume store with cash on delivery", "store")]
    [InlineData("موقع عيادة أسنان مع حجز موعد", "booking")]
    [InlineData("Dentist website where patients book a time", "booking")]
    [InlineData("Online school with video lessons for kids", "courses")]
    [InlineData("Buy and sell used cars", "ads")]
    [InlineData("कपड़ों की ऑनलाइन शॉप", "store")]
    [InlineData("बच्चों के लिए कोडिंग कोर्स", "courses")]
    public void Detects_template_from_description(string text, string expected) =>
        Assert.Equal(expected, Catalog.Detect(text).Key);

    [Theory]
    [InlineData(null, null)]
    [InlineData("auto", null)]
    [InlineData("en", "English")]
    [InlineData("HI", "Hindi")]
    [InlineData("Swahili", "Swahili")]
    [InlineData("<script>", "script")]
    [InlineData("12345", null)]
    public void Site_language_accepts_codes_and_language_names(string? input, string? expected) =>
        Assert.Equal(expected, Casco.Api.Features.Projects.ProjectService.SiteLanguage(input));
}
