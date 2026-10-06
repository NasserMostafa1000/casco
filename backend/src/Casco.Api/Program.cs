using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Casco.Api.Domain;
using Casco.Api.Features.Admin;
using Casco.Api.Features.Agent;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Auth;
using Casco.Api.Features.Backend;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Commerce;
using Casco.Api.Features.Desktop;
using Casco.Api.Features.Monitoring;
using Casco.Api.Features.Notifications;
using Casco.Api.Features.Projects;
using Casco.Api.Features.Publishing;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Casco.Api.Infrastructure.Logging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Log files on the server: {DataPath}/logs/casco-yyyy-MM-dd.log (the data folder is a host volume in Docker).
var errorFeed = new ErrorFeed();
var fileLog = config.GetSection("Logging:File").Get<FileLogOptions>() ?? new FileLogOptions();
var logDir = Path.IsPathRooted(fileLog.Directory) ? fileLog.Directory : Path.Combine(config["App:DataPath"] ?? "data", fileLog.Directory);
builder.Logging.AddProvider(new FileLoggerProvider(logDir, fileLog, errorFeed));
builder.Services.AddSingleton(errorFeed);

builder.Services.Configure<AppOptions>(config.GetSection("App"));
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
builder.Services.Configure<BillingOptions>(config.GetSection("Billing"));
builder.Services.Configure<ZiinaOptions>(config.GetSection("Ziina"));
builder.Services.Configure<AgentOptions>(config.GetSection("Agent"));
builder.Services.Configure<AiOptions>(config.GetSection("Ai"));
builder.Services.Configure<ExternalAuthOptions>(config.GetSection("Auth"));
builder.Services.Configure<TurnstileOptions>(config.GetSection("Turnstile"));
builder.Services.Configure<StorageOptions>(config.GetSection("Storage"));
builder.Services.Configure<SmtpOptions>(config.GetSection("Smtp"));
builder.Services.Configure<MonitorOptions>(config.GetSection("Monitor"));
builder.Services.Configure<StockImageOptions>(config.GetSection("StockImages"));
builder.Services.PostConfigure<AiOptions>(ai =>
{
    // Local development without API keys falls back to the offline fake model.
    if (builder.Environment.IsDevelopment() && ai.Providers.Values.All(p => string.IsNullOrWhiteSpace(p.ApiKey)))
        ai.UseFake = true;
});

var jwtKey = config["Jwt:Key"] ?? "";
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:Key must be set to a random secret of at least 32 characters (env var Jwt__Key).");

var dbProvider = config["Database:Provider"] ?? "Sqlite";
var connectionString = config.GetConnectionString("Default") ?? "Data Source=data/casco.db";
builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (dbProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        o.UseNpgsql(connectionString, npgsql =>
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(3), errorCodesToAdd: null));
    else o.UseSqlite(connectionString);
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = config["Jwt:Issuer"] ?? "casco",
        ValidAudience = TokenService.AppAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            // Tokens issued before logout revocation have no id, so they stop working at this deploy.
            var jti = (ctx.SecurityToken as JsonWebToken)?.Id;
            if (string.IsNullOrEmpty(jti))
            {
                ctx.Fail("revoked");
                return;
            }
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            if (await TokenRevocation.IsRevokedAsync(db, jti, ctx.HttpContext.RequestAborted))
                ctx.Fail("revoked");
        }
    };
});
builder.Services.AddAuthorizationBuilder().AddPolicy("admin", p => p.RequireRole(Roles.Admin));

