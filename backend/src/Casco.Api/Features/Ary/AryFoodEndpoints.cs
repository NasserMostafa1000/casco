using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Ary;

/// <summary>
/// Catalogue for the ARY FOOD site. Public reads, admin writes. Uploaded images live on this server
/// and a replaced image is deleted as soon as the new file is stored.
/// </summary>
public static class AryFoodEndpoints
{
    const string LoginEmail = "admin";
    const string DefaultPassword = "123456";
    const long MaxImageBytes = 12 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static void MapAryFood(this WebApplication app)
    {
        var api = app.MapGroup("/api/ary").RequireCors("sites").DisableAntiforgery();
        api.MapGet("/catalog", (Delegate)GetCatalog);
        api.MapGet("/media/{name}", (Delegate)GetMedia);
        api.MapPost("/login", (Delegate)Login).RequireRateLimiting("auth");
        api.MapPut("/password", (Delegate)ChangePassword);
        api.MapPut("/catalog", (Delegate)SaveCatalog);
        api.MapPost("/media", (Delegate)UploadMedia).RequireRateLimiting("uploads");
        api.MapDelete("/media", (Delegate)DeleteMedia);
    }

    static string Root(HttpContext ctx)
    {
        var data = ctx.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value.DataPath;
        var dir = Path.Combine(data, "ary-food");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "media"));
        return dir;
    }

    static string CatalogPath(string root) => Path.Combine(root, "catalog.json");
    static string AuthPath(string root) => Path.Combine(root, "auth.json");

    static async Task<IResult> GetCatalog(HttpContext ctx)
    {
        var path = CatalogPath(Root(ctx));
        if (!File.Exists(path)) return Results.Ok(new { empty = true });
        var json = await File.ReadAllTextAsync(path, ctx.RequestAborted);
        return Results.Text(json, "application/json");
    }

    static IResult GetMedia(string name, HttpContext ctx)
    {
        var file = MediaFile(Root(ctx), name);
        if (file is null || !File.Exists(file)) return Results.NotFound();
        return Results.File(file, ContentType(file));
    }

    static async Task<IResult> Login(LoginBody body, HttpContext ctx)
    {
        var email = (body.Email ?? "").Trim();
        var password = body.Password ?? "";
        if (!string.Equals(email, LoginEmail, StringComparison.OrdinalIgnoreCase) || password.Length == 0)
            return Results.Json(new { message = "البريد أو كلمة المرور غير صحيحة" }, statusCode: 401);

        await Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            var auth = await ReadAuth(Root(ctx));
            if (!Verify(password, auth))
                return Results.Json(new { message = "البريد أو كلمة المرور غير صحيحة" }, statusCode: 401);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            auth.Tokens.RemoveAll(t => t.Expires < DateTime.UtcNow);
            auth.Tokens.Add(new TokenRow(HashToken(token), DateTime.UtcNow.AddDays(14)));
            if (auth.Tokens.Count > 8) auth.Tokens.RemoveRange(0, auth.Tokens.Count - 8);
            await WriteAuth(Root(ctx), auth);
            return Results.Ok(new { token });
        }
        finally { Gate.Release(); }
    }

    static async Task<IResult> ChangePassword(PasswordBody body, HttpContext ctx)
    {
        if (!await Authorized(ctx)) return Results.Json(new { message = "سجّل الدخول أولاً" }, statusCode: 401);
        var next = body.Next ?? "";
        if (next.Length < 6) return Results.Json(new { message = "كلمة المرور الجديدة قصيرة" }, statusCode: 400);
        await Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            var root = Root(ctx);
            var auth = await ReadAuth(root);
            if (!Verify(body.Current ?? "", auth))
                return Results.Json(new { message = "كلمة المرور الحالية غير صحيحة" }, statusCode: 400);
            auth.PasswordHash = HashPassword(next);
            auth.Tokens.Clear();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            auth.Tokens.Add(new TokenRow(HashToken(token), DateTime.UtcNow.AddDays(14)));
            await WriteAuth(root, auth);
            return Results.Ok(new { token });
        }
        finally { Gate.Release(); }
    }

    static async Task<IResult> SaveCatalog(HttpContext ctx)
    {
        if (!await Authorized(ctx)) return Results.Json(new { message = "سجّل الدخول أولاً" }, statusCode: 401);
        var node = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        if (node is not JsonObject obj) return Results.Json(new { message = "بيانات غير صالحة" }, statusCode: 400);
        obj.Remove("passwordHash");
        obj.Remove("tokens");
        var text = obj.ToJsonString(Json);
        if (text.Length > 1_500_000) return Results.Json(new { message = "الكتالوج كبير" }, statusCode: 400);

        await Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            var root = Root(ctx);
            var previous = await ReadCatalog(root);
            await File.WriteAllTextAsync(CatalogPath(root), text, ctx.RequestAborted);
            DeleteDroppedMedia(root, previous, text);
            return Results.Text(text, "application/json");
        }
        finally { Gate.Release(); }
    }

    static async Task<IResult> UploadMedia(HttpContext ctx)
    {
        if (!await Authorized(ctx)) return Results.Json(new { message = "سجّل الدخول أولاً" }, statusCode: 401);
        if (!ctx.Request.HasFormContentType) return Results.Json(new { message = "ارفع ملفاً" }, statusCode: 400);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.Json(new { message = "ارفع ملفاً" }, statusCode: 400);
        if (file.Length > MaxImageBytes) return Results.Json(new { message = "الملف أكبر من ١٢ ميجا" }, statusCode: 400);
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".gif" and not ".mp4")
            return Results.Json(new { message = "نوع الملف غير مدعوم" }, statusCode: 400);

        var name = $"{Guid.NewGuid():N}{ext}";
        var root = Root(ctx);
        var dest = Path.Combine(root, "media", name);
        await using (var stream = File.Create(dest))
            await file.CopyToAsync(stream, ctx.RequestAborted);

        var replace = form["replace"].ToString();
        if (!string.IsNullOrWhiteSpace(replace))
            TryDeleteMedia(root, replace);

        var url = $"{ctx.Request.PathBase}/api/ary/media/{name}";
        return Results.Ok(new { url });
    }

    static async Task<IResult> DeleteMedia(HttpContext ctx)
    {
        if (!await Authorized(ctx)) return Results.Json(new { message = "سجّل الدخول أولاً" }, statusCode: 401);
        var url = ctx.Request.Query["url"].ToString();
        TryDeleteMedia(Root(ctx), url);
        return Results.Ok(new { ok = true });
    }

    static async Task<bool> Authorized(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = header["Bearer ".Length..].Trim();
        if (token.Length < 20) return false;
        var hash = HashToken(token);
        var auth = await ReadAuth(Root(ctx));
        return auth.Tokens.Any(t => t.Hash == hash && t.Expires > DateTime.UtcNow);
    }

    static async Task<AuthFile> ReadAuth(string root)
    {
        var path = AuthPath(root);
        if (!File.Exists(path))
        {
            var fresh = new AuthFile { PasswordHash = HashPassword(DefaultPassword) };
            await WriteAuth(root, fresh);
            return fresh;
        }
        var auth = JsonSerializer.Deserialize<AuthFile>(await File.ReadAllTextAsync(path), Json) ?? new AuthFile();
        if (string.IsNullOrWhiteSpace(auth.PasswordHash))
            auth.PasswordHash = HashPassword(DefaultPassword);
        auth.Tokens ??= [];
        return auth;
    }

    static Task WriteAuth(string root, AuthFile auth) =>
        File.WriteAllTextAsync(AuthPath(root), JsonSerializer.Serialize(auth, Json));

    static async Task<string?> ReadCatalog(string root)
    {
        var path = CatalogPath(root);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    static bool Verify(string password, AuthFile auth) =>
        !string.IsNullOrWhiteSpace(auth.PasswordHash) && FixedEquals(auth.PasswordHash, HashPassword(password, auth.PasswordHash));

    static string HashPassword(string password) => HashPassword(password, null);

    static string HashPassword(string password, string? existing)
    {
        byte[] salt;
        if (existing is not null && existing.Contains('.'))
            salt = Convert.FromBase64String(existing.Split('.')[0]);
        else
            salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static bool FixedEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    static void DeleteDroppedMedia(string root, string? previous, string next)
    {
        foreach (var url in MediaUrls(previous).Except(MediaUrls(next)))
            TryDeleteMedia(root, url);
    }

    static IEnumerable<string> MediaUrls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        const string marker = "/api/ary/media/";
        var index = 0;
        while ((index = json.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + marker.Length;
            var end = start;
            while (end < json.Length && json[end] is not ('"' or '\\' or '?' or '#')) end++;
            if (end > start) yield return json[start..end];
            index = end;
        }
    }

    static void TryDeleteMedia(string root, string urlOrName)
    {
        var name = urlOrName;
        var marker = "/api/ary/media/";
        var at = name.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0) name = name[(at + marker.Length)..];
        var cut = name.IndexOfAny(['?', '#']);
        if (cut >= 0) name = name[..cut];
        var file = MediaFile(root, name);
        if (file is not null && File.Exists(file)) File.Delete(file);
    }

    static string? MediaFile(string root, string name)
    {
        var file = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(file) || file != name.Trim()) return null;
        var path = Path.Combine(root, "media", file);
        var full = Path.GetFullPath(path);
        var media = Path.GetFullPath(Path.Combine(root, "media"));
        var prefix = media.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        _ => "image/jpeg",
    };

    sealed class AuthFile
    {
        public string PasswordHash { get; set; } = "";
        public List<TokenRow> Tokens { get; set; } = [];
    }

    sealed record TokenRow(string Hash, DateTime Expires);
    sealed record LoginBody(string? Email, string? Password);
    sealed record PasswordBody(string? Current, string? Next);
}
