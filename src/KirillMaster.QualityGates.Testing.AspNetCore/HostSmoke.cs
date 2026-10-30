using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KirillMaster.QualityGates.Testing.AspNetCore;

/// <summary>Z14 and X1 helpers over a <see cref="WebApplicationFactory{TEntryPoint}"/>: the host starts, every route is hit by a test, exactly one exception handler is registered.</summary>
public static class HostSmoke
{
    /// <summary>Starts the host with the test configuration; startup validation (ValidateOnStart, DI validation) runs, so a broken composition root fails here. <paramref name="probePath"/> is requested once and must not answer 5xx; pass null to skip it.</summary>
    public static async Task AssertStartsAsync<TEntryPoint>(Action<IWebHostBuilder>? configure = null, string? probePath = "/")
        where TEntryPoint : class
    {
        await using var factory = new WebApplicationFactory<TEntryPoint>().WithWebHostBuilder(configure ?? (_ => { }));
        using var client = factory.CreateClient();
        if (probePath is null)
        {
            return;
        }

        using var response = await client.GetAsync(new Uri(probePath, UriKind.Relative));
        if ((int)response.StatusCode >= 500)
        {
            throw new InvalidOperationException($"Host started but GET {probePath} answered {(int)response.StatusCode}.");
        }
    }

    /// <summary>Routes of the endpoint table (fallback endpoints, <c>MapFallback</c>, excluded) as "METHOD /pattern" (methods joined with "|", "ANY" without a method constraint), sorted.</summary>
    public static IReadOnlyList<string> Routes(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return [.. services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.Order != int.MaxValue)
            .Select(e => RouteKey(e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods, e.RoutePattern.RawText ?? string.Empty))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Fails unless exactly one <see cref="IExceptionHandler"/> is registered in the host.</summary>
    public static void AssertSingleExceptionHandler(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var handlers = services.GetServices<IExceptionHandler>().Select(h => h.GetType().FullName).Order(StringComparer.Ordinal).ToArray();
        if (handlers.Length != 1)
        {
            throw new InvalidOperationException($"Expected exactly one IExceptionHandler, found {handlers.Length}: {string.Join(", ", handlers)}");
        }
    }

    internal static string RouteKey(IEnumerable<string>? methods, string pattern)
    {
        var joined = methods is null ? "ANY" : string.Join('|', methods.Order(StringComparer.Ordinal));
        return joined.Length == 0 ? "ANY " + pattern : $"{joined} {pattern}";
    }
}
