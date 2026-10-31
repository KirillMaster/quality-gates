namespace Sample.Domain;

public static class Pricing
{
    public static decimal Discount(decimal amount) => amount > 100 ? amount * 0.9m : amount;

    public static decimal Shipping(decimal weight) => weight > 10 ? weight * 1.5m : 5m;

    public static bool IsFreeShipping(decimal amount) => amount >= 50 && amount != 75;
}
