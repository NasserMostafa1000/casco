namespace Casco.Api.Infrastructure;

public class AppOptions
{
    /// <summary>Public base URL of the API (also previews, uploads and the site SDK), e.g. https://api.casco.studio</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";
    /// <summary>URL of the builder UI when it is hosted separately (e.g. Cloudflare Pages): payment redirects,
    /// email links and the "made with Casco" badge. Defaults to PublicUrl (the API serves the UI itself).</summary>
    public string? FrontendUrl { get; set; }
    /// <summary>Hosts that serve the platform itself (not customer sites).</summary>
    public string[] Hosts { get; set; } = ["localhost", "127.0.0.1"];
    /// <summary>Published sites live on {slug}.{SitesDomain}.</summary>
    public string SitesDomain { get; set; } = "localhost";
    public string SitesScheme { get; set; } = "http";
    /// <summary>Port appended to site URLs in development (empty in production).</summary>
    public string? SitesPort { get; set; } = "5080";
    /// <summary>Public IPs of the sites server, used to verify custom domains (A records).</summary>
    public string[] ServerIps { get; set; } = [];
    /// <summary>Hostname customers should CNAME their domain to.</summary>
    public string CnameTarget { get; set; } = "sites.casco.studio";
    public string DataPath { get; set; } = "data";
    public string[] AdminEmails { get; set; } = [];
    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];

    public string FrontendBase => (string.IsNullOrWhiteSpace(FrontendUrl) ? PublicUrl : FrontendUrl).TrimEnd('/');

    public bool SeparateFrontend => !string.Equals(FrontendBase, PublicUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>What follows the chosen name in a site address, e.g. ".casco.studio".</summary>
    public string SiteSuffix => $".{SitesDomain}{(string.IsNullOrEmpty(SitesPort) ? "" : ":" + SitesPort)}";

    public string SiteUrl(string slug) => $"{SitesScheme}://{slug}{SiteSuffix}";
}

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "casco";
    public int ExpiryDays { get; set; } = 14;
}

public class BillingOptions
{
    /// <summary>Real provider cost (USD) covered by one credit.</summary>
    public decimal UsdPerCredit { get; set; } = 0.001m;
    public string Currency { get; set; } = "USD";
    public int SignupBonusCredits { get; set; } = 0;
    public FreePlanOptions Free { get; set; } = new();
    public ProPlanOptions Pro { get; set; } = new();
    public HostingOptions Hosting { get; set; } = new();
    public List<TopupPack> TopupPacks { get; set; } = [];
    public EconomicsOptions Economics { get; set; } = new();

    public static string Dollars(int minor) => $"${minor / 100m:0.##}";
}

public class OperatingExpense
{
    public string Name { get; set; } = "";
    public decimal MonthlyUsd { get; set; }
}

/// <summary>Fixed costs used by the admin unit-economics report (AI costs come from the AiUsage table).</summary>
public class EconomicsOptions
{
    /// <summary>Monthly server bill (e.g. both Contabo VPS + backups), spread over hosted sites.</summary>
    public decimal InfraMonthlyUsd { get; set; }
    /// <summary>Monthly domain / DNS cost (e.g. casco.studio registrar + Cloudflare).</summary>
    public decimal DomainMonthlyUsd { get; set; }
    /// <summary>Monthly email mailbox / SMTP cost (e.g. Hostinger no-reply@).</summary>
    public decimal EmailMonthlyUsd { get; set; }
    /// <summary>Named monthly costs of running Casco (servers, domain, email, Apple, anything else).</summary>
    public List<OperatingExpense> OperatingExpenses { get; set; } = [];
    /// <summary>Payment gateway fee taken from each payment (Ziina), in percent.</summary>
    public decimal PaymentFeePercent { get; set; }

    public decimal PlatformMonthlyUsd => OperatingExpenses.Count > 0
        ? OperatingExpenses.Sum(e => e.MonthlyUsd)
        : InfraMonthlyUsd + DomainMonthlyUsd + EmailMonthlyUsd;
}

