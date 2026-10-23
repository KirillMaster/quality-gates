using HexSample.Application;
using HexSample.Domain;

namespace HexSample.Adapters;

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly Dictionary<Guid, Order> orders = [];

    public void Add(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        orders[order.Id] = order;
    }

    public Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(orders.GetValueOrDefault(id));
}
