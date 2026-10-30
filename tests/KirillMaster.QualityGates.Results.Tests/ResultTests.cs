using KirillMaster.QualityGates.Results.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KirillMaster.QualityGates.Results.Tests;

public sealed class ResultTests
{
    private static readonly Error Missing = Error.NotFound("order.missing", "Order was not found");

    [Fact]
    public void Value_converts_to_success()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Error_converts_to_failure()
    {
        Result<int> result = Missing;

        Assert.True(result.IsFailure);
        Assert.Equal(Missing, result.Error);
    }

    [Fact]
    public void Non_generic_result_converts_from_error_and_has_success_singleton()
    {
        Result failed = Missing;

        Assert.True(failed.IsFailure);
        Assert.True(Result.Success.IsSuccess);
    }

    [Fact]
    public void Null_value_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => (Result<string>)(string)null!);

    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.Unexpected, 500)]
    public void Error_type_maps_to_status(ErrorType type, int status) =>
        Assert.Equal(status, ResultHttpExtensions.StatusCode(type));

    [Fact]
    public void Failure_maps_to_problem_with_code_and_status()
    {
        Result<int> result = Missing;

        var http = Assert.IsType<ProblemHttpResult>(result.ToHttpResult());

        Assert.Equal(404, http.ProblemDetails.Status);
        Assert.Equal("order.missing", http.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public void Validation_details_are_listed()
    {
        Result failed = Error.Validation("order.invalid", "Invalid", [Error.Validation("order.qty", "Quantity must be positive")]);

        var http = Assert.IsType<ProblemHttpResult>(failed.ToHttpResult());

        Assert.True(http.ProblemDetails.Extensions.ContainsKey("errors"));
    }

    [Fact]
    public void Success_maps_to_ok_and_no_content()
    {
        Result<int> value = 7;

        Assert.IsType<Ok<int>>(value.ToHttpResult());
        Assert.IsType<NoContent>(Result.Success.ToHttpResult());
    }

    [Fact]
    public async Task Exception_handler_hides_exception_details()
    {
        var context = new DefaultHttpContext();
        var writer = new CapturingProblemDetailsService();
        var handler = new ProblemExceptionHandler(writer);

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("secret"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.DoesNotContain("secret", writer.Written!.ProblemDetails.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? Written { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.FromResult(true);
        }
    }
}
