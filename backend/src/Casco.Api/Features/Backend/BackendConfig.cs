using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Casco.Api.Infrastructure;

namespace Casco.Api.Features.Backend;

public static class Access
{
    public const string Public = "public";
    public const string Users = "users";
    public const string Owner = "owner";
    public const string Admin = "admin";
}

public class FieldDef
{
    public string Type { get; set; } = "text";
    public string? Label { get; set; }
    public bool Required { get; set; }
    /// <summary>Max length for strings, max value for numbers.</summary>
    public double? Max { get; set; }
    public double? Min { get; set; }
    public List<string>? Options { get; set; }
    /// <summary>Visitors cannot set it; only the owner dashboard and server functions can.</summary>
    public bool Readonly { get; set; }
    /// <summary>Hidden from everyone except the owner and server functions (e.g. a phone number on a public review).</summary>
    public bool Private { get; set; }
    public JsonNode? Default { get; set; }
}

public class CollectionAccess
{
    public string Read { get; set; } = Access.Public;
    public string Create { get; set; } = Access.Admin;
    public string Update { get; set; } = Access.Admin;
    public string Delete { get; set; } = Access.Admin;
}

public class CollectionDef
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public Dictionary<string, FieldDef> Fields { get; set; } = new(StringComparer.Ordinal);
    public CollectionAccess Access { get; set; } = new();
    /// <summary>Equality filter applied to every non-admin read, e.g. {"approved": true} for moderated content.</summary>
    public Dictionary<string, JsonNode?>? ReadWhere { get; set; }
}

public class FunctionDef
{
    public string Name { get; set; } = "";
    public string Access { get; set; } = Backend.Access.Public;
}

/// <summary>Parsed casco.backend.json: the custom data model and server functions of one site version.</summary>
public partial class BackendConfig
{
    public const string FileName = "casco.backend.json";
    public const string FunctionsFile = "server/functions.js";
    public const int MaxCollections = 20;
    public const int MaxFields = 40;

    public static readonly string[] FieldTypes =
        ["text", "longtext", "number", "integer", "boolean", "date", "datetime", "email", "phone", "url", "image", "images", "select", "tags"];

    private static readonly HashSet<string> ReservedFields = ["id", "createdAt", "updatedAt", "mine", "userId", "user"];

    public Dictionary<string, CollectionDef> Collections { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FunctionDef> Functions { get; } = new(StringComparer.Ordinal);

    public static readonly BackendConfig Empty = new();

    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,39}$")] private static partial Regex Identifier();
    [GeneratedRegex(@"^\+?[0-9 ()\-]{6,20}$")] private static partial Regex Phone();

    public static bool IsBackendFile(string path) =>
        path == FileName || path.StartsWith("server/", StringComparison.Ordinal);

    public static BackendConfig FromFiles(IReadOnlyDictionary<string, string> files) =>
        files.TryGetValue(FileName, out var json) ? Parse(json, out _) : Empty;

    /// <summary>Lenient parse: invalid parts are skipped and reported in <paramref name="errors"/>.</summary>
    public static BackendConfig Parse(string json, out List<string> errors)
    {
        errors = [];
        var config = new BackendConfig();
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e)
        {
            errors.Add($"{FileName} is not valid JSON: {e.Message}");
            return config;
        }
        if (root is not JsonObject obj)
        {
            errors.Add($"{FileName} must be a JSON object.");
            return config;
        }

        if (obj["collections"] is JsonObject collections)
        {
            foreach (var (name, node) in collections)
            {
                if (config.Collections.Count >= MaxCollections) { errors.Add($"At most {MaxCollections} collections are allowed."); break; }
                if (!Identifier().IsMatch(name)) { errors.Add($"Collection name '{name}' must be camelCase letters/digits (e.g. reviews)."); continue; }
                if (node is not JsonObject c) { errors.Add($"Collection '{name}' must be an object."); continue; }
                var def = ParseCollection(name, c, errors);
                if (def is not null) config.Collections[name] = def;
            }
        }
        else if (obj["collections"] is not null) errors.Add("\"collections\" must be an object keyed by collection name.");

