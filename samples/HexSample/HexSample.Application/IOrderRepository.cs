using HexSample.Domain;

namespace HexSample.Application;

public interface IOrderRepository
{
    Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken);
}
