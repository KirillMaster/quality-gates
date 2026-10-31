using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Adapters.Roslyn;

/// <summary>Reads assembly facts with System.Reflection.Metadata. Namespace edges come from base types, interfaces, field types and method signatures (parameters, return types); calls inside method bodies are not decoded.</summary>
public sealed class MetadataAssemblyReader : IAssemblyReader
{
    public IReadOnlyList<AssemblyFacts> Read(IReadOnlyList<string> files)
    {
        var raw = new List<Raw>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            if (ReadOne(file) is { } item)
            {
                raw.Add(item);
            }
        }

        var names = raw.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        return [.. raw.GroupBy(r => r.Name, StringComparer.Ordinal).Select(g => g.First()).Select(r => new AssemblyFacts(
            r.Name,
            r.Types,
            r.Abstract,
            r.References,
            r.Edges.Where(e => names.Contains(e.TargetAssembly) && !(e.TargetAssembly == r.Name && e.SourceNamespace == e.TargetNamespace))
                .Select(e => $"{r.Name}:{e.SourceNamespace} -> {e.TargetAssembly}:{e.TargetNamespace}").ToHashSet(StringComparer.Ordinal)))];
    }

    private static Raw? ReadOne(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            var name = reader.GetString(reader.GetAssemblyDefinition().Name);
            var references = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).ToHashSet(StringComparer.Ordinal);
            var recorder = new Recorder(reader, name);
            var types = 0;
            var abstractTypes = 0;
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var typeName = reader.GetString(definition.Name);
                if (typeName.StartsWith('<'))
                {
                    continue;
                }

                types++;
                var isAbstract = definition.Attributes.HasFlag(TypeAttributes.Abstract);
                if (isAbstract && !definition.Attributes.HasFlag(TypeAttributes.Sealed))
                {
                    abstractTypes++;
                }

                recorder.Source = recorder.NamespaceOf(handle);
                recorder.Add(definition.BaseType);
                foreach (var implementation in definition.GetInterfaceImplementations())
                {
                    recorder.Add(reader.GetInterfaceImplementation(implementation).Interface);
                }

                foreach (var field in definition.GetFields())
                {
                    reader.GetFieldDefinition(field).DecodeSignature(recorder, null);
                }

                foreach (var method in definition.GetMethods())
                {
                    reader.GetMethodDefinition(method).DecodeSignature(recorder, null);
                }
            }

            return new Raw(name, types, abstractTypes, references, recorder.Edges);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record Raw(string Name, int Types, int Abstract, IReadOnlySet<string> References, IReadOnlyList<Edge> Edges);

    private sealed record Edge(string SourceNamespace, string TargetAssembly, string TargetNamespace);

    private sealed class Recorder(MetadataReader reader, string assembly) : ISignatureTypeProvider<object?, object?>
    {
        public string Source { get; set; } = string.Empty;

        public List<Edge> Edges { get; } = [];

        public string NamespaceOf(TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            return definition.GetDeclaringType().IsNil ? reader.GetString(definition.Namespace) : NamespaceOf(definition.GetDeclaringType());
        }

        public void Add(EntityHandle handle)
        {
            if (handle.IsNil)
            {
                return;
            }

            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    Record(assembly, NamespaceOf((TypeDefinitionHandle)handle));
                    break;
                case HandleKind.TypeReference:
                    RecordReference((TypeReferenceHandle)handle);
                    break;
                case HandleKind.TypeSpecification:
                    reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                    break;
                default:
                    break;
            }
        }

        public object? GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            Add(handle);
            return null;
        }

        public object? GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
        {
            Add(handle);
            return null;
        }

        public object? GetTypeFromSpecification(MetadataReader metadata, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            Add(handle);
            return null;
        }

        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;

        public object? GetSZArrayType(object? elementType) => null;

        public object? GetArrayType(object? elementType, ArrayShape shape) => null;

        public object? GetByReferenceType(object? elementType) => null;

        public object? GetPointerType(object? elementType) => null;

        public object? GetPinnedType(object? elementType) => null;

        public object? GetModifiedType(object? modifier, object? unmodifiedType, bool isRequired) => null;

        public object? GetGenericInstantiation(object? genericType, ImmutableArray<object?> typeArguments) => null;

        public object? GetFunctionPointerType(MethodSignature<object?> signature) => null;

        public object? GetGenericMethodParameter(object? genericContext, int index) => null;

        public object? GetGenericTypeParameter(object? genericContext, int index) => null;

        private void RecordReference(TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            switch (type.ResolutionScope.Kind)
            {
                case HandleKind.AssemblyReference:
                    Record(reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name), reader.GetString(type.Namespace));
                    break;
                case HandleKind.TypeReference:
                    RecordReference((TypeReferenceHandle)type.ResolutionScope);
                    break;
                default:
                    break;
            }
        }

        private void Record(string targetAssembly, string targetNamespace) => Edges.Add(new Edge(Source, targetAssembly, targetNamespace));
    }
}
