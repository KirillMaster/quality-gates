namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class ResultRulesTests
{
    private const string Stubs = """
        using System;
        using System.Diagnostics.CodeAnalysis;
        using System.Threading;
        using System.Threading.Tasks;
        public enum ErrorType { Validation, NotFound }
        public sealed class Error
        {
            public Error(string code, string message) { }
            public static Error NotFound(string code, string message) => new Error(code, message);
        }
        public sealed class Result
        {
            [MemberNotNullWhen(false, nameof(Error))] public bool IsSuccess => Error is null;
            [MemberNotNullWhen(true, nameof(Error))] public bool IsFailure => Error is not null;
            public Error? Error { get; }
            public static implicit operator Result(Error e) => null!;
            public Result Bind(Func<Result> next) => null!;
        }
        public sealed class Result<T>
        {
            [MemberNotNullWhen(true, nameof(Value))] [MemberNotNullWhen(false, nameof(Error))] public bool IsSuccess => Error is null;
            [MemberNotNullWhen(true, nameof(Error))] [MemberNotNullWhen(false, nameof(Value))] public bool IsFailure => Error is not null;
            public T? Value { get; }
            public Error? Error { get; }
            public static implicit operator Result<T>(T value) => null!;
            public static implicit operator Result<T>(Error e) => null!;
            public Result<U> Select<U>(Func<T, U> map) => null!;
        }
        public class ProblemDetails { public string? Detail { get; set; } }
        public static class Problems { public static object Problem(string? title = null, string? detail = null) => null!; }
        public static class Hosting { public static void MapGet(this object app, string pattern, Delegate handler) { } }
        public interface IHostedService { }
        """;

    private static async Task<string[]> Ids(string body, params (string Key, string Value)[] config) =>
        [.. (await AnalyzerHarness.DiagnoseWithConfigAsync(Stubs + "\n" + body, config)).Where(id => id.StartsWith("QG01", StringComparison.Ordinal))];

    private static async Task<string[]> IdsIn(string assembly, string body) =>
        [.. (await AnalyzerHarness.DiagnoseInAssemblyAsync(Stubs + "\n" + body, assembly)).Where(id => id.StartsWith("QG01", StringComparison.Ordinal))];

    [Fact]
    public async Task Stubs_alone_are_clean() => Assert.Empty(await Ids(string.Empty));

    [Fact]
    public async Task Controller_returning_result_is_reported_x2() =>
        Assert.Equal(["QG0100"], await Ids("public class OrdersController { public Result<int> Get() => 1; }"));

    [Fact]
    public async Task Controller_returning_task_of_result_is_reported_x2() =>
        Assert.Equal(["QG0100"], await Ids("public class OrdersController { public async Task<Result> Get() => await Task.FromResult<Result>(null!); }"));

    [Fact]
    public async Task Handler_and_non_result_endpoints_are_not_reported_x2() =>
        Assert.Empty(await Ids("public class OrderHandler { public Result<int> Handle() => 1; } public class OrdersController { public int Get() => 1; private Result<int> Helper() => 1; }"));

    [Fact]
    public async Task Minimal_api_lambda_returning_result_is_reported_x2() =>
        Assert.Equal(["QG0100"], await Ids("class P { void M(object app) { app.MapGet(\"/\", () => { Result<int> r = 1; return r; }); } }"));

    [Fact]
    public async Task Minimal_api_lambda_returning_other_is_not_reported_x2() =>
        Assert.Empty(await Ids("class P { void M(object app) { app.MapGet(\"/\", () => 1); } }"));

    [Fact]
    public async Task Catch_in_core_namespace_is_reported_c29() =>
        Assert.Equal(["QG0101"], await Ids("namespace Shop.Core.Orders { class H { void M() { try { } catch (InvalidOperationException) { } } } }"));

    [Fact]
    public async Task Catch_in_core_assembly_is_reported_c29() =>
        Assert.Equal(["QG0101"], await IdsIn("Shop.Domain", "class H { void M() { try { } catch (InvalidOperationException) { } } }"));

    [Fact]
    public async Task Catch_in_core_layer_property_is_reported_c29() =>
        Assert.Equal(["QG0101"], await Ids("class H { void M() { try { } catch (InvalidOperationException) { } } }", ("build_property.QualityGatesLayer", "core")));

    [Fact]
    public async Task Catch_in_test_layer_is_ignored() =>
        Assert.Empty(await Ids("class H { void M() { try { } catch (Exception) { } } }", ("build_property.QualityGatesLayer", "test")));

    [Fact]
    public async Task Catch_in_adapter_assembly_is_not_core() =>
        Assert.Empty(await IdsIn("Shop.Adapters.Db", "class H { void M() { try { } catch (InvalidOperationException) { } } }"));

    [Fact]
    public async Task Catch_all_in_adapter_is_reported_x3() =>
        Assert.Equal(["QG0102"], await IdsIn("Shop.Adapters.Db", "class H { void M() { try { } catch (Exception) { } } }"));

    [Fact]
    public async Task Bare_catch_in_adapter_is_reported_x3() =>
        Assert.Equal(["QG0102"], await IdsIn("Shop.Adapters.Db", "class H { void M() { try { } catch { } } }"));

    [Fact]
    public async Task Catch_all_with_rethrow_or_filter_is_allowed_x3() =>
        Assert.Empty(await IdsIn("Shop.Adapters.Db", "class H { void M(bool b) { try { } catch (Exception) { throw; } try { } catch (Exception) when (b) { } } }"));

    [Fact]
    public async Task Catch_all_in_exception_handler_boundary_is_allowed_x3() =>
        Assert.Empty(await IdsIn("Shop.Adapters.Db", "class ErrorMiddleware { void M() { try { } catch (Exception) { } } }"));

    [Fact]
    public async Task Delay_in_catch_is_reported_x4() =>
        Assert.Equal(["QG0103"], await Ids("class H { async Task M() { try { } catch (InvalidOperationException) { await Task.Delay(100); } } }"));

    [Fact]
    public async Task Sleep_in_loop_is_reported_x4() =>
        Assert.Equal(["QG0103"], await Ids("class H { void M() { for (var i = 0; i < 3; i++) { System.Threading.Thread.Sleep(10); } } }"));

    [Fact]
    public async Task Delay_outside_loop_is_allowed_x4() =>
        Assert.Empty(await Ids("class H { async Task M() { await Task.Delay(100); } }"));

    [Fact]
    public async Task Cancellation_polling_loop_and_hosted_service_are_allowed_x4() =>
        Assert.Empty(await Ids("class H { async Task M(CancellationToken ct) { while (!ct.IsCancellationRequested) { await Task.Delay(100, ct); } } } class W : IHostedService { async Task M() { while (true) { await Task.Delay(100); } } }"));

    [Fact]
    public async Task Catching_cancellation_is_reported_x6() =>
        Assert.Equal(["QG0104"], await IdsIn("Shop.Adapters.Db", "class H { void M() { try { } catch (OperationCanceledException) { } } }"));

    [Fact]
    public async Task Catching_task_canceled_is_reported_x6() =>
        Assert.Equal(["QG0104"], await IdsIn("Shop.Adapters.Db", "class H { void M() { try { } catch (TaskCanceledException) { } } }"));

    [Fact]
    public async Task Cancellation_catch_that_rethrows_or_in_hosted_service_is_allowed_x6() =>
        Assert.Empty(await IdsIn("Shop.Adapters.Db", "class H { void M() { try { } catch (OperationCanceledException) { throw; } } } class W : IHostedService { void M() { try { } catch (OperationCanceledException) { } } }"));

    [Fact]
    public async Task Consumer_catch_is_reported_x7() =>
        Assert.Equal(["QG0105"], await IdsIn("Shop.Adapters.Bus", "namespace MassTransit { public interface IConsumer<T> { } } class C : MassTransit.IConsumer<int> { void M() { try { } catch (Exception) { throw; } } }"));

    [Fact]
    public async Task Consumer_interface_is_configurable_x7() =>
        Assert.Equal(["QG0105"], await Ids("public interface IMessageHandler<T> { } class C : IMessageHandler<int> { void M() { try { } catch (InvalidOperationException) { } } }", ("qg_consumer_interfaces", "IMessageHandler")));

    [Fact]
    public async Task Consumer_without_catch_is_clean_x7() =>
        Assert.Empty(await Ids("namespace MassTransit { public interface IConsumer<T> { } } class C : MassTransit.IConsumer<int> { void M() { } }"));

    [Fact]
    public async Task Discarded_result_is_reported_x8() =>
        Assert.Equal(["QG0106"], await Ids("class H { Result M() => null!; void Use() { M(); } }"));

    [Fact]
    public async Task Awaited_discarded_result_is_reported_x8() =>
        Assert.Equal(["QG0106"], await Ids("class H { Task<Result<int>> M() => null!; async Task Use() { await M(); } }"));

    [Fact]
    public async Task Discard_assignment_is_reported_x8() =>
        Assert.Equal(["QG0106"], await Ids("class H { Result M() => null!; void Use() { _ = M(); } }"));

    [Fact]
    public async Task Unused_result_local_is_reported_x8() =>
        Assert.Equal(["QG0106"], await Ids("class H { Result M() => null!; void Use() { var r = M(); } }"));

    [Fact]
    public async Task Void_lambda_discarding_result_is_reported_x8() =>
        Assert.Equal(["QG0106"], await Ids("class H { Result M() => null!; void Use() { Action a = () => M(); a(); } }"));

    [Fact]
    public async Task Checked_returned_or_passed_result_is_clean_x8() =>
        Assert.Empty(await Ids("class H { Result M() => null!; Result Use() { var r = M(); if (r.IsFailure) { return r; } return M(); } void Take(Result r) { } void Pass() { Take(M()); } }"));

    [Fact]
    public async Task Non_result_discard_is_clean_x8() =>
        Assert.Empty(await Ids("class H { int M() => 1; void Use() { M(); _ = M(); var x = M(); } }"));

    [Fact]
    public async Task Literal_error_code_is_reported_x9() =>
        Assert.Equal(["QG0107"], await Ids("class H { Error E() => new Error(\"order.missing\", \"Missing\"); }"));

    [Fact]
    public async Task Literal_code_in_factory_is_reported_x9() =>
        Assert.Equal(["QG0107"], await Ids("class H { Error E() => Error.NotFound(\"order.missing\", \"Missing\"); }"));

    [Fact]
    public async Task Interpolated_code_is_reported_x9() =>
        Assert.Equal(["QG0107"], await Ids("class H { Error E(int id) => new Error($\"order.{id}\", \"Missing\"); }"));

    [Fact]
    public async Task Catalog_and_non_literal_codes_are_clean_x9() =>
        Assert.Empty(await Ids("static class OrderErrors { public const string MissingCode = \"order.missing\"; public static readonly Error Missing = new Error(MissingCode, \"Missing\"); } class H { Error E() => new Error(OrderErrors.MissingCode, \"x\"); }"));

    [Fact]
    public async Task Throwing_in_result_method_is_reported_x10() =>
        Assert.Equal(["QG0108"], await Ids("class H { Result M(int a) { if (a < 0) { throw new InvalidOperationException(\"x\"); } return null!; } }"));

    [Fact]
    public async Task Throw_expression_in_async_result_method_is_reported_x10() =>
        Assert.Equal(["QG0108"], await Ids("class H { async Task<Result> M(string? a) { var b = a ?? throw new InvalidOperationException(); await Task.Yield(); return null!; } }"));

    [Fact]
    public async Task Guards_and_unreachable_are_allowed_x10() =>
        Assert.Empty(await Ids("class H { Result M(string? a, int k) { ArgumentNullException.ThrowIfNull(a); if (k > 5) { throw new ArgumentOutOfRangeException(nameof(k)); } return k switch { 1 => null!, _ => throw new System.Diagnostics.UnreachableException() }; } }"));

    [Fact]
    public async Task Throwing_in_non_result_method_is_clean_x10() =>
        Assert.Empty(await Ids("class H { void M() { throw new InvalidOperationException(); } }"));

    [Fact]
    public async Task Result_flag_without_member_not_null_when_is_reported_z4() =>
        Assert.Equal(["QG0109", "QG0109"], [.. (await AnalyzerHarness.DiagnoseWithConfigAsync("public sealed class Result { public bool IsSuccess => true; public bool IsFailure => !IsSuccess; public string? Error { get; } }")).Where(id => id == "QG0109")]);

    [Fact]
    public async Task Result_without_value_or_error_is_clean_z4() =>
        Assert.DoesNotContain("QG0109", await AnalyzerHarness.DiagnoseWithConfigAsync("public sealed class Result { public bool IsSuccess => true; }"));

    [Fact]
    public async Task Exception_message_in_problem_details_is_reported_z4() =>
        Assert.Equal(["QG0110"], await Ids("class H { object M(Exception ex) => new ProblemDetails { Detail = ex.Message }; }"));

    [Fact]
    public async Task Interpolated_exception_in_problem_call_is_reported_z4() =>
        Assert.Equal(["QG0110"], await Ids("class H { object M(Exception ex) => Problems.Problem(detail: $\"failed: {ex}\"); }"));

    [Fact]
    public async Task Static_problem_detail_is_clean_z4() =>
        Assert.Empty(await Ids("class H { object M() => new ProblemDetails { Detail = \"Something went wrong\" }; object N() => Problems.Problem(detail: \"x\"); }"));

    [Fact]
    public async Task Bind_chain_is_reported_fluent() =>
        Assert.Equal(["QG0111"], await Ids("class H { Result M(Result r) => r.Bind(() => r); }"));

    [Fact]
    public async Task Query_syntax_over_result_is_reported_fluent() =>
        Assert.Equal(["QG0111"], await Ids("class H { Result<int> M(Result<int> r) => from x in r select x + 1; }"));

    [Fact]
    public async Task Map_on_non_result_and_linq_over_list_are_clean_fluent() =>
        Assert.Empty(await Ids("class Router { public Router Map(string p) => this; } class H { Router M(Router r) => r.Map(\"/\").Map(\"/x\"); int[] N(int[] a) => (from x in a select x + 1).ToArray(); }"));

    [Fact]
    public async Task Result_names_are_configurable() =>
        Assert.Equal(["QG0106"], await Ids("public sealed class Outcome { } class H { Outcome M() => null!; void Use() { M(); } }", ("qg_result_type_names", "Outcome")));
}
