using Casco.Api.Features.Agent;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Features.Sites;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class StockImagesTests
{
    [Theory]
    [InlineData("1200x800", 1200, 800)]
    [InlineData("400X400", 400, 400)]
    public void Parses_sizes(string size, int w, int h)
    {
        Assert.True(StockImages.TryParseSize(size, out var pw, out var ph));
        Assert.Equal((w, h), (pw, ph));
    }

    [Theory]
    [InlineData("1200")]
    [InlineData("0x800")]
    [InlineData("9999x800")]
    [InlineData("axb")]
    public void Rejects_bad_sizes(string size) => Assert.False(StockImages.TryParseSize(size, out _, out _));

    [Fact]
    public void Keywords_are_cleaned_and_capped()
    {
        Assert.Equal(["dentist", "patient", "clinic"], StockImages.Keywords("Dentist-patient,clinic"));
        Assert.Equal(["a", "b", "c", "d", "e"], StockImages.Keywords("a-b-c-d-e-f-g"));
        Assert.Empty(StockImages.Keywords("--<>--"));
    }

    [Fact]
    public async Task Without_a_pexels_key_falls_back_to_keyword_photos_that_stay_the_same()
    {
        var stock = new StockImages(new HttpClient(), new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new StockImageOptions()), NullLogger<StockImages>.Instance);
        var url = await stock.ResolveAsync(["burger", "restaurant", "food"], 800, 600, 0, default);
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.DoesNotContain("picsum.photos", url);
        Assert.Equal(url, await stock.ResolveAsync(["burger", "restaurant", "food"], 800, 600, 0, default));
        Assert.NotEqual(url, await stock.ResolveAsync(["dentist", "clinic", "patient"], 800, 600, 0, default));
    }

    [Fact]
    public void Detects_videos_but_not_iphone_photos()
    {
        static byte[] Ftyp(string brand) => [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', .. System.Text.Encoding.ASCII.GetBytes(brand)];
        Assert.Equal(".mp4", UploadService.DetectVideoExtension(Ftyp("isom")));
        Assert.Equal(".mov", UploadService.DetectVideoExtension(Ftyp("qt  ")));
        Assert.Equal(".webm", UploadService.DetectVideoExtension([0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(UploadService.DetectVideoExtension(Ftyp("heic")));
        Assert.Null(UploadService.DetectVideoExtension([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("https://cdn.casco.studio/casco/uploads/abc/o-1.webp",
            R2UploadStorage.CdnObjectUrl("https://cdn.casco.studio/", "casco/uploads/", "abc/o-1.webp"));
        Assert.Null(R2UploadStorage.CdnObjectUrl("  ", "casco/uploads/", "abc/o-1.webp"));
        Assert.False(UploadService.IsVideo("o-a.webp"));
        Assert.Equal("cdn.casco.studio/casco/uploads/abc/", CloudflareCachePurge.CachePrefix("https://cdn.casco.studio/casco/uploads/abc"));
        Assert.Null(CloudflareCachePurge.CachePrefix("not a url"));
    }

    [Fact]
    public void Templates_and_prompt_point_photos_at_the_stock_endpoint()
    {
        var files = SiteFiles.ReplaceTokens(new Dictionary<string, string> { ["index.html"] = "<img src=\"{{STOCK}}/800x500/shop\">" }, "A", "B");
        Assert.Equal($"<img src=\"{StockImages.BaseUrl}/800x500/shop\">", files["index.html"]);
        Assert.Contains(StockImages.BaseUrl + "/1200x800/", PromptBuilder.SystemPrompt());
        Assert.Contains("https://nasser.chat", PromptBuilder.SystemPrompt());
        Assert.DoesNotContain("picsum", PromptBuilder.SystemPrompt());
        Assert.Equal(PromptBuilder.LayoutDirection("عيادة أسنان"), PromptBuilder.LayoutDirection("عيادة أسنان"));
        Assert.NotEqual(PromptBuilder.LayoutDirection("عيادة أسنان"), PromptBuilder.LayoutDirection("مطعم برجر وبطاطس"));
    }

    [Fact]
    public void Broken_internet_photos_become_stock_urls_and_real_ones_stay()
    {
        StockImages.BaseUrl = "https://api.casco.studio/stock";
        var html = """
            <img src="https://images.unsplash.com/photo-123-burger.jpg" alt="">
            <img src="https://api.casco.studio/stock/800x500/dentist-clinic">
            <img src="https://cdn.casco.studio/casco/uploads/abc/a.webp">
            <video src="clip.mp4"></video>
            <img src="assets/logo.svg">
            <div style="background:url('https://images.pexels.com/photos/9/fries.png')"></div>
            """;
        var fixedHtml = StockImages.FixImages(html);
        Assert.Contains("https://api.casco.studio/stock/1200x800/photo-123-burger", fixedHtml);
        Assert.Contains("https://api.casco.studio/stock/800x500/dentist-clinic", fixedHtml);
        Assert.Contains("https://cdn.casco.studio/casco/uploads/abc/a.webp", fixedHtml);
        Assert.Contains("clip.mp4", fixedHtml);
        Assert.Contains("assets/logo.svg", fixedHtml);
        Assert.Contains("https://api.casco.studio/stock/1200x800/fries?i=1", fixedHtml);
        Assert.DoesNotContain("unsplash", fixedHtml);
        Assert.DoesNotContain("pexels", fixedHtml);
    }
}
