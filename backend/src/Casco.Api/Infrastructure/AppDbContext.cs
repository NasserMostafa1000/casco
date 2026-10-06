using Casco.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Casco.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CreditEntry> CreditEntries => Set<CreditEntry>();
    public DbSet<CreditReservation> CreditReservations => Set<CreditReservation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WalletTransfer> WalletTransfers => Set<WalletTransfer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectVersion> ProjectVersions => Set<ProjectVersion>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();
    public DbSet<AiProviderInvoice> AiProviderInvoices => Set<AiProviderInvoice>();
    public DbSet<AccountEntry> AccountEntries => Set<AccountEntry>();
    public DbSet<AiResponseCacheEntry> AiResponseCache => Set<AiResponseCacheEntry>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<SiteUser> SiteUsers => Set<SiteUser>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<Ad> Ads => Set<Ad>();
    public DbSet<SiteRecord> SiteRecords => Set<SiteRecord>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<BookingService> BookingServices => Set<BookingService>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<SentNotification> SentNotifications => Set<SentNotification>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<DesktopLogin> DesktopLogins => Set<DesktopLogin>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite stores DateTime without a kind; every timestamp here is UTC (Npgsql already returns UTC).
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true)
            configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.GoogleId).IsUnique();
            e.HasIndex(x => x.AppleId).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.GoogleId).HasMaxLength(255);
            e.Property(x => x.AppleId).HasMaxLength(255);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.Locale).HasMaxLength(5).HasDefaultValue("ar");
            e.Property(x => x.ReferralCode).HasMaxLength(12);
            e.HasIndex(x => x.ReferralCode).IsUnique();
            e.HasIndex(x => x.ReferredByUserId);
            e.HasOne(x => x.Subscription).WithOne().HasForeignKey<Subscription>(x => x.UserId);
        });

        b.Entity<Subscription>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.Plan).HasMaxLength(20);
            e.Property(x => x.Interval).HasMaxLength(20);
        });

        b.Entity<CreditEntry>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Bucket });
            e.Property(x => x.Bucket).HasMaxLength(20);
            e.Property(x => x.Type).HasMaxLength(30);
            e.Property(x => x.Reference).HasMaxLength(100);
        });

        b.Entity<CreditReservation>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => x.TaskId).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.CloseReason).HasMaxLength(16);
        });

        b.Entity<Payment>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ProviderPaymentId);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
        });

        b.Entity<DesktopLogin>(e =>
        {
            e.HasIndex(x => x.UserCode).IsUnique();
            e.HasIndex(x => x.DeviceCodeHash).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.Property(x => x.UserCode).HasMaxLength(16);
            e.Property(x => x.DeviceCodeHash).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(20);
        });

        b.Entity<WalletTransfer>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Method).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.ProofKey).HasMaxLength(80);
            e.Property(x => x.ContentType).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.ReviewNote).HasMaxLength(300);
        });

        b.Entity<Project>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.SiteKey).IsUnique();
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.CustomDomain).IsUnique();
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Slug).HasMaxLength(63);
            e.Property(x => x.SiteKey).HasMaxLength(32);
            e.Property(x => x.CustomDomain).HasMaxLength(253);
            e.Property(x => x.AdminSuspensionReason).HasMaxLength(500);
        });

        b.Entity<ProjectVersion>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
            e.Property(x => x.Prompt).HasMaxLength(1000);
            e.Property(x => x.Model).HasMaxLength(100);
        });
        b.Entity<ChatMessage>(e => e.HasIndex(x => x.ProjectId));

        b.Entity<AgentTask>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Status });
            e.HasIndex(x => x.Status);
            e.Property(x => x.CostUsd).HasPrecision(18, 8);
            e.Property(x => x.Remaining).HasMaxLength(2000);
        });

        b.Entity<AiUsage>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.TaskId);
            e.Property(x => x.EstimatedCostUsd).HasPrecision(18, 8);
            e.Property(x => x.ActualCostUsd).HasPrecision(18, 8);
            e.Property(x => x.Plan).HasMaxLength(16);
        });

        b.Entity<AiProviderInvoice>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.Month }).IsUnique();
            e.Property(x => x.AmountUsd).HasPrecision(18, 4);
            e.Property(x => x.EstimatedUsd).HasPrecision(18, 8);
        });

        b.Entity<AccountEntry>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Cadence).HasMaxLength(8);
            e.Property(x => x.AmountUsd).HasPrecision(12, 2);
            e.HasIndex(x => x.OnDate);
        });

        b.Entity<AiResponseCacheEntry>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.OriginalCostUsd).HasPrecision(18, 8);
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<AppSetting>(e => e.HasKey(x => x.Key));
        b.Entity<FormSubmission>(e => e.HasIndex(x => new { x.ProjectId, x.CreatedAt }));
        b.Entity<SiteUser>(e => e.HasIndex(x => new { x.ProjectId, x.Email }).IsUnique());

        b.Entity<Course>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.HasMany(x => x.Lessons).WithOne().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Enrollment>(e => e.HasIndex(x => new { x.CourseId, x.SiteUserId }).IsUnique());

        b.Entity<Ad>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Status, x.CreatedAt });
            e.Property(x => x.Price).HasPrecision(18, 2);
        });

        b.Entity<SiteRecord>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Collection });
            e.Property(x => x.Collection).HasMaxLength(40);
        });

        b.Entity<Product>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(80);
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.ComparePrice).HasPrecision(18, 2);
        });

        b.Entity<Order>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.ShippingFee).HasPrecision(18, 2);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.PaymentMethod).HasMaxLength(20);
        });

        b.Entity<BookingService>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Price).HasPrecision(18, 2);
        });

        b.Entity<Booking>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.StartUtc });
            e.Property(x => x.Status).HasMaxLength(20);
        });

        b.Entity<SentNotification>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.SentAt);
            e.Property(x => x.Key).HasMaxLength(160);
            e.Property(x => x.Kind).HasMaxLength(40);
        });

        b.Entity<RevokedToken>(e =>
        {
            e.HasKey(x => x.Jti);
            e.Property(x => x.Jti).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=casco;Username=casco;Password=casco")
            .Options;
        return new AppDbContext(options);
    }
}
