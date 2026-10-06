using System.Text.Json;

namespace Casco.Api.Features.Sites;

public record SiteTemplate(
    string Key,
    string Name,
    string Description,
    string Plan,
    string[] Modules,
    string[] Keywords,
    IReadOnlyDictionary<string, string> Files);

public class TemplateCatalog
{
    private readonly Dictionary<string, SiteTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);

    public TemplateCatalog(IWebHostEnvironment env, ILogger<TemplateCatalog> logger)
        : this(Path.Combine(env.ContentRootPath, "SiteTemplates"), logger) { }

    public TemplateCatalog(string root, ILogger logger)
    {
        if (!Directory.Exists(root))
        {
            logger.LogWarning("SiteTemplates folder not found at {Root}", root);
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "template.json");
            if (!File.Exists(manifestPath)) continue;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Key)) continue;

            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (rel == "template.json" || !SiteFiles.IsAllowedPath(rel)) continue;
                files[rel] = File.ReadAllText(file);
            }

            _templates[manifest.Key] = new SiteTemplate(manifest.Key, manifest.Name ?? manifest.Key,
                manifest.Description ?? "", manifest.Plan ?? "pro", manifest.Modules ?? [], manifest.Keywords ?? [], files);
        }
        logger.LogInformation("Loaded {Count} site templates", _templates.Count);
    }

    public IReadOnlyCollection<SiteTemplate> All => _templates.Values;

    public SiteTemplate? Get(string key) => _templates.GetValueOrDefault(key);

    public SiteTemplate Default => Get("landing") ?? _templates.Values.First();

    /// <summary>Picks a template from the user's description using keywords only, so no AI call is spent.</summary>
    public SiteTemplate Detect(string text)
    {
        var lower = text.ToLowerInvariant();
        // Ties go to the landing page: it is the cheapest to host, and "coffee shop" should not become a store.
        var best = _templates.Values
            .Select(t => (t, score: t.Keywords.Count(k => lower.Contains(k.ToLowerInvariant()))))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.t.Key == Default.Key ? 0 : 1)
            .FirstOrDefault();
        return best.score > 0 ? best.t : Default;
    }

    private sealed class Manifest
    {
        public string Key { get; set; } = "";
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Plan { get; set; }
        public string[]? Modules { get; set; }
        public string[]? Keywords { get; set; }
    }
}
