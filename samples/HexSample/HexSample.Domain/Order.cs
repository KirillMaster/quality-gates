namespace HexSample.Domain;

public sealed class Order
{
    public Order(Guid id, decimal total)
    {
        Id = id;
        Total = total;
    }

    public Guid Id { get; }

    public decimal Total { get; }
}
