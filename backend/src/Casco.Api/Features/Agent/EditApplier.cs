using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jint;
using Casco.Api.Features.Sites;

namespace Casco.Api.Features.Agent;

public class AiEditResponse
{
    [JsonPropertyName("thinking")] public string? Thinking { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("files")] public List<AiFileOp>? Files { get; set; }
    /// <summary>Set when the request was too big for one reply: what is still left to build.</summary>
    [JsonPropertyName("remaining")] public string? Remaining { get; set; }
    /// <summary>Files from the site map the model wants to see before editing.</summary>
    [JsonPropertyName("read")] public List<string>? Read { get; set; }
    /// <summary>Choices the model needs from the user before it can change the site.</summary>
    [JsonPropertyName("questions")] public List<AiQuestion>? Questions { get; set; }
}

public class AiQuestion
{
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("options")] public List<AiOption>? Options { get; set; }
}

public class AiOption
{
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
}

public class AiFileOp
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("edits")] public List<AiEdit>? Edits { get; set; }
}

public class AiEdit
{
    [JsonPropertyName("find")] public string? Find { get; set; }
    [JsonPropertyName("replace")] public string? Replace { get; set; }
}

public record EditConstraints(bool SinglePage, int MaxFiles, int MaxFileBytes, int MaxTotalBytes);

/// <param name="LimitReached">The site hit its file-count or total-size cap; retrying cannot help.</param>
public record ApplyResult(SortedDictionary<string, string> Files, List<string> Errors, HashSet<string> Changed, HashSet<string> Affected,
    bool LimitReached = false);

