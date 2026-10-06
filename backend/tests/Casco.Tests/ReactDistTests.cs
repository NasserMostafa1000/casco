using System.IO.Compression;
using System.Text;
using Casco.Api.Features.Notifications;
using Casco.Api.Features.Publishing;
using Casco.Api.Infrastructure;

namespace Casco.Tests;

public class ReactDistTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(3, null)]
    [InlineData(2, "2d")]
    [InlineData(0.5, "2d")]
    [InlineData(-0.1, "stopped")]
    [InlineData(-4, null)]
    public void React_hosting_reminder_is_two_days_before_then_stopped(double daysLeft, string? stage) =>
        Assert.Equal(stage, ReminderStages.ForReact(Now.AddDays(daysLeft), Now));

    [Fact]
    public void Extract_strips_a_single_dist_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "casco-react-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var ms = Zip(
                ("dist/index.html", "<!doctype html>"),
                ("dist/assets/app.js", "console.log(1)"),
                ("dist/.DS_Store", "junk"));
            var count = ReactDist.Extract(ms, dir);
            Assert.Equal(2, count);
            Assert.Equal("<!doctype html>", File.ReadAllText(Path.Combine(dir, "index.html")));
            Assert.True(File.Exists(Path.Combine(dir, "assets", "app.js")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Extract_rejects_a_zip_without_index_or_with_a_parent_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "casco-react-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ApiException>(() => ReactDist.Extract(Zip(("readme.txt", "hi")), dir));
        Assert.Throws<ApiException>(() => ReactDist.Extract(Zip(("dist/../secret.txt", "no"), ("dist/index.html", "x")), dir));
        Assert.False(Directory.Exists(dir));
    }

    private static MemoryStream Zip(params (string Path, string Body)[] files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, body) in files)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(body);
            }
        }
        ms.Position = 0;
        return ms;
    }
}
