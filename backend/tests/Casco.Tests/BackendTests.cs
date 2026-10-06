using System.Text.Json.Nodes;
using Casco.Api.Domain;
using Casco.Api.Features.Agent;
using Casco.Api.Features.Backend;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Commerce;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class BackendTests
{
    private const string ReviewsConfig = """
        {
          "collections": {
            "reviews": {
              "fields": {
                "name": { "type": "text", "required": true, "max": 80 },
                "rating": { "type": "integer", "min": 1, "max": 5 },
                "phone": { "type": "phone", "private": true },
                "approved": { "type": "boolean", "readonly": true, "default": false }
              },
              "access": { "read": "public", "create": "public" },
              "readWhere": { "approved": true }
            }
          },
          "functions": { "stats": {} }
        }
        """;

    private static CollectionDef Reviews() => BackendConfig.Parse(ReviewsConfig, out _).Collections["reviews"];

    private static RecordRow Row(string json, int minutesAgo = 0) =>
        new(Guid.NewGuid(), null, (JsonObject)JsonNode.Parse(json)!, DateTime.UtcNow.AddMinutes(-minutesAgo), DateTime.UtcNow);

    // ---------- casco.backend.json ----------

    [Fact]
    public void Parses_valid_config()
    {
        var config = BackendConfig.Parse(ReviewsConfig, out var errors);
        Assert.Empty(errors);
        var reviews = config.Collections["reviews"];
        Assert.Equal(4, reviews.Fields.Count);
        Assert.True(reviews.Fields["phone"].Private);
        Assert.Equal(Access.Public, reviews.Access.Create);
        Assert.Equal(Access.Admin, reviews.Access.Update);
        Assert.True(config.Functions.ContainsKey("stats"));
    }

    [Fact]
    public void Reports_invalid_config_parts()
    {
        const string json = """
            {
              "collections": {
                "Bad Name": { "fields": { "a": { "type": "text" } } },
                "items": { "fields": { "id": { "type": "text" }, "kind": { "type": "select" }, "x": { "type": "weird" } } },
                "posts": { "fields": { "title": { "type": "text" } }, "access": { "create": "public", "update": "owner" } }
              },
              "functions": { "go": { "access": "admin" } }
            }
            """;
        var config = BackendConfig.Parse(json, out var errors);
        Assert.Contains(errors, e => e.Contains("Bad Name"));
        Assert.Contains(errors, e => e.Contains("items.id"));
        Assert.Contains(errors, e => e.Contains("items.kind"));
        Assert.Contains(errors, e => e.Contains("items.x"));
        Assert.Contains(errors, e => e.Contains("anonymous records have no owner"));
        Assert.Contains(errors, e => e.Contains("'go'"));
        Assert.False(config.Collections.ContainsKey("items"));
        Assert.Empty(config.Functions);

        BackendConfig.Parse("{ not json", out errors);
        Assert.Single(errors);
    }

    [Fact]
    public void Validates_and_normalizes_writes()
    {
        var def = Reviews();
        var data = BackendConfig.ValidateData(def, (JsonObject)JsonNode.Parse("""{"name":"  Ali ","rating":"4.6","_hp":""}""")!,
            partial: false, admin: false, "https://x/u/");
        Assert.Equal("Ali", data["name"]!.GetValue<string>());
        Assert.Equal(5L, data["rating"]!.GetValue<long>());
        Assert.False(data["approved"]!.GetValue<bool>());

        Assert.Throws<ApiException>(() => BackendConfig.ValidateData(def, new JsonObject { ["rating"] = 3 }, false, false, ""));
        Assert.Throws<ApiException>(() => BackendConfig.ValidateData(def, new JsonObject { ["name"] = "a", ["approved"] = true }, false, false, ""));
        Assert.Throws<ApiException>(() => BackendConfig.ValidateData(def, new JsonObject { ["name"] = "a", ["rating"] = 9 }, false, false, ""));
        Assert.Throws<ApiException>(() => BackendConfig.ValidateData(def, new JsonObject { ["name"] = "a", ["hack"] = 1 }, false, false, ""));
        var approved = BackendConfig.ValidateData(def, new JsonObject { ["approved"] = true }, partial: true, admin: true, "");
        Assert.True(approved["approved"]!.GetValue<bool>());
    }

    // ---------- Querying ----------

    [Fact]
    public void Query_applies_read_where_filters_sort_and_paging()
    {
        var def = Reviews();
        var rows = new[]
        {
            Row("""{"name":"A","rating":5,"approved":true,"phone":"0501"}""", 3),
            Row("""{"name":"B","rating":2,"approved":true}""", 2),
            Row("""{"name":"C","rating":4,"approved":false}""", 1),
        };

        var (visitor, total) = RecordStore.Query(rows, def, new RecordQuery(), admin: false);
        Assert.Equal(2, total);
        Assert.Equal(["B", "A"], visitor.Select(r => r.Data["name"]!.GetValue<string>()));

        var (admin, adminTotal) = RecordStore.Query(rows, def, new RecordQuery(Sort: "-rating"), admin: true);
        Assert.Equal(3, adminTotal);
        Assert.Equal(["A", "C", "B"], admin.Select(r => r.Data["name"]!.GetValue<string>()));

        var where = (JsonObject)JsonNode.Parse("""{"rating":{"gte":4}}""")!;
        Assert.Equal(2, RecordStore.Query(rows, def, new RecordQuery(where), admin: true).Total);
        var inList = (JsonObject)JsonNode.Parse("""{"name":{"in":["A","C"]}}""")!;
        Assert.Equal(1, RecordStore.Query(rows, def, new RecordQuery(inList), admin: false).Total);
        Assert.Single(RecordStore.Query(rows, def, new RecordQuery(PageSize: 1, Page: 2), admin: true).Items);
        Assert.Equal(1, RecordStore.Query(rows, def, new RecordQuery(Search: "b"), admin: false).Total);

        var byPhone = (JsonObject)JsonNode.Parse("""{"phone":"0501"}""")!;
        Assert.Throws<ApiException>(() => RecordStore.Query(rows, def, new RecordQuery(byPhone), admin: false));
        var unknown = (JsonObject)JsonNode.Parse("""{"nope":1}""")!;
        Assert.Throws<ApiException>(() => RecordStore.Query(rows, def, new RecordQuery(unknown), admin: true));
    }

    [Fact]
    public void Shape_hides_private_fields_from_visitors()
    {
        var def = Reviews();
        var row = Row("""{"name":"A","phone":"0501","approved":true}""");
        Assert.Null(RecordStore.Shape(def, row, null, admin: false)["phone"]);
        Assert.Equal("0501", RecordStore.Shape(def, row, null, admin: true)["phone"]!.GetValue<string>());
        Assert.False(RecordStore.Shape(def, row, Guid.NewGuid(), admin: false)["mine"]!.GetValue<bool>());
    }

    [Fact]
    public void Compare_orders_json_scalars()
    {
        var n = JsonNode.Parse("[null,true,3,\"3\",\"10\",\"b\",\"A\"]")!.AsArray();
        Assert.True(RecordStore.Compare(n[0], n[1]) < 0);
        Assert.True(RecordStore.Compare(n[1], n[2]) < 0);
        Assert.Equal(0, RecordStore.Compare(n[2], n[3]));
        Assert.True(RecordStore.Compare(n[2], n[4]) < 0);
        Assert.True(RecordStore.Compare(n[6], n[5]) < 0);
    }

    // ---------- Booking slots ----------

    private static BookingSettings Hours() => new()
    {
        TimeZone = "Asia/Dubai",
        SlotMinutes = 30,
        MinNoticeMinutes = 60,
        Week = [new WorkingDay { Day = (int)DayOfWeek.Friday, Open = "09:00", Close = "11:00" }]
    };

    private static readonly DateOnly Friday = new(2026, 1, 16);

    [Fact]
    public void Slots_follow_working_hours_timezone_and_capacity()
    {
        var now = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var busy = new[] { (new DateTime(2026, 1, 16, 5, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 16, 6, 0, 0, DateTimeKind.Utc)) };
        var slots = BookingSlots.Compute(Hours(), 60, Friday, busy, now);

        Assert.Equal(["09:00", "09:30", "10:00"], slots.Select(s => s.LocalTime));
        Assert.Equal(new DateTime(2026, 1, 16, 5, 0, 0, DateTimeKind.Utc), slots[0].StartUtc);
        Assert.Equal([false, false, true], slots.Select(s => s.Available));

        var roomy = Hours();
        roomy.Capacity = 2;
        Assert.All(BookingSlots.Compute(roomy, 60, Friday, busy, now), s => Assert.True(s.Available));
    }

    [Fact]
    public void Slots_respect_notice_closed_days_and_range()
    {
        var hours = Hours();
        Assert.Empty(BookingSlots.Compute(hours, 60, Friday, [], new DateTime(2026, 1, 16, 5, 10, 0, DateTimeKind.Utc)));
        Assert.Empty(BookingSlots.Compute(hours, 30, Friday.AddDays(1), [], new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Empty(BookingSlots.Compute(hours, 30, Friday, [], new DateTime(2026, 1, 17, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Empty(BookingSlots.Compute(hours, 30, Friday.AddDays(7 * 10), [], new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        hours.ClosedDates = ["2026-01-16"];
        Assert.Empty(BookingSlots.Compute(hours, 30, Friday, [], new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
    }

    // ---------- Hosting ----------

    [Fact]
    public void Required_tier_detects_backend_usage()
    {
        Assert.Equal(HostingTiers.Static, HostingService.RequiredTier(new Dictionary<string, string>
        {
            ["index.html"] = "<html><script>Casco.forms.submit('contact', {})</script></html>"
        }));
        Assert.Equal(HostingTiers.Backend, HostingService.RequiredTier(new Dictionary<string, string>
        {
            ["index.html"] = "<html><script>Casco.cart.add(p)</script></html>"
        }));
        Assert.Equal(HostingTiers.Backend, HostingService.RequiredTier(new Dictionary<string, string>
        {
            ["index.html"] = "<html></html>",
            [BackendConfig.FileName] = ReviewsConfig
        }));

        Assert.Equal(HostingTiers.Backend, HostingService.RequiredTier(new Dictionary<string, string>
        {
            ["assets/app.js"] = "let C; function boot() { C = window.Casco; C.ads.list({}); }"
        }));
        Assert.Equal(HostingTiers.Static, HostingService.RequiredTier(new Dictionary<string, string>
        {
            ["assets/app.js"] = "var api = window.Casco; api.forms.submit('x', {}); var ads = { db: 1 }; ads.db;"
        }));

        var features = HostingService.Features(new Dictionary<string, string>
        {
            ["index.html"] = "<script>Casco.cart.count(); Casco.fn('stats'); Casco.bookings.services()</script>"
        });
        Assert.Equal(["bookings", "db", "store"], features);
    }

    [Fact]
    public void Hosting_extension_prorates_between_tiers()
    {
        var options = new HostingOptions();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var paid = now.AddDays(30);

        Assert.Equal(now.AddMonths(1), HostingService.ExtendUntil(now, null, null, HostingTiers.Static, 1, options));
        Assert.Equal(paid.AddMonths(1), HostingService.ExtendUntil(now, HostingTiers.Static, paid, HostingTiers.Static, 1, options));
        Assert.Equal(now.AddDays(15).AddMonths(1), HostingService.ExtendUntil(now, HostingTiers.Static, paid, HostingTiers.Backend, 1, options));
        Assert.Equal(now.AddDays(60).AddMonths(12), HostingService.ExtendUntil(now, HostingTiers.Backend, paid, HostingTiers.Static, 12, options));
        Assert.Equal(now.AddMonths(1), HostingService.ExtendUntil(now, HostingTiers.Backend, now.AddDays(-2), HostingTiers.Backend, 1, options));
    }

    // ---------- Validator ----------

    [Fact]
    public void Validator_checks_backend_files_and_references()
    {
        var files = new Dictionary<string, string>
        {
            ["index.html"] = "<html><script>Casco.db.list('orders'); Casco.fn('missing'); Casco.db.create('reviews', {})</script></html>",
            [BackendConfig.FileName] = ReviewsConfig,
            ["server/extra.js"] = "function x() {}"
        };
        var errors = SiteValidator.Validate(files, singlePage: true).Select(e => e.Error).ToList();
        Assert.Contains(errors, e => e.Contains("does not exist"));
        Assert.Contains(errors, e => e.Contains("server/extra.js") || e.Contains("Only server/functions.js"));
        Assert.Contains(errors, e => e.Contains("'orders'"));
        Assert.Contains(errors, e => e.Contains("'missing'"));
        Assert.DoesNotContain(errors, e => e.Contains("'reviews'"));

        files.Remove("server/extra.js");
        files[BackendConfig.FunctionsFile] = "function stats(input, ctx) { return { ok: true ";
        errors = SiteValidator.Validate(files, singlePage: true).Select(e => e.Error).ToList();
        Assert.Contains(errors, e => e.Contains("syntax error"));

        files[BackendConfig.FunctionsFile] = "function other() { return 1; }";
        errors = SiteValidator.Validate(files, singlePage: true).Select(e => e.Error).ToList();
        Assert.Contains(errors, e => e.Contains("Function 'stats'"));
    }
}

public class FunctionSandboxTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly RecordStore _store = new();
    private readonly FunctionRunner _runner;
    private readonly SiteContext _site;
    private readonly SiteRef _project;
    private readonly BackendConfig _config;

    public FunctionSandboxTests()
    {
        var app = Options.Create(new AppOptions { DataPath = Path.GetTempPath() });
        _runner = new FunctionRunner(_store, new UploadService(new LocalUploadStorage(app), app), NullLogger<FunctionRunner>.Instance);
        _site = new SiteContext(_t.Db, new MemoryCache(new MemoryCacheOptions()), null!, Options.Create(new BillingOptions()), TimeProvider.System);

        var user = new User { Email = "o@b.c", Name = "O" };
        var project = new Project { UserId = user.Id, Name = "P", SiteKey = "k" + Guid.NewGuid().ToString("N"), Slug = "s" + Guid.NewGuid().ToString("N")[..10] };
        _t.Db.Users.Add(user);
        _t.Db.Projects.Add(project);
        _t.Db.SaveChanges();
        _project = new SiteRef(project.Id, user.Id, project.SiteKey, false, null, null, false, null, null, "{}");
        _config = BackendConfig.Parse("""
            { "collections": { "notes": { "fields": { "text": { "type": "text", "required": true } } } } }
            """, out _);
    }

    public void Dispose()
    {
        _store.Dispose();
        _t.Dispose();
    }

    private Task<JsonNode?> Run(string code, string name, JsonNode? input = null) =>
        _runner.RunAsync(_site, _project, _config, code, name, input, null, CancellationToken.None);

    [Fact]
    public async Task Runs_functions_with_input_and_database()
    {
        const string code = """
            function add(input, ctx) {
              ctx.db.create('notes', { text: input.text });
              var list = ctx.db.list('notes');
              return { total: list.total, first: list.items[0].text, anonymous: ctx.user === null };
            }
            """;
        var result = await Run(code, "add", new JsonObject { ["text"] = "مرحبا" });
        Assert.Equal(1, result!["total"]!.GetValue<int>());
        Assert.Equal("مرحبا", result["first"]!.GetValue<string>());
        Assert.True(result["anonymous"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Infinite_loops_hit_the_limit()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Run("function spin() { while (true) {} }", "spin"));
        Assert.Equal("function_limit", ex.Code);
    }

    [Fact]
    public async Task No_host_access_and_errors_are_reported()
    {
        var clr = await Run("function probe() { return [typeof System, typeof importNamespace, typeof require, typeof fetch, typeof setTimeout]; }", "probe");
        Assert.All(clr!.AsArray(), v => Assert.Equal("undefined", v!.GetValue<string>()));

        var thrown = await Assert.ThrowsAsync<ApiException>(() => Run("function bad() { throw new Error('الكمية نفدت'); }", "bad"));
        Assert.Equal("function_error", thrown.Code);
        Assert.Equal("الكمية نفدت", thrown.Message);

        var invalid = await Assert.ThrowsAsync<ApiException>(() => Run("function ok(input, ctx) { return ctx.db.list('secret'); }", "ok"));
        Assert.Equal(400, invalid.Status);
    }
}
