using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Casco.Api.Features.Backend;

public record RecordRow(Guid Id, Guid? UserId, JsonObject Data, DateTime CreatedAt, DateTime UpdatedAt);

public record RecordQuery(JsonObject? Where = null, string? Sort = null, string? Search = null, int Page = 1, int PageSize = 20);

/// <summary>
/// Storage for custom collections. Sites are small, so each collection is loaded once into a size-bounded cache
/// and filtered in memory; writes go to the database and drop the cached copy.
/// </summary>
public sealed class RecordStore : IDisposable
{
    public const int MaxPerCollection = 5000;
    public const int MaxPerProject = 20000;
    public const int MaxRecordBytes = 16 * 1024;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024 });
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private static string Key(Guid projectId, string collection) => $"{projectId:N}:{collection}";

    public static SemaphoreSlim LockFor(Guid projectId) => Locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));

    public async Task<IReadOnlyList<RecordRow>> LoadAsync(AppDbContext db, Guid projectId, string collection, CancellationToken ct = default)
    {
        var key = Key(projectId, collection);
        if (_cache.TryGetValue(key, out IReadOnlyList<RecordRow>? cached) && cached is not null) return cached;

        var rows = await db.SiteRecords.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Collection == collection)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.SiteUserId, r.DataJson, r.CreatedAt, r.UpdatedAt })
            .ToListAsync(ct);
        long size = 256;
        var list = new List<RecordRow>(rows.Count);
        foreach (var r in rows)
        {
            size += r.DataJson.Length * 4L + 200;
            list.Add(new RecordRow(r.Id, r.SiteUserId, ParseObject(r.DataJson), r.CreatedAt, r.UpdatedAt));
        }
        _cache.Set(key, (IReadOnlyList<RecordRow>)list, new MemoryCacheEntryOptions { Size = size, SlidingExpiration = TimeSpan.FromMinutes(10) });
        return list;
    }

    public void Invalidate(Guid projectId, string collection) => _cache.Remove(Key(projectId, collection));

    public async Task<RecordRow> CreateAsync(AppDbContext db, Guid projectId, string collection, JsonObject data, Guid? userId, CancellationToken ct = default)
    {
        var json = data.ToJsonString();
        if (json.Length > MaxRecordBytes) throw ApiException.BadRequest("البيانات أكبر من المسموح");
        var gate = LockFor(projectId);
        await gate.WaitAsync(ct);
        try
        {
            if (await db.SiteRecords.CountAsync(r => r.ProjectId == projectId && r.Collection == collection, ct) >= MaxPerCollection)
                throw ApiException.BadRequest("وصلت هذه القائمة للحد الأقصى من العناصر");
            if (await db.SiteRecords.CountAsync(r => r.ProjectId == projectId, ct) >= MaxPerProject)
                throw ApiException.BadRequest("وصل الموقع للحد الأقصى من البيانات");
            var now = DateTime.UtcNow;
            var entity = new SiteRecord { ProjectId = projectId, Collection = collection, SiteUserId = userId, DataJson = json, CreatedAt = now, UpdatedAt = now };
            db.SiteRecords.Add(entity);
            await db.SaveChangesAsync(ct);
            Invalidate(projectId, collection);
            return new RecordRow(entity.Id, userId, data, now, now);
        }
        finally { gate.Release(); }
    }

    public async Task<RecordRow?> UpdateAsync(AppDbContext db, Guid projectId, string collection, Guid id, JsonObject patch, CancellationToken ct = default)
    {
        var entity = await db.SiteRecords.FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == projectId && r.Collection == collection, ct);
        if (entity is null) return null;
        var data = ParseObject(entity.DataJson);
        foreach (var (k, v) in patch)
        {
            if (v is null) data.Remove(k);
            else data[k] = v.DeepClone();
        }
        var json = data.ToJsonString();
        if (json.Length > MaxRecordBytes) throw ApiException.BadRequest("البيانات أكبر من المسموح");
        entity.DataJson = json;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        Invalidate(projectId, collection);
        return new RecordRow(entity.Id, entity.SiteUserId, data, entity.CreatedAt, entity.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(AppDbContext db, Guid projectId, string collection, Guid id, CancellationToken ct = default)
    {
        var deleted = await db.SiteRecords.Where(r => r.Id == id && r.ProjectId == projectId && r.Collection == collection).ExecuteDeleteAsync(ct);
        Invalidate(projectId, collection);
        return deleted > 0;
    }

    public static JsonObject ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    // ---------- Querying ----------

    public static (List<RecordRow> Items, int Total) Query(IEnumerable<RecordRow> rows, CollectionDef def, RecordQuery q, bool admin)
    {
        var filtered = rows;
        if (!admin && def.ReadWhere is { Count: > 0 } readWhere)
            filtered = filtered.Where(r => readWhere.All(w => Compare(Field(r, w.Key), w.Value) == 0));
        if (q.Where is { Count: > 0 } where)
        {
            foreach (var (field, condition) in where)
            {
                if (!admin && def.Fields.TryGetValue(field, out var f) && f.Private) throw ApiException.BadRequest($"لا يمكن التصفية بالحقل '{field}'");
                if (field != "createdAt" && field != "updatedAt" && !def.Fields.ContainsKey(field)) throw ApiException.BadRequest($"الحقل '{field}' غير معروف");
                var name = field;
                var cond = condition;
                filtered = filtered.Where(r => Matches(Field(r, name), cond));
            }
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            var textFields = def.Fields.Where(f => (admin || !f.Value.Private) && f.Value.Type is "text" or "longtext" or "select" or "tags" or "email")
                .Select(f => f.Key).ToList();
            filtered = filtered.Where(r => textFields.Any(f => r.Data[f]?.ToJsonString().Contains(term, StringComparison.OrdinalIgnoreCase) == true));
        }

        var list = filtered.ToList();
        var sort = string.IsNullOrWhiteSpace(q.Sort) ? "-createdAt" : q.Sort.Trim();
        var desc = sort.StartsWith('-');
        var sortField = sort.TrimStart('-', '+');
        if (sortField != "createdAt" && sortField != "updatedAt" && !def.Fields.ContainsKey(sortField)) sortField = "createdAt";
        list.Sort((a, b) =>
        {
            var c = Compare(Field(a, sortField), Field(b, sortField));
            return desc ? -c : c;
        });

        var size = Math.Clamp(q.PageSize, 1, 100);
        var page = Math.Max(1, q.Page);
        return (list.Skip((page - 1) * size).Take(size).ToList(), list.Count);
    }

    private static JsonNode? Field(RecordRow r, string name) => name switch
    {
        "createdAt" => JsonValue.Create(r.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
        "updatedAt" => JsonValue.Create(r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
        _ => r.Data[name]
    };

    private static bool Matches(JsonNode? value, JsonNode? condition)
    {
        if (condition is not JsonObject ops) return Compare(value, condition) == 0 || ArrayContains(value, condition);
        foreach (var (op, operand) in ops)
        {
            var ok = op switch
            {
                "eq" => Compare(value, operand) == 0,
                "ne" => Compare(value, operand) != 0,
                "gt" => value is not null && Compare(value, operand) > 0,
                "gte" => value is not null && Compare(value, operand) >= 0,
                "lt" => value is not null && Compare(value, operand) < 0,
                "lte" => value is not null && Compare(value, operand) <= 0,
                "in" => operand is JsonArray arr && arr.Any(x => Compare(value, x) == 0),
                "contains" => ArrayContains(value, operand)
                              || (value is JsonValue && operand is JsonValue && Str(value).Contains(Str(operand), StringComparison.OrdinalIgnoreCase)),
                _ => throw ApiException.BadRequest($"عامل التصفية '{op}' غير مدعوم")
            };
            if (!ok) return false;
        }
        return true;
    }

    private static bool ArrayContains(JsonNode? value, JsonNode? item) =>
        value is JsonArray arr && arr.Any(x => Compare(x, item) == 0);

    private static string Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";

    /// <summary>Total order over JSON scalars: null &lt; bool &lt; number &lt; string.</summary>
    public static int Compare(JsonNode? a, JsonNode? b)
    {
        static int Rank(JsonNode? n) => n switch
        {
            null => 0,
            JsonValue v when v.TryGetValue<bool>(out _) => 1,
            JsonValue v when v.TryGetValue<double>(out _) => 2,
            JsonValue v when v.TryGetValue<string>(out _) => 3,
            _ => 4
        };
        int ra = Rank(a), rb = Rank(b);
        if (ra == 2 && rb == 3 && double.TryParse(Str(b), NumberStyles.Float, CultureInfo.InvariantCulture, out var bn))
            return a!.GetValue<double>().CompareTo(bn);
        if (ra == 3 && rb == 2 && double.TryParse(Str(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var an))
            return an.CompareTo(b!.GetValue<double>());
        if (ra != rb) return ra.CompareTo(rb);
        return ra switch
        {
            0 => 0,
            1 => a!.GetValue<bool>().CompareTo(b!.GetValue<bool>()),
            2 => a!.GetValue<double>().CompareTo(b!.GetValue<double>()),
            3 => string.Compare(a!.GetValue<string>(), b!.GetValue<string>(), StringComparison.OrdinalIgnoreCase),
            _ => string.CompareOrdinal(a!.ToJsonString(), b!.ToJsonString())
        };
    }

    /// <summary>Response shape: id + fields + timestamps; private fields only for the owner/admin.</summary>
    public static JsonObject Shape(CollectionDef def, RecordRow r, Guid? viewer, bool admin)
    {
        var obj = admin ? (JsonObject)r.Data.DeepClone() : BackendConfig.PublicView(def, r.Data);
        obj["id"] = r.Id.ToString();
        obj["createdAt"] = r.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        obj["updatedAt"] = r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        obj["mine"] = viewer is not null && r.UserId == viewer;
        if (admin) obj["userId"] = r.UserId?.ToString();
        return obj;
    }

    public void Dispose() => _cache.Dispose();
}
