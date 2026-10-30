using KirillMaster.QualityGates.Architecture.Conventions;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class ConventionTests
{
    private const string Interface = "namespace Microsoft.AspNetCore.Diagnostics { public interface IExceptionHandler { } }";

    [Fact]
    public void Exactly_one_exception_handler_is_required()
    {
        var none = FixtureCompiler.Compile("ExNone", Interface);
        var one = FixtureCompiler.Compile("ExOne", Interface + "namespace App { public class Global : Microsoft.AspNetCore.Diagnostics.IExceptionHandler { } public abstract class Base : Microsoft.AspNetCore.Diagnostics.IExceptionHandler { } }");
        var two = FixtureCompiler.Compile("ExTwo", Interface + "namespace App { public class A : Microsoft.AspNetCore.Diagnostics.IExceptionHandler { } public class B : Microsoft.AspNetCore.Diagnostics.IExceptionHandler { } }");

        Assert.Equal([("exception-handlers", 0)], ErrorConventions.SingleExceptionHandler([none]).Select(v => (v.Key, v.Measure)));
        Assert.Empty(ErrorConventions.SingleExceptionHandler([one]));
        Assert.Equal([("exception-handlers", 2)], ErrorConventions.SingleExceptionHandler([two]).Select(v => (v.Key, v.Measure)));
        Assert.Empty(ErrorConventions.SingleExceptionHandler([one, none]));
    }

    [Fact]
    public void Error_codes_are_unique_across_error_catalogs()
    {
        var assembly = FixtureCompiler.Compile("ErrorCodes", @"
namespace App
{
    public sealed record Error(string Code);
    public static class OrderErrors { public const string NotFoundCode = ""order.not_found""; public const string Label = ""order.gone""; public static readonly Error Gone = new(""order.gone""); }
    public static class BillErrors { public static Error Dup => new(""order.gone""); public const string Ok = ""bill.ok""; }
    public static class BoomErrors { public static Error Bad => throw new System.InvalidOperationException(""boom""); }
    public static class Unrelated { public const string Same = ""order.gone""; }
}");

        var found = ErrorConventions.UniqueErrorCodes([assembly]);

        Assert.Equal([("App.BoomErrors.Bad", 1), ("order.gone", 2)], found.Select(v => (v.Key, v.Measure)));
        var duplicate = found.Single(v => v.Key == "order.gone");
        Assert.Contains("OrderErrors.Gone", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("BillErrors.Dup", duplicate.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Label", duplicate.Message, StringComparison.Ordinal);
        Assert.Contains("boom", found.Single(v => v.Key == "App.BoomErrors.Bad").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_roots_need_a_concurrency_token()
    {
        var model = new FakeModel(
            new FakeEntity(typeof(Order), true),
            new FakeEntity(typeof(Invoice), false),
            new FakeEntity(typeof(Line), false),
            new FakeEntity(typeof(Marked), false));

        var found = EfConcurrency.MissingConcurrencyToken(model);

        Assert.Equal([typeof(Invoice).FullName, typeof(Marked).FullName], found.Select(v => v.Key));
        Assert.Equal([typeof(Line).FullName], EfConcurrency.MissingConcurrencyToken(model, "^Line$").Select(v => v.Key));
        Assert.Throws<InvalidOperationException>(() => EfConcurrency.MissingConcurrencyToken(new object()));
    }

    private interface IAggregateRoot;

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class AggregateRootAttribute : Attribute;

    private sealed class Order : IAggregateRoot;

    private sealed class Invoice : IAggregateRoot;

    private sealed class Line;

    [AggregateRoot]
    private sealed class Marked;

    private sealed class FakeModel(params FakeEntity[] entities)
    {
        public FakeEntity[] GetEntityTypes() => entities;
    }

    private sealed class FakeEntity(Type clrType, bool token)
    {
        public Type ClrType { get; } = clrType;

        public FakeProperty[] GetProperties() => [new FakeProperty(false), new FakeProperty(token)];
    }

    private sealed class FakeProperty(bool token)
    {
        public bool IsConcurrencyToken { get; } = token;
    }
}
