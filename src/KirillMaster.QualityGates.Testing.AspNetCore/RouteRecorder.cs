using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KirillMaster.QualityGates.Testing.AspNetCore;

/// <summary>
/// Z14: records which routes the tests hit and compares them with the endpoint table. Share one recorder across the test run (a class fixture or static), install it with
/// <c>factory.WithWebHostBuilder(b =&gt; b.ConfigureServices(recorder.Install))</c>, and call <see cref="AssertAllRoutesHit"/> in the last test or the fixture's dispose.
/// </summary>
public sealed class RouteRecorder
{
    /// <summary>Routes that are infrastructure rather than API (OpenAPI document, health, swagger).</summary>
    public const string DefaultIgnorePattern = @"^\S+ /(openapi|swagger|health|healthz|alive|ready)(/|$|\.)";

    private readonly ConcurrentDictionary<string, byte> _hits = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Hits => [.. _hits.Keys.Order(StringComparer.Ordinal)];

    public void Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IStartupFilter>(new RecordingFilter(this));
    }

    /// <summary>Routes of the endpoint table that no recorded request matched and that do not match <paramref name="ignorePattern"/>.</summary>
    public IReadOnlyList<string> Uncovered(IServiceProvider services, string ignorePattern = DefaultIgnorePattern)
    {
        var ignore = new Regex(ignorePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return [.. HostSmoke.Routes(services).Where(r => !ignore.IsMatch(r) && !_hits.ContainsKey(r))];
    }

    /// <summary>Fails for every uncovered route that is not in <paramref name="known"/> (the ratchet baseline: routes covered later should be removed from it).</summary>
    public void AssertAllRoutesHit(IServiceProvider services, IEnumerable<string>? known = null, string ignorePattern = DefaultIgnorePattern)
    {
        var tolerated = (known ?? []).ToHashSet(StringComparer.Ordinal);
        var fresh = Uncovered(services, ignorePattern).Where(r => !tolerated.Contains(r)).ToArray();
        if (fresh.Length > 0)
        {
            throw new InvalidOperationException("Routes not hit by any test (cover them or add to the known list only to tolerate): " + string.Join("; ", fresh));
        }
    }

    private void Record(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint endpoint)
        {
            _hits[HostSmoke.RouteKey(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods, endpoint.RoutePattern.RawText ?? string.Empty)] = 0;
        }
    }

    private sealed class RecordingFilter(RouteRecorder recorder) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, pipeline) =>
                {
                    await pipeline();
                    recorder.Record(context);
                });
                next(app);
            };
    }
}
