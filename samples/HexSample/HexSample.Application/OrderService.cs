using HexSample.Domain;

namespace HexSample.Application;

public sealed class OrderService(IOrderRepository repository)
{
    public async Task<decimal> TotalAsync(Guid id, CancellationToken cancellationToken)
    {
        Order? order = await repository.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return order?.Total ?? 0m;
    }
}
