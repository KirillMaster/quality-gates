using Sample.Domain;

namespace Sample.Tests;

public class PricingTests
{
    [Fact]
    public void Discount_runs_without_asserting_anything()
    {
        _ = Pricing.Discount(50);
        _ = Pricing.Discount(500);
    }
}
