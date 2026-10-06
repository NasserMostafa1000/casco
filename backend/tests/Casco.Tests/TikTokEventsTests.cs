using Casco.Api.Domain;
using Casco.Api.Features.Billing;

namespace Casco.Tests;

public class TikTokEventsTests
{
    [Fact]
    public void Email_hash_matches_tiktok_example()
    {
        Assert.Equal("848a771458438fc2ec420560d769fb9b9b86851ee338ec56517baabd79d3bb4f", TikTokEvents.Hash("alice_abc@gmail.com"));
    }

    [Fact]
    public void Payment_names_match_the_pixel()
    {
        Assert.Equal(("pro-monthly", "Casco Pro monthly"), TikTokEvents.Describe(new Payment { Kind = PaymentKinds.Subscription, Interval = BillingIntervals.Monthly }));
        Assert.Equal(("pro-yearly", "Casco Pro yearly"), TikTokEvents.Describe(new Payment { Kind = PaymentKinds.Subscription, Interval = BillingIntervals.Yearly }));
        Assert.Equal(("credits-100", "Casco credits"), TikTokEvents.Describe(new Payment { Kind = PaymentKinds.Topup, TopupPackId = "credits-100" }));
        Assert.Equal(("hosting-backend-yearly", "Casco hosting with backend"), TikTokEvents.Describe(new Payment { Kind = PaymentKinds.Hosting, HostingTier = HostingTiers.Backend, Interval = BillingIntervals.Yearly }));
    }
}