/// <summary>Per-site monthly hosting, billed separately from the Pro (editing) plan.</summary>
public class HostingOptions
{
    public int StaticMonthlyMinor { get; set; } = 500;
    public int BackendMonthlyMinor { get; set; } = 1000;
    /// <summary>A yearly payment costs this many months (12 months for the price of 10).</summary>
    public int YearlyPricedMonths { get; set; } = 10;
    /// <summary>Published sites keep running this long after hosting expires. React dist hosting has no grace period.</summary>
    public int GraceDays { get; set; } = 3;
    /// <summary>One year of hosting for an uploaded React build.</summary>
    public int ReactYearlyMinor { get; set; } = 1000;

    public int GraceDaysFor(string? tier) => tier == Domain.HostingTiers.React ? 0 : GraceDays;

    public int MonthlyMinor(string tier) => tier == Domain.HostingTiers.Backend ? BackendMonthlyMinor : StaticMonthlyMinor;
    public int PriceMinor(string tier, string interval)
    {
        if (tier == Domain.HostingTiers.React) return ReactYearlyMinor;
        return MonthlyMinor(tier) * (interval == Domain.BillingIntervals.Yearly ? YearlyPricedMonths : 1);
    }
}

public class FreePlanOptions
{
    public int MaxProjects { get; set; } = 1;
}

public class ProPlanOptions
{
    public int MonthlyPriceMinor { get; set; } = 1800;
    public int YearlyPriceMinor { get; set; } = 18000;
    public int MonthlyCredits { get; set; } = 5000;
    public int MaxProjects { get; set; } = 20;
}

public class TopupPack
{
    public string Id { get; set; } = "";
    public int Credits { get; set; }
    public int PriceMinor { get; set; }
}

public class ZiinaOptions
{
    public string BaseUrl { get; set; } = "https://api-v2.ziina.com/api/";
    public string ApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public bool TestMode { get; set; } = true;
    public bool EnforceIpAllowlist { get; set; }
    public string[] AllowedIps { get; set; } = ["3.29.184.186", "3.29.190.95", "20.233.47.127", "13.202.161.181"];
}

public class AgentOptions
{
    /// <summary>Concurrent agent tasks. Workers mostly wait on AI providers, and a multi-part build keeps one busy for minutes.</summary>
    public int Workers { get; set; } = 8;
    /// <summary>AI calls per part: the first call plus automatic fix rounds.</summary>
    public int MaxAiCallsPerTask { get; set; } = 3;
    public int MaxOutputTokens { get; set; } = 16000;
    /// <summary>Time limit for each part of a task.</summary>
    public int TaskTimeoutSeconds { get; set; } = 180;
    public Dictionary<string, int> ReserveCredits { get; set; } = new() { ["cheap"] = 20, ["standard"] = 80, ["premium"] = 400 };
    /// <summary>
    /// A request may start with less than its full reservation (the last credits of the month) as long as this many are left.
    /// The first AI call can then overrun the balance slightly; the overrun is taken from the next credits.
    /// </summary>
    public Dictionary<string, int> MinCreditsToStart { get; set; } = new() { ["cheap"] = 5, ["standard"] = 10, ["premium"] = 100 };
    /// <summary>Free plan (single page) site limits.</summary>
    public int MaxFiles { get; set; } = 30;
    public int MaxFileBytes { get; set; } = 200_000;
    public int MaxTotalBytes { get; set; } = 1_000_000;
    /// <summary>Pro site limits: technical safety caps only, the real limit is the user's credits.</summary>
    public int ProMaxFiles { get; set; } = 200;
    public int ProMaxTotalBytes { get; set; } = 5_000_000;
    /// <summary>
    /// A request too big for one reply is built in parts: the model returns "remaining" and is called again,
    /// up to this many parts per message, while the user's credits last.
    /// </summary>
    public int MaxStepsPerTask { get; set; } = 8;
    /// <summary>Older versions beyond this are deleted (never the current or published one); each version is a full copy of the site.</summary>
    public int KeepVersions { get; set; } = 50;
    /// <summary>Retries of the same model after a rate limit or overloaded provider (not billed), before falling back to the next model.</summary>
    public int ProviderRetries { get; set; } = 4;
    /// <summary>How often the worker looks for tasks that died mid-run and credit holds that outlived their task.</summary>
    public int ReconcileMinutes { get; set; } = 2;
    public int ContextBytesLimit { get; set; } = 60_000;
    public int HistoryMessages { get; set; } = 6;
    public int ResponseCacheDays { get; set; } = 7;
}
