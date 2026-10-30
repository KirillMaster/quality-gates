using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddExceptionHandler<TestExceptionHandler>();
builder.Services.AddOptions<StartupOptions>().Configure(o => o.Name = builder.Configuration["Name"]).Validate(o => !string.IsNullOrEmpty(o.Name), "Name is required").ValidateOnStart();
var app = builder.Build();
app.UseExceptionHandler(_ => { });
app.MapGet("/", () => "ok");
app.MapGet("/orders/{id}", (string id) => id);
app.MapPost("/orders", () => Results.Created("/orders/1", null));
app.MapGet("/health", () => "up");
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapGet("/boom", () => Results.StatusCode(500));
app.Run();

public sealed class StartupOptions
{
    public string? Name { get; set; }
}

public sealed class TestExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public sealed class SecondExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public partial class Program;
