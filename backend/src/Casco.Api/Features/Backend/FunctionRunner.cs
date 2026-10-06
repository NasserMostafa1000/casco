using System.Text.Json;
using System.Text.Json.Nodes;
using Casco.Api.Domain;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Jint;
using Jint.Runtime;

namespace Casco.Api.Features.Backend;

public record FunctionUser(Guid Id, string Name, string Email);

/// <summary>
/// Runs the site's server/functions.js in a Jint sandbox: no CLR, file, network or timer access, hard CPU/memory limits.
/// The only way out is ctx.db, which goes through the same schema validation as the dashboard.
/// </summary>
public class FunctionRunner(RecordStore store, UploadService uploads, ILogger<FunctionRunner> logger)
{
    public const int MaxInputBytes = 32 * 1024;
    public const int MaxOutputBytes = 64 * 1024;
    public const int MaxDbOperations = 50;
    private static readonly SemaphoreSlim Gate = new(4, 4);

    private const string Prelude = """
        var __ctx = Object.freeze({
          user: __user ? JSON.parse(__user) : null,
          now: __now,
          db: Object.freeze({
            list: function (c, q) { return JSON.parse(__db('list', String(c), JSON.stringify(q || {}))); },
            get: function (c, id) { return JSON.parse(__db('get', String(c), JSON.stringify({ id: String(id) }))); },
            create: function (c, data) { return JSON.parse(__db('create', String(c), JSON.stringify(data || {}))); },
            update: function (c, id, data) { return JSON.parse(__db('update', String(c), JSON.stringify({ id: String(id), data: data || {} }))); },
            remove: function (c, id) { return JSON.parse(__db('remove', String(c), JSON.stringify({ id: String(id) }))); }
          })
        });
        function __call(name) {
          var f = globalThis[name];
          if (typeof f !== 'function') throw new Error('Function "' + name + '" is not defined in server/functions.js');
          var result = f(JSON.parse(__input), __ctx);
          return JSON.stringify(result === undefined ? null : result);
        }
        """;

    private static Engine CreateEngine(CancellationToken ct) => new(o => o
        .Strict()
        .TimeoutInterval(TimeSpan.FromSeconds(2))
        .MaxStatements(200_000)
        .LimitMemory(32_000_000)
        .LimitRecursion(64)
        .RegexTimeoutInterval(TimeSpan.FromSeconds(1))
        .CancellationToken(ct));

