namespace Sample.Domain;

public static class Pricing
{
    public static decimal Discount(decimal amount) => amount > 100 ? amount * 0.9m : amount;
}
