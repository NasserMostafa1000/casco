using Casco.Api.Domain;
using Casco.Api.Features.Admin;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Publishing;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class SuspensionTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ServiceProvider _services;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IOptions<BillingOptions> _billing = Options.Create(new BillingOptions());
    private readonly SiteResolver _resolver;
    private readonly HostingService _hosting;
    private readonly SiteContext _site;
    private readonly SiteSuspension _suspension;
    private readonly User _admin = new() { Email = "admin@b.c", Name = "Admin", Role = "Admin" };

    public SuspensionTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_t.Connection));
        _services = services.BuildServiceProvider();
        var app = Options.Create(new AppOptions { DataPath = Path.GetTempPath(), SitesDomain = "casco.studio" });
        _resolver = new SiteResolver(_services.GetRequiredService<IServiceScopeFactory>(), _cache, app, _billing);
        _hosting = new HostingService(_t.Db, _cache, _resolver, _billing, app, TimeProvider.System);
        _site = new SiteContext(_t.Db, _cache, null!, _billing, TimeProvider.System);
        _suspension = new SiteSuspension(_t.Db, _hosting, TimeProvider.System, NullLogger<SiteSuspension>.Instance);
        _t.Db.Users.Add(_admin);
        _t.Db.SaveChanges();
    }

    public void Dispose()
    {
        _services.Dispose();
        _cache.Dispose();
        _t.Dispose();
    }

    private Project Site(DateTime? paidUntil)
    {
        var owner = new User { Email = $"{Guid.NewGuid():N}@b.c", Name = "Owner" };
        var project = new Project
        {
            UserId = owner.Id, Name = "Shop", SiteKey = "k" + Guid.NewGuid().ToString("N"), Slug = "s" + Guid.NewGuid().ToString("N")[..10],
            PublishedAt = DateTime.UtcNow, HostingTier = HostingTiers.Backend, HostingPaidUntil = paidUntil
        };
        _t.Db.Users.Add(owner);
        _t.Db.Projects.Add(project);
        _t.Db.SaveChanges();
        return project;
    }

    private async Task<bool> Serving(Project p) => (await _resolver.ResolveAsync($"{p.Slug}.casco.studio"))!.Active;

    [Fact]
    public async Task Suspension_stops_a_paid_site_and_lifting_it_keeps_the_paid_hosting()
    {
        var paidUntil = DateTime.UtcNow.AddDays(20);
        var p = Site(paidUntil);
        Assert.True(await Serving(p));
        await _site.ResolveAsync(p.SiteKey);

        await _suspension.SuspendAsync(p.Id, "  منتجات مخالفة  ", _admin.Id);

        Assert.False(await Serving(p));
        var visitor = await Assert.ThrowsAsync<ApiException>(() => _site.ResolveAsync(p.SiteKey));
        Assert.Equal(SiteSuspension.Code, visitor.Code);
        Assert.DoesNotContain("مخالفة", visitor.Message);
        var owner = Assert.Throws<ApiException>(() => SiteSuspension.EnsureNotSuspended(p));
        Assert.Contains("منتجات مخالفة", owner.Message);
        Assert.Equal(_admin.Id, p.AdminSuspendedBy);
        Assert.NotNull(p.AdminSuspendedAt);
        Assert.Equal(paidUntil, p.HostingPaidUntil);
        Assert.True(_hosting.IsActive(p, HostingTiers.Backend));

        await _suspension.UnsuspendAsync(p.Id, _admin.Id);

        Assert.True(await Serving(p));
        await _site.ResolveAsync(p.SiteKey);
        Assert.False(p.IsAdminSuspended);
        Assert.Null(p.AdminSuspensionReason);
        Assert.Equal(paidUntil, p.HostingPaidUntil);
    }

    [Fact]
    public async Task Lifting_a_suspension_does_not_revive_expired_hosting()
    {
        var p = Site(DateTime.UtcNow.AddDays(-30));
        await _suspension.SuspendAsync(p.Id, "مراجعة", _admin.Id);
        await _suspension.UnsuspendAsync(p.Id, _admin.Id);

        Assert.False(await Serving(p));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("ab")]
    public async Task A_reason_is_required(string? reason)
    {
        var p = Site(DateTime.UtcNow.AddDays(5));
        var ex = await Assert.ThrowsAsync<ApiException>(() => _suspension.SuspendAsync(p.Id, reason, _admin.Id));
        Assert.Equal(400, ex.Status);
        Assert.False(p.IsAdminSuspended);
    }
}
