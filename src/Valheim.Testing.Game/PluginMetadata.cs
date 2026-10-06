using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Valheim.Testing.Game;

/// <summary>A BepInEx plugin as its assembly declares it: <c>[BepInPlugin]</c> and the attributes BepInEx reads beside it.</summary>
/// <param name="Type">The plugin class's full name.</param>
/// <param name="Processes">The <c>[BepInProcess]</c> filters; empty loads in every process.</param>
[ResultShape]
public sealed record PluginDeclaration(string Guid, string Name, string Version, string Type,
    IReadOnlyList<PluginDependency> Dependencies, IReadOnlyList<string> Incompatibilities, IReadOnlyList<string> Processes);

/// <summary>A <c>[BepInDependency]</c>: a hard one stops the plugin loading without it; a soft one only orders loading.</summary>
[ResultShape]
public sealed record PluginDependency(string Guid, bool Hard, string? MinimumVersion);

/// <summary>What a DLL declares to BepInEx: its assembly name, the assemblies it references and its plugins (none for a library).</summary>
[ResultShape]
public sealed record PluginAssembly(string AssemblyName, IReadOnlyList<string> References, IReadOnlyList<PluginDeclaration> Plugins);

/// <summary>
/// Reads a DLL's BepInEx metadata without loading it, as BepInEx's chainloader decides what to load: the
/// <c>[BepInPlugin]</c>, <c>[BepInDependency]</c>, <c>[BepInIncompatibility]</c> and <c>[BepInProcess]</c> attributes on its
/// types, matched by full type name as BepInEx matches them, and the assembly references the runtime will resolve. A file
/// name says nothing here: a renamed DLL declares the same plugin, and a DLL named like a plugin may declare none.
/// </summary>
public static class PluginMetadata
{
    private const string Plugin = "BepInEx.BepInPlugin", Dependency = "BepInEx.BepInDependency",
        Incompatibility = "BepInEx.BepInIncompatibility", Process = "BepInEx.BepInProcess";

    /// <summary>The metadata of the DLL at <paramref name="path"/>; refuses a file that is not a .NET assembly.</summary>
    public static PluginAssembly Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        if (!image.HasMetadata) throw new InvalidDataException($"{path} is not a .NET assembly, so BepInEx cannot load it as a plugin or library.");
        var reader = image.GetMetadataReader();
        if (!reader.IsAssembly) throw new InvalidDataException($"{path} is a .NET module without an assembly manifest; BepInEx loads assemblies only.");
        string name = reader.GetString(reader.GetAssemblyDefinition().Name);
        var references = reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var plugins = new List<PluginDeclaration>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            string[]? plugin = null;
            var dependencies = new List<PluginDependency>();
            var incompatibilities = new List<string>();
            var processes = new List<string>();
            foreach (var attributeHandle in type.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                string? attributeType = AttributeType(reader, attribute);
                if (attributeType is not (Plugin or Dependency or Incompatibility or Process)) continue;
                var arguments = attribute.DecodeValue(TypeNames.Instance).FixedArguments;
                switch (attributeType)
                {
                    case Plugin when arguments.Length == 3:
                        plugin = [Text(arguments[0]), Text(arguments[1]), Text(arguments[2])];
                        break;
                    case Dependency when arguments.Length == 1:
                        dependencies.Add(new(Text(arguments[0]), true, null));
                        break;
                    case Dependency when arguments.Length == 2 && arguments[1].Value is string minimum:
                        dependencies.Add(new(Text(arguments[0]), true, minimum)); // BepInDependency(guid, minimumVersion) is hard.
                        break;
                    case Dependency when arguments.Length == 2 && arguments[1].Value is int flags:
                        dependencies.Add(new(Text(arguments[0]), (flags & HardDependency) != 0, null));
                        break;
                    case Incompatibility when arguments.Length == 1:
                        incompatibilities.Add(Text(arguments[0]));
                        break;
                    case Process when arguments.Length == 1:
                        processes.Add(Text(arguments[0]));
                        break;
                    default:
                        throw new InvalidDataException($"{path}: {TypeName(reader, type)} has a {attributeType} attribute of a shape BepInEx 5 does not define ({arguments.Length} arguments).");
                }
            }
            if (plugin != null)
                plugins.Add(new(plugin[0], plugin[1], plugin[2], TypeName(reader, type), dependencies, incompatibilities, processes));
            else if (dependencies.Count + incompatibilities.Count + processes.Count != 0)
                throw new InvalidDataException($"{path}: {TypeName(reader, type)} has BepInEx dependency or process attributes but no [BepInPlugin], which BepInEx ignores.");
        }
        return new(name, references, plugins);
    }

    /// <summary>BepInEx 5's <c>BepInDependency.DependencyFlags.HardDependency</c>.</summary>
    private const int HardDependency = 1;

    private static string Text(CustomAttributeTypedArgument<string> argument) =>
        argument.Value as string ?? throw new InvalidDataException("A BepInEx attribute's text argument is null.");

    private static string TypeName(MetadataReader reader, TypeDefinition type)
    {
        string name = reader.GetString(type.Name), space = reader.GetString(type.Namespace);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return TypeName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
        return space.Length == 0 ? name : space + "." + name;
    }

    // The attribute's type by namespace and name, wherever it is defined: BepInEx's own loader matches the same way.
    private static string? AttributeType(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle parent;
        if (attribute.Constructor.Kind == HandleKind.MemberReference) parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition) parent = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        else return null;
        if (parent.Kind == HandleKind.TypeReference)
        {
            var reference = reader.GetTypeReference((TypeReferenceHandle)parent);
            return reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);
        }
        if (parent.Kind == HandleKind.TypeDefinition)
        {
            var definition = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
            return reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name);
        }
        return null;
    }

    // Decodes attribute arguments by type name. Every enum a BepInEx attribute takes (DependencyFlags) is an int.
    private sealed class TypeNames : ICustomAttributeTypeProvider<string>
    {
        public static readonly TypeNames Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