        if (obj["functions"] is JsonObject functions)
        {
            foreach (var (name, node) in functions)
            {
                if (!Identifier().IsMatch(name)) { errors.Add($"Function name '{name}' must be camelCase letters/digits."); continue; }
                var access = (node as JsonObject)?["access"]?.GetValue<string>() ?? Access.Public;
                if (access is not (Access.Public or Access.Users)) { errors.Add($"Function '{name}' access must be \"public\" or \"users\"."); continue; }
                config.Functions[name] = new FunctionDef { Name = name, Access = access };
            }
        }
        return config;
    }

    private static CollectionDef? ParseCollection(string name, JsonObject c, List<string> errors)
    {
        var def = new CollectionDef { Name = name, Label = Str(c["label"]) };
        if (c["fields"] is not JsonObject fields || fields.Count == 0)
        {
            errors.Add($"Collection '{name}' needs a non-empty \"fields\" object.");
            return null;
        }
        foreach (var (fieldName, fieldNode) in fields)
        {
            if (def.Fields.Count >= MaxFields) { errors.Add($"Collection '{name}' has more than {MaxFields} fields."); break; }
            if (!Identifier().IsMatch(fieldName) || ReservedFields.Contains(fieldName))
            {
                errors.Add($"Field '{name}.{fieldName}' has an invalid or reserved name (reserved: {string.Join(", ", ReservedFields)}).");
                continue;
            }
            FieldDef? field;
            try { field = fieldNode?.Deserialize<FieldDef>(JsonOpts); }
            catch (JsonException) { field = null; }
            if (field is null || !FieldTypes.Contains(field.Type))
            {
                errors.Add($"Field '{name}.{fieldName}' needs \"type\" in: {string.Join(", ", FieldTypes)}.");
                continue;
            }
            if (field.Type == "select" && (field.Options is null || field.Options.Count == 0))
            {
                errors.Add($"Select field '{name}.{fieldName}' needs \"options\".");
                continue;
            }
            def.Fields[fieldName] = field;
        }
        if (def.Fields.Count == 0) return null;

        if (c["access"] is JsonObject a)
        {
            def.Access = new CollectionAccess
            {
                Read = Str(a["read"]) ?? Access.Public,
                Create = Str(a["create"]) ?? Access.Admin,
                Update = Str(a["update"]) ?? Access.Admin,
                Delete = Str(a["delete"]) ?? Access.Admin
            };
        }
        if (def.Access.Read is not (Access.Public or Access.Users or Access.Owner or Access.Admin))
            errors.Add($"Collection '{name}' access.read must be public, users, owner or admin.");
        if (def.Access.Create is not (Access.Public or Access.Users or Access.Admin))
            errors.Add($"Collection '{name}' access.create must be public, users or admin.");
        if (def.Access.Update is not (Access.Owner or Access.Admin))
            errors.Add($"Collection '{name}' access.update must be owner or admin.");
        if (def.Access.Delete is not (Access.Owner or Access.Admin))
            errors.Add($"Collection '{name}' access.delete must be owner or admin.");
        if (def.Access.Create == Access.Public && (def.Access.Update == Access.Owner || def.Access.Delete == Access.Owner))
            errors.Add($"Collection '{name}': anonymous records have no owner, so use create \"users\" when update/delete is \"owner\".");

        if (c["readWhere"] is JsonObject where)
        {
            def.ReadWhere = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (k, v) in where)
            {
                if (!def.Fields.ContainsKey(k)) { errors.Add($"Collection '{name}' readWhere uses unknown field '{k}'."); continue; }
                def.ReadWhere[k] = v?.DeepClone();
            }
        }
        return def;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Validates and normalizes a write. <paramref name="partial"/> skips missing required fields (updates).
    /// Readonly fields are rejected unless <paramref name="admin"/>.
    /// </summary>
    public static JsonObject ValidateData(CollectionDef def, JsonObject input, bool partial, bool admin, string uploadPrefix)
    {
        var output = new JsonObject();
        foreach (var (key, _) in input)
        {
            if (key == "_hp") continue;
            if (!def.Fields.ContainsKey(key)) throw ApiException.BadRequest($"الحقل '{key}' غير معروف");
        }

        foreach (var (name, field) in def.Fields)
        {
            var present = input.TryGetPropertyValue(name, out var value);
            if (present && field.Readonly && !admin) throw ApiException.BadRequest($"لا يمكن تعديل الحقل '{field.Label ?? name}'");
            if (!present || value is null || (value is JsonValue sv && sv.TryGetValue<string>(out var s0) && s0.Length == 0))
            {
                if (!partial)
                {
                    if (field.Default is not null) output[name] = field.Default.DeepClone();
                    else if (field.Required) throw ApiException.BadRequest($"الحقل '{field.Label ?? name}' مطلوب");
                }
                else if (present)
                {
                    if (field.Required) throw ApiException.BadRequest($"الحقل '{field.Label ?? name}' مطلوب");
                    output[name] = null;
                }
                continue;
            }
            output[name] = Normalize(name, field, value, uploadPrefix);
        }
        return output;
    }

    private static JsonNode? Normalize(string name, FieldDef f, JsonNode value, string uploadPrefix)
    {
        var label = f.Label ?? name;
        ApiException Bad(string why) => ApiException.BadRequest($"الحقل '{label}' {why}");

        string AsString(int defaultMax)
        {
            var s = value is JsonValue v && v.TryGetValue<string>(out var str) ? str
                : value is JsonValue n && (n.TryGetValue<double>(out _) || n.TryGetValue<bool>(out _)) ? n.ToJsonString()
                : throw Bad("يجب أن يكون نصاً");
            s = s.Trim();
            var max = (int)Math.Min(f.Max ?? defaultMax, defaultMax);
            if (s.Length > max) throw Bad($"أطول من {max} حرف");
            if (f.Min is { } min && s.Length < min) throw Bad($"أقصر من {min} حرف");
            return s;
        }

        string SafeUrl(string s, bool image)
        {
            if (image && s.StartsWith(uploadPrefix, StringComparison.Ordinal)) return s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) throw Bad("يجب أن يكون رابطاً يبدأ بـ https://");
            return s;
        }

        switch (f.Type)
        {
            case "text": return AsString(500);
            case "longtext": return AsString(10000);
            case "email":
            {
                var s = AsString(256);
                if (!s.Contains('@') || s.Contains(' ')) throw Bad("بريد إلكتروني غير صالح");
                return s.ToLowerInvariant();
            }
            case "phone":
            {
                var s = AsString(20);
                if (!Phone().IsMatch(s)) throw Bad("رقم هاتف غير صالح");
                return s;
            }
            case "url": return SafeUrl(AsString(1000), image: false);
            case "image": return SafeUrl(AsString(1000), image: true);
            case "select":
            {
                var s = AsString(200);
                if (!f.Options!.Contains(s)) throw Bad($"يجب أن يكون أحد: {string.Join("، ", f.Options!)}");
                return s;
            }
            case "number":
            case "integer":
            {
                double d;
                if (value is JsonValue v && v.TryGetValue<double>(out var num)) d = num;
                else if (value is JsonValue sv && sv.TryGetValue<string>(out var str) && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) d = parsed;
                else throw Bad("يجب أن يكون رقماً");
                if (double.IsNaN(d) || double.IsInfinity(d)) throw Bad("يجب أن يكون رقماً");
                if (f.Type == "integer") d = Math.Round(d);
                if (f.Min is { } min && d < min) throw Bad($"أقل من {min}");
                if (f.Max is { } max && d > max) throw Bad($"أكبر من {max}");
                return f.Type == "integer" ? JsonValue.Create((long)d) : JsonValue.Create(d);
            }
            case "boolean":
                if (value is JsonValue b && b.TryGetValue<bool>(out var bv)) return bv;
                if (value is JsonValue bs && bs.TryGetValue<string>(out var bstr) && bool.TryParse(bstr, out var parsedBool)) return parsedBool;
                throw Bad("يجب أن يكون true أو false");
            case "date":
            case "datetime":
            {
                var s = AsString(40);
                if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) throw Bad("تاريخ غير صالح");
                return f.Type == "date" ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }
            case "images":
            case "tags":
            {
                if (value is not JsonArray arr) throw Bad("يجب أن يكون قائمة");
                var max = (int)(f.Max ?? (f.Type == "images" ? 10 : 20));
                if (arr.Count > max) throw Bad($"أكثر من {max} عنصر");
                var result = new JsonArray();
                foreach (var item in arr)
                {
                    var s = item is JsonValue iv && iv.TryGetValue<string>(out var str) ? str.Trim() : throw Bad("يجب أن تكون عناصره نصوصاً");
                    if (f.Type == "images") s = SafeUrl(s, image: true);
                    else if (s.Length > 60) throw Bad("فيه عنصر أطول من 60 حرف");
                    if (s.Length > 0) result.Add(s);
                }
                return result;
            }
            default: throw Bad("نوعه غير مدعوم");
        }
    }

    /// <summary>Copy of the record data without private fields (for visitors).</summary>
    public static JsonObject PublicView(CollectionDef def, JsonObject data)
    {
        var copy = new JsonObject();
        foreach (var (k, v) in data)
            if (!def.Fields.TryGetValue(k, out var f) || !f.Private) copy[k] = v?.DeepClone();
        return copy;
    }
}