var corsOrigins = config.GetSection("App:CorsOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod());
    o.AddPolicy("sites", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { message = "طلبات كثيرة، انتظر قليلاً ثم حاول", code = "rate_limited" }, ct);

    static string Key(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";

    void Add(string name, int permits, TimeSpan window) =>
        o.AddPolicy(name, ctx => RateLimitPartition.GetFixedWindowLimiter(Key(ctx),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 }));

    Add("auth", 10, TimeSpan.FromMinutes(1));
    Add("device", 40, TimeSpan.FromMinutes(1));
    Add("desktop", 120, TimeSpan.FromMinutes(1));
    Add("ai", 12, TimeSpan.FromMinutes(1));
    Add("site", 180, TimeSpan.FromMinutes(1));
    Add("forms", 10, TimeSpan.FromMinutes(1));
    Add("uploads", 30, TimeSpan.FromMinutes(1));
    Add("monitor", 20, TimeSpan.FromMinutes(1));
    Add("tiktok", 60, TimeSpan.FromMinutes(1));

    // Endpoints with their own policy keep that policy. Everything else (account, billing, project reads) uses this.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(Key(ctx),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = FloodGuardMiddleware.PermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddSingleton(PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = FloodGuardMiddleware.PermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        })));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // The app is only reachable through Caddy on the private Docker network.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 4 * 1024 * 1024);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<TemplateCatalog>();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<AgentQueue>();
builder.Services.AddSingleton<TaskStreamHub>();
builder.Services.AddSingleton<SiteResolver>();
builder.Services.AddSingleton<PricingService>();
if (config.GetSection("Storage").Get<StorageOptions>()?.UseR2 == true)
    builder.Services.AddSingleton<IUploadStorage, R2UploadStorage>();
else
    builder.Services.AddSingleton<IUploadStorage, LocalUploadStorage>();
builder.Services.Configure<CloudflareOptions>(config.GetSection("Cloudflare"));
builder.Services.AddHttpClient(CloudflareCachePurge.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<CloudflareCachePurge>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddSingleton<ExternalTokenValidator>();
builder.Services.AddHttpClient<TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<FakeChatProvider>();
builder.Services.AddHttpClient<OpenAiCompatibleProvider>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.Configure<TikTokOptions>(config.GetSection("TikTok"));
builder.Services.AddHttpClient(TikTokEvents.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddSingleton<TikTokEvents>();
builder.Services.AddHttpClient<ZiinaClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<StockImages>(c => c.Timeout = TimeSpan.FromSeconds(12));

builder.Services.AddScoped<CreditService>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddSingleton<WalletProofStore>();
builder.Services.AddScoped<WalletTransferService>();
builder.Services.AddScoped<PaymentFulfillment>();
builder.Services.AddScoped<AiClient>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<PublishService>();
builder.Services.AddScoped<AgentTaskRunner>();
builder.Services.AddScoped<HostingService>();
builder.Services.AddScoped<SiteSuspension>();
builder.Services.AddScoped<Casco.Api.Features.Admin.EconomicsService>();
builder.Services.AddScoped<Casco.Api.Features.Admin.AccountsService>();
builder.Services.AddScoped<SiteContext>();
builder.Services.AddScoped<FunctionRunner>();
builder.Services.AddSingleton<RecordStore>();
builder.Services.AddSingleton<EmailQueue>();
builder.Services.AddSingleton<SiteNotifier>();
builder.Services.AddSingleton<ServerMonitor>();
builder.Services.AddSingleton<LeaderElection>();
builder.Services.AddScoped<UserEmails>();

builder.Services.AddHostedService(sp => sp.GetRequiredService<LeaderElection>());
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<EmailSenderService>();
builder.Services.AddHostedService<SiteNotificationSender>();
builder.Services.AddHostedService<SubscriptionMaintenanceService>();
builder.Services.AddHostedService<BillingReminderService>();
builder.Services.AddHostedService<ServerMonitorService>();

var app = builder.Build();
StockImages.BaseUrl = app.Services.GetRequiredService<IOptions<AppOptions>>().Value.PublicUrl.TrimEnd('/') + "/stock";

var dataPath = config["App:DataPath"] ?? "data";
Directory.CreateDirectory(dataPath);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsNpgsql()) await db.Database.MigrateAsync();
    else
    {
        await db.Database.EnsureCreatedAsync();
        // EnsureCreated leaves an existing SQLite file unchanged, so add the logout table when it is missing.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RevokedTokens" (
                "Jti" TEXT NOT NULL CONSTRAINT "PK_RevokedTokens" PRIMARY KEY,
                "ExpiresAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_RevokedTokens_ExpiresAt" ON "RevokedTokens" ("ExpiresAt");""");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "WalletTransfers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_WalletTransfers" PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "Method" TEXT NOT NULL,
                "AmountMinor" INTEGER NOT NULL,
                "Currency" TEXT NOT NULL,
                "ProofKey" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ReviewNote" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ReviewedAt" TEXT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_WalletTransfers_UserId" ON "WalletTransfers" ("UserId");""");
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_WalletTransfers_Status" ON "WalletTransfers" ("Status");""");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "DesktopLogins" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DesktopLogins" PRIMARY KEY,
                "UserCode" TEXT NOT NULL,
                "DeviceCodeHash" TEXT NOT NULL,
                "UserId" TEXT NULL,
                "Status" TEXT NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_DesktopLogins_UserCode" ON "DesktopLogins" ("UserCode");""");
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_DesktopLogins_DeviceCodeHash" ON "DesktopLogins" ("DeviceCodeHash");""");
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_DesktopLogins_ExpiresAt" ON "DesktopLogins" ("ExpiresAt");""");
    }
}
await app.Services.GetRequiredService<PricingService>().LoadAsync();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<Casco.Api.Features.Admin.AccountsService>().ImportLegacyAsync();

