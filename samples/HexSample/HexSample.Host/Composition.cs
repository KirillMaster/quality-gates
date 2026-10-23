using HexSample.Adapters;
using HexSample.Application;

namespace HexSample.Host;

public static class Composition
{
    public static OrderService CreateOrderService() => new(new InMemoryOrderRepository());
}