public static partial class EditApplier
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static AiEditResponse? Parse(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            text = firstNewline >= 0 ? text[(firstNewline + 1)..] : text;
            if (text.EndsWith("```")) text = text[..^3];
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<AiEditResponse>(text[start..(end + 1)], JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static ApplyResult Apply(IReadOnlyDictionary<string, string> current, AiEditResponse response, EditConstraints c)
    {
        var files = new SortedDictionary<string, string>(current.ToDictionary(), StringComparer.Ordinal);
        var errors = new List<string>();
        var changed = new HashSet<string>();
        var affected = new HashSet<string>();
        var limitReached = false;

        foreach (var op in response.Files ?? [])
        {
            var path = (op.Path ?? "").Trim().TrimStart('/').Replace('\\', '/');
            if (!SiteFiles.IsAllowedPath(path))
            {
                errors.Add($"Invalid path \"{path}\". Use paths like page.html or assets/app.js.");
                continue;
            }

            switch ((op.Op ?? "").ToLowerInvariant())
            {
                case "write":
                    if (op.Content is null) { errors.Add($"write {path}: missing content"); break; }
                    if (c.SinglePage && SiteFiles.IsHtml(path) && path != "index.html")
                    {
                        errors.Add($"Free plan allows a single page: do not create {path}; put the content in index.html sections instead.");
                        break;
                    }
                    if (!files.ContainsKey(path) && files.Count >= c.MaxFiles)
                    {
                        errors.Add($"Too many files (max {c.MaxFiles}): {path} was not created.");
                        limitReached = true;
                        break;
                    }
                    if (Encoding.UTF8.GetByteCount(op.Content) > c.MaxFileBytes) { errors.Add($"{path} is too large (max {c.MaxFileBytes / 1000}KB)."); break; }
                    files[path] = op.Content;
                    changed.Add(path);
                    break;

                case "delete":
                    if (path == "index.html") { errors.Add("index.html cannot be deleted."); break; }
                    if (files.Remove(path)) changed.Add(path);
                    break;

                case "edit":
                    if (!files.TryGetValue(path, out var content))
                    {
                        errors.Add($"edit {path}: file does not exist (use op \"write\" to create it).");
                        break;
                    }
                    foreach (var edit in op.Edits ?? [])
                    {
                        if (string.IsNullOrEmpty(edit.Find)) { errors.Add($"edit {path}: empty find"); affected.Add(path); continue; }
                        var replaced = ReplaceOnce(content, edit.Find, edit.Replace ?? "");
                        if (replaced is null)
                        {
                            errors.Add($"edit {path}: this find text was not found: \"{Preview(edit.Find)}\"");
                            affected.Add(path);
                            continue;
                        }
                        content = replaced;
                        changed.Add(path);
                    }
                    if (Encoding.UTF8.GetByteCount(content) > c.MaxFileBytes) { errors.Add($"{path} is too large after edits."); break; }
                    files[path] = content;
                    break;

                default:
                    errors.Add($"Unknown op \"{op.Op}\" for {path}.");
                    break;
            }
        }

        if (SiteFiles.TotalBytes(files) > c.MaxTotalBytes)
            return new ApplyResult(new SortedDictionary<string, string>(current.ToDictionary(), StringComparer.Ordinal),
                ["The website became too large; keep files smaller."], [], affected, LimitReached: true);

        return new ApplyResult(files, errors, changed, affected, limitReached);
    }

    /// <summary>Exact match first, then a whitespace-tolerant match (models often alter indentation).</summary>
    public static string? ReplaceOnce(string content, string find, string replace)
    {
        var idx = content.IndexOf(find, StringComparison.Ordinal);
        if (idx >= 0) return string.Concat(content.AsSpan(0, idx), replace, content.AsSpan(idx + find.Length));

        var trimmed = find.Trim();
        if (trimmed.Length > 0 && trimmed != find)
        {
            idx = content.IndexOf(trimmed, StringComparison.Ordinal);
            if (idx >= 0) return string.Concat(content.AsSpan(0, idx), replace.Trim(), content.AsSpan(idx + trimmed.Length));
        }

        var tokens = Whitespace().Split(trimmed).Where(t => t.Length > 0).Select(Regex.Escape).ToArray();
        if (tokens.Length == 0) return null;
        Match match;
        try { match = new Regex(string.Join(@"\s*", tokens), RegexOptions.None, TimeSpan.FromSeconds(1)).Match(content); }
        catch (RegexMatchTimeoutException) { return null; }
        return match.Success
            ? string.Concat(content.AsSpan(0, match.Index), replace, content.AsSpan(match.Index + match.Length))
            : null;
    }

    private static string Preview(string s)
    {
        var one = Whitespace().Replace(s, " ").Trim();
        return one.Length <= 120 ? one : one[..120] + "…";
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}

public static partial class SiteValidator
{
    [GeneratedRegex(@"href\s*=\s*[""']([^""'#?:]+\.html)", RegexOptions.IgnoreCase)] private static partial Regex LocalLinks();
    private const string MissingPage = "which does not exist; create it or fix the link.";

    /// <summary>Links to pages that are planned for a later part of a multi-part build are not errors yet.</summary>
    public static bool IsMissingPage(string error) => error.EndsWith(MissingPage, StringComparison.Ordinal);

    /// <summary>Shared header/navigation/footer script of multi-page sites; once it exists every page must load it.</summary>
    public const string LayoutScript = "assets/layout.js";
    [GeneratedRegex(@"<script\b[^>]*\bsrc\s*=\s*[""'](?:\./|/)?assets/layout\.js", RegexOptions.IgnoreCase)] private static partial Regex LoadsLayout();

    [GeneratedRegex(@"<script\b", RegexOptions.IgnoreCase)] private static partial Regex ScriptOpen();
    [GeneratedRegex(@"</script>", RegexOptions.IgnoreCase)] private static partial Regex ScriptClose();

    /// <summary>A new site is React: each HTML page is a shell, and a module imports React.</summary>
    public static List<string> ReactShellErrors(IReadOnlyDictionary<string, string> files, IEnumerable<string> paths)
    {
        var errors = new List<string>();
        var html = paths.Where(p => files.ContainsKey(p) && SiteFiles.IsHtml(p)).Distinct().ToList();
        if (html.Count == 0) return errors;
        var react = files.Any(f => f.Key.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && f.Value.Contains("esm.sh/react", StringComparison.Ordinal));
        if (!react)
            errors.Add("The site must be React. Add a module that imports React from https://esm.sh/react@19, react-dom from https://esm.sh/react-dom@19/client, and htm from https://esm.sh/htm, then renders into #root. Do not build the page with vanilla JavaScript.");
        foreach (var path in html)
        {
            var content = files[path];
            var shell = (content.Contains("id=\"root\"", StringComparison.Ordinal) || content.Contains("id='root'", StringComparison.Ordinal))
                        && content.Contains("tailwindcss", StringComparison.OrdinalIgnoreCase);
            if (!shell)
                errors.Add($"{path} must be a thin HTML shell: the Tailwind script, the @theme block, <div id=\"root\"></div>, and <script type=\"module\" src=\"assets/....js\"></script>. Put the design in the React module, not in the HTML body.");
        }
        return errors;
    }

    /// <summary>Cheap static checks that catch the most common broken outputs and feed the auto-fix loop.</summary>
    public static List<(string Path, string Error)> Validate(IReadOnlyDictionary<string, string> files, bool singlePage)
    {
        var errors = new List<(string, string)>();
        if (!files.ContainsKey("index.html")) errors.Add(("index.html", "index.html is missing."));

        foreach (var (path, content) in files.Where(f => SiteFiles.IsHtml(f.Key)))
        {
            if (singlePage && path != "index.html")
                errors.Add((path, $"Free plan allows only index.html; remove {path}."));
            if (content.IndexOf("</html>", StringComparison.OrdinalIgnoreCase) < 0)
                errors.Add((path, $"{path} looks truncated (missing </html>)."));
            if (ScriptOpen().Matches(content).Count != ScriptClose().Matches(content).Count)
                errors.Add((path, $"{path} has unbalanced <script> tags."));
            if (content.Length > 700 && content.Count(c => c == '\n') < 8)
                errors.Add((path, $"{path} is packed onto too few lines. Rewrite it with a newline after every statement. A minified file hides the error that leaves the page white."));
            if (!singlePage && files.ContainsKey(LayoutScript) && !LoadsLayout().IsMatch(content))
                errors.Add((path, $"{path} does not load the shared {LayoutScript}; add <script src=\"{LayoutScript}\"></script> and remove its own copy of the header/navigation/footer."));

            foreach (Match m in LocalLinks().Matches(content))
            {
                var target = m.Groups[1].Value.TrimStart('.', '/');
                if (!files.ContainsKey(target))
                    errors.Add((path, singlePage
                        ? $"{path} links to {target} which cannot exist on the free plan; link to an in-page #section instead."
                        : $"{path} links to {target} {MissingPage}"));
            }
        }
        foreach (var (path, content) in files.Where(f => f.Key.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !f.Key.StartsWith("server/", StringComparison.Ordinal)))
        {
            if (content.Length > 700 && content.Count(c => c == '\n') < 8)
                errors.Add((path, $"{path} is packed onto too few lines. Rewrite it with a newline after every statement. A minified file hides the error that leaves the page white."));
            if (JavaScriptSyntax(content) is { } syntax)
                errors.Add((path, $"{path} has a JavaScript syntax error ({syntax}). The page stays white until this is fixed."));
        }
        ValidateBackend(files, errors);
        return errors.DistinctBy(e => e.Item2).ToList();
    }

    private static readonly Regex ImportLine = new(@"^\s*import\s.+;?\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Null when the script parses. Import lines are removed first because the checker is not a browser module loader.</summary>
    public static string? JavaScriptSyntax(string code)
    {
        var body = ImportLine.Replace(code, "");
        body = body.Replace("export default ", "").Replace("export ", "");
        try
        {
            Engine.PrepareScript(body, "page.js", strict: false);
            return null;
        }
        catch (Exception e)
        {
            var message = e.Message.Replace("page.js: ", "");
            return message.Length > 220 ? message[..220] : message;
        }
    }

    [GeneratedRegex(@"Casco\s*\.\s*(?:db\s*\.\s*(?:collection|list|get|create|update|remove)\s*\(\s*|fn\s*\(\s*)[""']([A-Za-z0-9_]+)[""']")]
    private static partial Regex BackendReference();

    private static void ValidateBackend(IReadOnlyDictionary<string, string> files, List<(string, string)> errors)
    {
        var config = Backend.BackendConfig.Empty;
        if (files.TryGetValue(Backend.BackendConfig.FileName, out var json))
        {
            config = Backend.BackendConfig.Parse(json, out var configErrors);
            errors.AddRange(configErrors.Select(e => (Backend.BackendConfig.FileName, e)));
        }

        foreach (var path in files.Keys.Where(p => p.StartsWith("server/", StringComparison.Ordinal) && p != Backend.BackendConfig.FunctionsFile))
            errors.Add((path, $"Only {Backend.BackendConfig.FunctionsFile} is allowed inside server/; move this code there."));

        if (files.TryGetValue(Backend.BackendConfig.FunctionsFile, out var code))
        {
            if (Backend.FunctionRunner.SyntaxError(code) is { } syntax)
                errors.Add((Backend.BackendConfig.FunctionsFile, $"{Backend.BackendConfig.FunctionsFile} has a syntax error: {syntax}"));
            foreach (var fn in config.Functions.Keys)
                if (!Regex.IsMatch(code, $@"function\s+{fn}\s*\("))
                    errors.Add((Backend.BackendConfig.FunctionsFile, $"Function '{fn}' is declared in {Backend.BackendConfig.FileName} but not defined as a top-level function in {Backend.BackendConfig.FunctionsFile}."));
        }
        else if (config.Functions.Count > 0)
            errors.Add((Backend.BackendConfig.FileName, $"Functions are declared but {Backend.BackendConfig.FunctionsFile} does not exist."));

        foreach (var (path, content) in files.Where(f => SiteFiles.IsHtml(f.Key) || f.Key.EndsWith(".js", StringComparison.Ordinal) && !f.Key.StartsWith("server/")))
        {
            foreach (Match m in BackendReference().Matches(content))
            {
                var name = m.Groups[1].Value;
                var isFunction = m.Value.Contains("fn", StringComparison.Ordinal) && !m.Value.Contains("db", StringComparison.Ordinal);
                if (isFunction && !config.Functions.ContainsKey(name))
                    errors.Add((path, $"{path} calls Casco.fn('{name}') but '{name}' is not declared in {Backend.BackendConfig.FileName} \"functions\"."));
                else if (!isFunction && !config.Collections.ContainsKey(name))
                    errors.Add((path, $"{path} uses collection '{name}' which is not declared in {Backend.BackendConfig.FileName} \"collections\"."));
            }
        }
    }
}
