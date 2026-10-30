using System.Reflection;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// X9: error codes are unique across all static classes whose name ends with <paramref name="catalogSuffix"/>. Only two kinds of member count: a member whose declared type is named like
    /// <paramref name="errorTypePattern"/> (default <c>Error</c>; its string <c>Code</c> property is the code) and a string member whose name matches <paramref name="codeMemberPattern"/> (default <c>Code$</c>).
    /// The key is the code, the measure the number of declarations; a member that cannot be read is a violation keyed by the member.
    /// </summary>
    public static IReadOnlyList<RuleViolation> UniqueErrorCodes(IEnumerable<Assembly> assemblies, string catalogSuffix = "Errors", string errorTypePattern = @"^Error(`\d+)?$", string codeMemberPattern = "Code$")
    {
        var errorType = new Regex(errorTypePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var codeMember = new Regex(codeMemberPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var members = assemblies.SelectMany(LoadableTypes).Where(t => t is { IsAbstract: true, IsSealed: true } && t.Name.EndsWith(catalogSuffix, StringComparison.Ordinal))
            .SelectMany(t => CodesOf(t, errorType, codeMember)).ToArray();
        var unreadable = members.Where(m => m.Failure is not null)
            .Select(m => new RuleViolation(ConventionRuleIds.UniqueErrorCodes, m.Owner, 1, $"Error code of {m.Owner} cannot be read: {m.Failure}"));
        var duplicates = members.Where(m => m.Failure is null && m.Value is { Length: > 0 }).GroupBy(m => m.Value!, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => new RuleViolation(ConventionRuleIds.UniqueErrorCodes, g.Key, g.Count(), $"Error code {g.Key} is declared {g.Count()} times: {string.Join(", ", g.Select(c => c.Owner).Order(StringComparer.Ordinal))}"));
        return [.. duplicates.Concat(unreadable).OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    private static IEnumerable<(string Owner, string? Value, string? Failure)> CodesOf(Type type, Regex errorType, Regex codeMember)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var candidates = type.GetFields(flags).Select(f => (f.Name, f.FieldType, Read: (Func<object?>)(() => f.GetValue(null))))
            .Concat(type.GetProperties(flags).Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null).Select(p => (p.Name, p.PropertyType, Read: (Func<object?>)(() => p.GetValue(null)))));
        foreach (var (name, memberType, read) in candidates)
        {
            var isError = errorType.IsMatch(memberType.Name);
            if (!isError && !(memberType == typeof(string) && codeMember.IsMatch(name)))
            {
                continue;
            }

            var owner = type.FullName + "." + name;
            string? code = null;
            string? failure = null;
            try
            {
                var value = read();
                code = value as string ?? value?.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as string;
            }
            catch (Exception ex) when (ex is TargetInvocationException or TypeInitializationException)
            {
                failure = (ex.InnerException ?? ex).Message;
            }

            yield return (owner, code, failure);
        }
    }

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
