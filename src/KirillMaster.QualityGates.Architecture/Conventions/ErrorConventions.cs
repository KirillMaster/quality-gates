using System.Reflection;

namespace KirillMaster.QualityGates.Architecture.Conventions;

public static class ConventionRuleIds
{
    public const string SingleExceptionHandler = "SingleExceptionHandler";
    public const string UniqueErrorCodes = "UniqueErrorCodes";
    public const string EfConcurrencyToken = "EfConcurrencyToken";
}

/// <summary>Test helpers for the error-handling conventions (X1, X9). Findings are <see cref="RuleViolation"/>s, so they go through <see cref="RuleBaseline"/> like the other rules.</summary>
public static class ErrorConventions
{
    public const string ExceptionHandlerInterface = "Microsoft.AspNetCore.Diagnostics.IExceptionHandler";

    /// <summary>X1: exactly one non-abstract class implements the exception handler interface in the host and composition assemblies; the key is "exception-handlers", the measure is the count.</summary>
    public static IReadOnlyList<RuleViolation> SingleExceptionHandler(IEnumerable<Assembly> assemblies, string handlerInterface = ExceptionHandlerInterface)
    {
        var handlers = assemblies.SelectMany(LoadableTypes).Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => i.FullName == handlerInterface))
            .Select(t => t.FullName ?? t.Name).Order(StringComparer.Ordinal).ToArray();
        return handlers.Length == 1
            ? []
            : [new RuleViolation(ConventionRuleIds.SingleExceptionHandler, "exception-handlers", handlers.Length, $"Expected exactly one {handlerInterface} implementation, found {handlers.Length}: {string.Join(", ", handlers)}")];
    }

    /// <summary>X9: error codes are unique across all static classes whose name ends with <paramref name="catalogSuffix"/>. A code is the value of a public static string field or property, or the string <c>Code</c> property of a public static field or property value (an Error). The key is the code, the measure the number of declarations.</summary>
    public static IReadOnlyList<RuleViolation> UniqueErrorCodes(IEnumerable<Assembly> assemblies, string catalogSuffix = "Errors")
    {
        var codes = assemblies.SelectMany(LoadableTypes).Where(t => t is { IsAbstract: true, IsSealed: true } && t.Name.EndsWith(catalogSuffix, StringComparison.Ordinal))
            .SelectMany(t => CodesOf(t).Select(code => (Code: code, Owner: t.FullName + "." + code.Member)))
            .Where(c => c.Code.Value is { Length: > 0 })
            .GroupBy(c => c.Code.Value!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        return [.. codes.Select(g => new RuleViolation(ConventionRuleIds.UniqueErrorCodes, g.Key, g.Count(), $"Error code {g.Key} is declared {g.Count()} times: {string.Join(", ", g.Select(c => c.Owner).Order(StringComparer.Ordinal))}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    private static IEnumerable<(string Member, string? Value)> CodesOf(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(flags))
        {
            yield return (field.Name, CodeOf(field.GetValue(null)));
        }

        foreach (var property in type.GetProperties(flags).Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null))
        {
            yield return (property.Name, CodeOf(property.GetValue(null)));
        }
    }

    private static string? CodeOf(object? value) =>
        value switch
        {
            null => null,
            string text => text,
            _ => value.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as string,
        };

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
