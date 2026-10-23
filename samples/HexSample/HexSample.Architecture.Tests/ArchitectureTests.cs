using System.Reflection;
using HexSample.Application;
using HexSample.Domain;
using KirillMaster.QualityGates.Architecture;

namespace HexSample.Architecture.Tests;

internal static class Solution
{
    public static IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(Order).Assembly,
        typeof(OrderService).Assembly,
        typeof(Adapters.InMemoryOrderRepository).Assembly,
        typeof(Host.Composition).Assembly,
    ];
}

public sealed class Layering : HexagonalArchitectureTests
{
    protected override IEnumerable<Assembly> Assemblies => Solution.Assemblies;

    protected override ArchitectureConfig Config => ArchitectureConfig.Hexagonal("HexSample");
}

public sealed class RepositoryRules : RepositoryRulesTests
{
    protected override IEnumerable<Assembly> LoadedAssemblies => Solution.Assemblies;
}
