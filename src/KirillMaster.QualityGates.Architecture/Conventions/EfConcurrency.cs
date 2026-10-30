using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Architecture.Conventions;

/// <summary>Z13 helper: aggregate roots of an EF Core model carry a concurrency token. Works by reflection over the model, so the package has no EF dependency.</summary>
public static class EfConcurrency
{
    /// <summary>Marker of an aggregate root: the simple name of a base class, implemented interface or attribute of the entity matches this regex.</summary>
    public const string DefaultMarkerPattern = @"^(IAggregateRoot|AggregateRoot(Attribute)?)(`\d+)?$";

    /// <summary>
    /// Entities of <paramref name="model"/> (<c>DbContext.Model</c>, or <c>IModel</c> from a design-time factory) that match <paramref name="markerPattern"/> but have no property with
    /// <c>IsConcurrencyToken</c>. The key is the entity CLR type.
    /// </summary>
    public static IReadOnlyList<RuleViolation> MissingConcurrencyToken(object model, string markerPattern = DefaultMarkerPattern)
    {
        ArgumentNullException.ThrowIfNull(model);
        var marker = new Regex(markerPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var entities = (IEnumerable?)Invoke(model, "GetEntityTypes") ?? throw new InvalidOperationException("Model has no GetEntityTypes(); pass an EF Core IModel.");
        return [.. entities.Cast<object>()
            .Select(e => (Entity: e, Clr: (Type)(Read(e, "ClrType") ?? throw new InvalidOperationException("Entity type has no ClrType"))))
            .Where(e => IsMarked(e.Clr, marker) && !HasToken(e.Entity))
            .Select(e => new RuleViolation(ConventionRuleIds.EfConcurrencyToken, e.Clr.FullName ?? e.Clr.Name, 1, $"Aggregate root {e.Clr.FullName} has no concurrency token (RowVersion, xmin or IsConcurrencyToken)"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    private static bool HasToken(object entity) =>
        ((IEnumerable?)Invoke(entity, "GetProperties") ?? Array.Empty<object>()).Cast<object>().Any(p => Read(p, "IsConcurrencyToken") is true);

    private static bool IsMarked(Type type, Regex marker)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (marker.IsMatch(t.Name) || t.GetCustomAttributesData().Any(a => marker.IsMatch(a.AttributeType.Name)))
            {
                return true;
            }
        }

        return type.GetInterfaces().Any(i => marker.IsMatch(i.Name));
    }

    private static IEnumerable<Type> Shapes(object target) => target.GetType().GetInterfaces().Prepend(target.GetType());

    private static object? Invoke(object target, string name) =>
        Shapes(target).Select(t => t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)).FirstOrDefault(m => m is not null)?.Invoke(target, null);

    private static object? Read(object target, string name) =>
        Shapes(target).Select(t => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)).FirstOrDefault(p => p is not null)?.GetValue(target);
}