    /// <summary>Returns a readable message when the code does not parse, otherwise null.</summary>
    public static string? SyntaxError(string code)
    {
        try
        {
            Engine.PrepareScript(code, BackendConfig.FunctionsFile, strict: true);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public async Task<JsonNode?> RunAsync(SiteContext site, SiteRef project, BackendConfig config, string code, string name,
        JsonNode? input, FunctionUser? user, CancellationToken ct)
    {
        var inputJson = input?.ToJsonString() ?? "{}";
        if (inputJson.Length > MaxInputBytes) throw ApiException.BadRequest("البيانات المرسلة كبيرة جداً");
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), ct))
            throw new ApiException(503, "الخادم مشغول، حاول بعد لحظات", "busy");
        try
        {
            return await Task.Run(() => Execute(site, project, config, code, name, inputJson, user, ct), ct);
        }
        finally { Gate.Release(); }
    }

    private JsonNode? Execute(SiteContext site, SiteRef project, BackendConfig config, string code, string name, string inputJson,
        FunctionUser? user, CancellationToken ct)
    {
        var ops = 0;
        var prefix = uploads.PublicPrefix(project.Id);

        string Db(string op, string collection, string argsJson)
        {
            if (++ops > MaxDbOperations) throw ApiException.BadRequest($"الدالة تجاوزت {MaxDbOperations} عملية على البيانات");
            if (!config.Collections.TryGetValue(collection, out var def)) throw ApiException.BadRequest($"القائمة '{collection}' غير معرفة في {BackendConfig.FileName}");
            var args = RecordStore.ParseObject(argsJson);
            // DB work runs on another thread so its allocations don't count against the script's memory limit.
            return Task.Run(() => DbOperationAsync(site, project, def, op, args, user, prefix, ct), ct).GetAwaiter().GetResult();
        }

        var engine = CreateEngine(ct);
        try
        {
            engine.SetValue("__db", new Func<string, string, string, string>(Db));
            engine.SetValue("__input", inputJson);
            engine.SetValue("__user", user is null ? "" : JsonSerializer.Serialize(new { id = user.Id, name = user.Name, email = user.Email }));
            engine.SetValue("__now", DateTime.UtcNow.ToString("O"));
            engine.Execute(Prelude);
            engine.Execute(code, BackendConfig.FunctionsFile);
            var output = engine.Invoke("__call", name).AsString();
            if (output.Length > MaxOutputBytes) throw ApiException.BadRequest("نتيجة الدالة كبيرة جداً");
            return JsonNode.Parse(output);
        }
        catch (ApiException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JavaScriptException e)
        {
            throw ApiException.BadRequest(Text.Truncate(e.Error.IsObject() && e.Error.AsObject().Get("message").IsString()
                ? e.Error.AsObject().Get("message").AsString() : e.Message, 300), "function_error");
        }
        catch (Exception e) when (e is TimeoutException or StatementsCountOverflowException or MemoryLimitExceededException
                                   or RecursionDepthOverflowException or ExecutionCanceledException)
        {
            throw ApiException.BadRequest("الدالة استغرقت وقتاً أو ذاكرة أكثر من المسموح", "function_limit");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Server function {Name} failed for project {ProjectId}", name, project.Id);
            throw ApiException.BadRequest("حدث خطأ في دالة الخادم", "function_error");
        }
    }

    private async Task<string> DbOperationAsync(SiteContext site, SiteRef project, CollectionDef def, string op, JsonObject args,
        FunctionUser? user, string prefix, CancellationToken ct)
    {
        var db = site.Db;
        Guid Id() => Guid.TryParse(args["id"]?.GetValue<string>(), out var g) ? g : throw ApiException.BadRequest("معرّف غير صالح");
        switch (op)
        {
            case "list":
            {
                var rows = await store.LoadAsync(db, project.Id, def.Name, ct);
                var q = new RecordQuery(args["where"] as JsonObject, Str(args["sort"]), Str(args["q"]),
                    (int?)Num(args["page"]) ?? 1, (int?)Num(args["pageSize"]) ?? 20);
                var (items, total) = RecordStore.Query(rows, def, q, admin: true);
                return new JsonObject
                {
                    ["items"] = new JsonArray(items.Select(r => (JsonNode)RecordStore.Shape(def, r, user?.Id, true)).ToArray()),
                    ["total"] = total
                }.ToJsonString();
            }
            case "get":
            {
                var id = Id();
                var row = (await store.LoadAsync(db, project.Id, def.Name, ct)).FirstOrDefault(r => r.Id == id);
                return row is null ? "null" : RecordStore.Shape(def, row, user?.Id, true).ToJsonString();
            }
            case "create":
            {
                if (!site.IsBackendActive(project) && await store.LoadAsync(db, project.Id, def.Name, ct) is { Count: >= SiteContext.TrialLimit })
                    throw ApiException.Payment($"وضع التجربة يسمح بـ {SiteContext.TrialLimit} عنصر فقط. فعّل استضافة الباك إند لاستقبال المزيد.", "backend_trial_limit");
                var data = BackendConfig.ValidateData(def, args, partial: false, admin: true, prefix);
                var row = await store.CreateAsync(db, project.Id, def.Name, data, user?.Id, ct);
                return RecordStore.Shape(def, row, user?.Id, true).ToJsonString();
            }
            case "update":
            {
                var patch = BackendConfig.ValidateData(def, args["data"] as JsonObject ?? new JsonObject(), partial: true, admin: true, prefix);
                var row = await store.UpdateAsync(db, project.Id, def.Name, Id(), patch, ct);
                return row is null ? "null" : RecordStore.Shape(def, row, user?.Id, true).ToJsonString();
            }
            case "remove":
                return await store.DeleteAsync(db, project.Id, def.Name, Id(), ct) ? "true" : "false";
            default:
                throw ApiException.BadRequest("عملية غير مدعومة");
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}

public static class FunctionEndpoints
{
    public static void MapFunctionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/s/{siteKey}/fn/{name}", async (string siteKey, string name, HttpContext http, SiteContext site, FunctionRunner runner) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var version = SiteContext.VersionFor(project, http);
            var config = await site.ConfigAsync(version);
            if (!config.Functions.TryGetValue(name, out var fn)) throw ApiException.NotFound($"الدالة '{name}' غير موجودة");
            var code = await site.FunctionsCodeAsync(version) ?? throw ApiException.NotFound("لا توجد دوال خادم لهذا الموقع");

            JsonNode? input = null;
            if (http.Request.ContentLength is > 0 || http.Request.ContentType?.Contains("json") == true)
            {
                if (http.Request.ContentLength > FunctionRunner.MaxInputBytes) throw ApiException.BadRequest("البيانات المرسلة كبيرة جداً");
                try { input = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); }
                catch (JsonException) { throw ApiException.BadRequest("بيانات غير صالحة"); }
            }

            FunctionUser? user = null;
            var userId = await site.SiteUserIdAsync(http, project);
            if (userId is not null)
            {
                var u = await site.RequireUserAsync(http, project);
                user = new FunctionUser(u.Id, u.Name, u.Email);
            }
            if (fn.Access == Access.Users && user is null) throw new ApiException(401, "سجّل الدخول أولاً", "login_required");

            var result = await runner.RunAsync(site, project, config, code, name, input, user, http.RequestAborted);
            return Results.Json(new { result });
        }).RequireCors("sites").RequireRateLimiting("site");
    }
}