if (app.Services.GetRequiredService<IUploadStorage>() is R2UploadStorage r2)
{
    var cdn = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.R2.PublicUrl;
    if (string.IsNullOrWhiteSpace(cdn))
        app.Logger.LogWarning("Storage:R2:PublicUrl is empty, so customer images and videos are still downloaded through this server. Set it to the bucket's public CDN domain.");
    try { await r2.ListAsync("_healthcheck"); }
    catch (Exception e)
    {
        app.Logger.LogError("Cloudflare R2 storage check failed ({Error}). Image uploads will fail until the R2 API token has " +
                            "Object Read & Write permission on Storage:R2:BucketName.", e.Message);
    }
}

app.UseForwardedHeaders();
app.UseApiErrors();
app.UseMiddleware<FloodGuardMiddleware>();
app.UseMiddleware<SiteHostingMiddleware>();
if (!app.Services.GetRequiredService<IOptions<AppOptions>>().Value.SeparateFrontend)
    app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", (LeaderElection leader) => Results.Ok(new { status = "ok", leader = leader.IsLeader })).DisableRateLimiting();
app.MapMonitorIngest();
app.MapAuthEndpoints();
app.MapDesktopAuthEndpoints();
app.MapDesktopAgentEndpoints();
app.MapTikTokEvents();
app.MapAccountEndpoints();
app.MapBillingEndpoints();
app.MapProjectEndpoints();
app.MapReactHost();
app.MapTaskStreamEndpoints();
app.MapPublishingEndpoints();
app.MapSiteRuntimeEndpoints();
app.MapSiteDataEndpoints();
app.MapCollectionEndpoints();
app.MapFunctionEndpoints();
app.MapStoreEndpoints();
app.MapBookingEndpoints();
app.MapAdminEndpoints();
UploadService.MapUploadFiles(app);
StockImages.Map(app);
app.MapGet("/api/templates", (TemplateCatalog templates) =>
    Results.Ok(templates.All.Select(t => new { t.Key, t.Name, t.Description, t.Plan, t.Modules })));

// Builder SPA (production build copied into wwwroot), or a redirect when it is hosted on Cloudflare Pages.
app.MapFallback(async ctx =>
{
    var appOpt = ctx.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value;
    if (appOpt.SeparateFrontend && !ctx.Request.Path.StartsWithSegments("/api") && HttpMethods.IsGet(ctx.Request.Method))
    {
        ctx.Response.Redirect(appOpt.FrontendBase + ctx.Request.Path + ctx.Request.QueryString);
        return;
    }
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (ctx.Request.Path.StartsWithSegments("/api") || !File.Exists(index))
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(index);
});

app.Run();

public partial class Program;
