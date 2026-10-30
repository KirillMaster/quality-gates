using KirillMaster.QualityGates.Testing.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KirillMaster.QualityGates.Testing.AspNetCore.Tests;

public sealed class HostSmokeTests
{
    private static void Unconfigured(IWebHostBuilder builder) => builder.UseContentRoot(AppContext.BaseDirectory);

    private static void Configured(IWebHostBuilder builder) => builder.UseContentRoot(AppContext.BaseDirectory).ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Name"] = "shop" }));

    [Fact]
    public async Task A_valid_host_starts_and_answers_the_probe()
    {
        await HostSmoke.AssertStartsAsync<Program>(Configured);
        await HostSmoke.AssertStartsAsync<Program>(Configured, probePath: null);
    }

    [Fact]
    public async Task A_host_with_invalid_options_fails_at_start()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => HostSmoke.AssertStartsAsync<Program>(Unconfigured));
        Assert.Contains("Name is required", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_probe_answering_5xx_fails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostSmoke.AssertStartsAsync<Program>(Configured, "/boom"));
    }

    [Fact]
    public async Task Routes_not_hit_by_tests_are_reported_and_known_ones_tolerated()
    {
        var recorder = new RouteRecorder();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            Configured(b);
            b.ConfigureServices(recorder.Install);
        });
        using var client = factory.CreateClient();
        (await client.GetAsync(new Uri("/orders/7", UriKind.Relative))).EnsureSuccessStatusCode();

        Assert.Equal(["GET /orders/{id}"], recorder.Hits);
        Assert.Equal(["GET /", "GET /boom", "POST /orders"], recorder.Uncovered(factory.Services));
        Assert.Throws<InvalidOperationException>(() => recorder.AssertAllRoutesHit(factory.Services));
        recorder.AssertAllRoutesHit(factory.Services, known: ["GET /", "GET /boom", "POST /orders"]);
        Assert.Contains("GET /health", HostSmoke.Routes(factory.Services));
    }

    [Fact]
    public async Task Exactly_one_exception_handler_must_be_registered()
    {
        await using var one = new WebApplicationFactory<Program>().WithWebHostBuilder(Configured);
        HostSmoke.AssertSingleExceptionHandler(one.Services);

        await using var two = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            Configured(b);
            b.ConfigureServices(s => s.AddExceptionHandler<SecondExceptionHandler>());
        });
        Assert.Throws<InvalidOperationException>(() => HostSmoke.AssertSingleExceptionHandler(two.Services));
    }
}
