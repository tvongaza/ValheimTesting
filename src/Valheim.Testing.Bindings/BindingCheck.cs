using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Valheim.Testing.Bindings;

/// <summary>What <see cref="BindingCheck"/> checks a mod against.</summary>
public sealed class BindingCheckOptions
{
    /// <summary>Directories searched for a referenced assembly as <c>name.dll</c>, for example the game's <c>valheim_Data/Managed</c>.</summary>
    public IList<string> GameDirectories { get; } = new List<string>();
    /// <summary>Assembly files checked whatever their file name, for example a pinned copy of <c>assembly_valheim.dll</c>. The name inside the file decides which references it answers.</summary>
    public IList<string> GameFiles { get; } = new List<string>();
    /// <summary>When not empty, only these assembly names are checked from <see cref="GameDirectories"/>. <see cref="GameFiles"/> are always checked.</summary>
    public ISet<string> OnlyAssemblies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Assemblies that must be supplied if the mod references them, so that a wrong path cannot pass by checking nothing.
    /// <c>assembly_valheim</c> by default; the report lists any that were not (<see cref="BindingReport.MissingRequired"/>).
    /// </summary>
    public ISet<string> RequiredAssemblies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "assembly_valheim" };
}

/// <summary>
/// Checks, without loading or running anything, that a built mod's references into the supplied game assemblies still
/// bind: every type, field and method (properties and events are their accessor methods) that the mod's code,
/// signatures, attributes and metadata name. A reference the runtime could not bind (a removed or renamed member, a
/// changed signature or field type, a removed type) is reported in <see cref="BindingReport.Missing"/> with the mod methods
/// that use it; the runtime would throw <c>MissingFieldException</c>, <c>MissingMethodException</c> or
/// <c>TypeLoadException</c> only when it first compiled one of them. A member that still exists but is not accessible from
/// the mod goes to <see cref="BindingReport.Access"/> instead: a mod built against publicized assemblies references
/// non-public members on purpose. References into assemblies that were not supplied are counted in
/// <see cref="BindingReport.NotChecked"/>, never reported as missing.
/// </summary>
public static class BindingCheck
{
    /// <summary>Reads the mod assembly and checks its references against the supplied game files; missing inputs are refused.</summary>
    public static BindingReport Check(string modPath, BindingCheckOptions options)
    {
        if (modPath == null) throw new ArgumentNullException(nameof(modPath));
        if (options == null) throw new ArgumentNullException(nameof(options));
        string full = Path.GetFullPath(modPath);
        if (!File.Exists(full)) throw new FileNotFoundException("Mod assembly not found: " + full, full);
        using var supplied = new SuppliedAssemblies(options, Path.GetDirectoryName(full) ?? ".");
        AssemblyDefinition mod = supplied.LoadMod(full);
        return new Checker(mod, supplied, options).Run(full);
    }
}

// The assemblies a check may use. Checking asks PathFor, which honours OnlyAssemblies; Cecil's own decoding (an enum
// argument of an attribute needs its underlying type) goes through Resolve, which may also use the mod's directory and
// the running framework, since decoding is not checking.
internal sealed class SuppliedAssemblies : IAssemblyResolver
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Dictionary<string, string>> _directories = new();
    private readonly ISet<string> _only;
    private readonly string _modDirectory;
    private readonly Dictionary<string, AssemblyDefinition> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly DefaultAssemblyResolver _framework = new();

    public SuppliedAssemblies(BindingCheckOptions options, string modDirectory)
    {
        _only = options.OnlyAssemblies;
        _modDirectory = modDirectory;
        foreach (string file in options.GameFiles)
        {
            string full = Path.GetFullPath(file);
            if (!File.Exists(full)) throw new FileNotFoundException("Game assembly not found: " + full, full);
            _files[Load(full).Name.Name] = full;
        }
        foreach (string directory in options.GameDirectories)
        {
            string full = Path.GetFullPath(directory);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Game assembly directory not found: " + full);
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(full).OrderBy(p => p, StringComparer.Ordinal))
            {
                string extension = Path.GetExtension(path);
                if ((extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    && !index.ContainsKey(Path.GetFileNameWithoutExtension(path)))
                    index[Path.GetFileNameWithoutExtension(path)] = path;
            }
            _directories.Add(index);
        }
    }

    public AssemblyDefinition LoadMod(string path) => Load(path);

    /// <summary>The file checked for this assembly name, or null when it was not supplied.</summary>
    public string? PathFor(string name)
    {
        if (_files.TryGetValue(name, out string? file)) return file;
        return _only.Count > 0 && !_only.Contains(name) ? null : Find(name);
    }

    public AssemblyDefinition? Get(string name) => PathFor(name) is { } path ? Load(path) : null;

    private string? Find(string name)
    {
        foreach (Dictionary<string, string> directory in _directories)
            if (directory.TryGetValue(name, out string? path)) return path;
        return null;
    }

    private AssemblyDefinition Load(string path)
    {
        if (!_loaded.TryGetValue(path, out AssemblyDefinition? assembly))
        {
            // InMemory: no file stays open, so a CI job or test can replace or delete the files afterwards.
            assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters(ReadingMode.Deferred) { AssemblyResolver = this, InMemory = true, ReadSymbols = false });
            _loaded[path] = assembly;
        }
        return assembly;
    }

    public AssemblyDefinition? Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

    public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        string? path = _files.TryGetValue(name.Name, out string? file) ? file : Find(name.Name);
        string local = Path.Combine(_modDirectory, name.Name + ".dll");
        if (path == null && File.Exists(local)) path = local;
        if (path != null) return Load(path);
        try { return _framework.Resolve(name); }
        catch (AssemblyResolutionException) { return null; }
    }

    public void Dispose()
    {
        foreach (AssemblyDefinition assembly in _loaded.Values) assembly.Dispose();
        _loaded.Clear();
        _framework.Dispose();
    }
}

internal sealed class Checker
{
    private const string MetadataOnly = "(module metadata only; no code uses it)";

    private sealed class Site
    {
        public Site(string where, TypeDefinition? from, bool sweep = false) { Where = where; From = from; Sweep = sweep; }
        public string Where { get; }
        public TypeDefinition? From { get; } // the mod type whose code or signature holds the reference, for protected access
        public bool Sweep { get; }
    }

    // How a type reference ended: in the mod itself, in an assembly that was not supplied, found, or missing.
    private sealed class Lookup
    {
        public static readonly Lookup Own = new();
        public TypeDefinition? Type { get; private set; }
        public string Assembly { get; private set; } = "";
        public bool NotSupplied { get; private set; }
        public string? Detail { get; private set; }
        public bool IsOwn => ReferenceEquals(this, Own);
        public static Lookup Found(TypeDefinition type) => new() { Type = type, Assembly = type.Module.Assembly.Name.Name };
        public static Lookup Unchecked(string assembly) => new() { Assembly = assembly, NotSupplied = true };
        public static Lookup Missing(string assembly, string? detail) => new() { Assembly = assembly, Detail = detail };
    }

    private sealed class Entry
    {
        public Entry(BindingFindingKind kind, string assembly, string member, string? detail) { Kind = kind; Assembly = assembly; Member = member; Detail = detail; }
        public BindingFindingKind Kind { get; }
        public string Assembly { get; }
        public string Member { get; }
        public string? Detail { get; }
        public List<string> UsedBy { get; } = new();
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    }

    private readonly AssemblyDefinition _mod;
    private readonly SuppliedAssemblies _supplied;
    private readonly BindingCheckOptions _options;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _unchecked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Lookup> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IMemberDefinition? Found, string? StoppedAt)> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<AssemblyDefinition, bool> _internalsVisible = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignoresAccessChecksTo = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = new();

    public Checker(AssemblyDefinition mod, SuppliedAssemblies supplied, BindingCheckOptions options)
    {
        _mod = mod; _supplied = supplied; _options = options;
    }

    public BindingReport Run(string modPath)
    {
        foreach (CustomAttribute attribute in _mod.CustomAttributes)
            if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute"
                && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string target)
                _ignoresAccessChecksTo.Add(target);

        UseAttributes(_mod, new Site("assembly attributes", null));
        foreach (ModuleDefinition module in _mod.Modules)
        {
            UseAttributes(module, new Site("module attributes", null));
            foreach (TypeDefinition type in module.GetTypes()) WalkType(type);
        }
        // Everything the metadata tables reference, in case a reference is not reachable from a definition walked above.
        var sweep = new Site(MetadataOnly, null, sweep: true);
        foreach (ModuleDefinition module in _mod.Modules)
        {
            foreach (TypeReference type in module.GetTypeReferences()) UseType(type, sweep);
            foreach (MemberReference member in module.GetMemberReferences())
            {
                if (member is FieldReference field) UseField(field, sweep);
                else if (member is MethodReference method) UseMethod(method, sweep);
            }
        }

        var referenced = new HashSet<string>(_mod.Modules.SelectMany(m => m.AssemblyReferences).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        List<BindingFinding> findings = _entries.Values
            .OrderBy(e => e.Assembly, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Member, StringComparer.Ordinal).ThenBy(e => e.Kind)
            .Select(e => new BindingFinding(e.Kind, e.Assembly, e.Member, e.Detail, _ignoresAccessChecksTo.Contains(e.Assembly), e.UsedBy.ToArray()))
            .ToList();
        return new BindingReport(
            modPath,
            _mod.Name.Name,
            findings.Where(f => f.IsMissing).ToArray(),
            findings.Where(f => !f.IsMissing).ToArray(),
            _checked.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new ReferencedAssembly(p.Key, p.Value.Count, _supplied.PathFor(p.Key))).ToArray(),
            _unchecked.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new ReferencedAssembly(p.Key, p.Value.Count)).ToArray(),
            _options.RequiredAssemblies.Where(r => referenced.Contains(r) && _supplied.PathFor(r) == null).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ignoresAccessChecksTo.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray(),
            _notes.ToArray());
    }

    private void WalkType(TypeDefinition type)
    {
        var site = new Site(type.FullName, type);
        UseType(type.BaseType, site);
        foreach (InterfaceImplementation implementation in type.Interfaces) { UseType(implementation.InterfaceType, site); UseAttributes(implementation, site); }
        UseGenericParameters(type, site);
        UseAttributes(type, site);
        foreach (FieldDefinition field in type.Fields)
        {
            var fieldSite = new Site(type.FullName + "::" + field.Name, type);
            UseType(field.FieldType, fieldSite);
            UseAttributes(field, fieldSite);
        }
        foreach (PropertyDefinition property in type.Properties)
        {
            var propertySite = new Site(type.FullName + "::" + property.Name, type);
            UseType(property.PropertyType, propertySite);
            foreach (ParameterDefinition parameter in property.Parameters) UseType(parameter.ParameterType, propertySite);
            UseAttributes(property, propertySite);
        }
        foreach (EventDefinition @event in type.Events)
        {
            var eventSite = new Site(type.FullName + "::" + @event.Name, type);
            UseType(@event.EventType, eventSite);
            UseAttributes(@event, eventSite);
        }
        foreach (MethodDefinition method in type.Methods) WalkMethod(method);
    }

    private void WalkMethod(MethodDefinition method)
    {
        var site = new Site(MethodName(method), method.DeclaringType);
        UseType(method.ReturnType, site);
        UseAttributes(method.MethodReturnType, site);
        foreach (ParameterDefinition parameter in method.Parameters) { UseType(parameter.ParameterType, site); UseAttributes(parameter, site); }
        UseGenericParameters(method, site);
        UseAttributes(method, site);
        foreach (MethodReference overridden in method.Overrides) UseMethod(overridden, site);
        if (!method.HasBody) return;
        MethodBody body = method.Body;
        foreach (VariableDefinition variable in body.Variables) UseType(variable.VariableType, site);
        foreach (ExceptionHandler handler in body.ExceptionHandlers) UseType(handler.CatchType, site);
        foreach (Instruction instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case TypeReference type: UseType(type, site); break;
                case FieldReference field: UseField(field, site); break;
                case MethodReference called: UseMethod(called, site); break;
                case CallSite callSite:
                    UseType(callSite.ReturnType, site);
                    foreach (ParameterDefinition parameter in callSite.Parameters) UseType(parameter.ParameterType, site);
                    break;
            }
        }
    }

    private void UseGenericParameters(IGenericParameterProvider provider, Site site)
    {
        if (!provider.HasGenericParameters) return;
        foreach (GenericParameter parameter in provider.GenericParameters)
        {
            UseAttributes(parameter, site);
            foreach (GenericParameterConstraint constraint in parameter.Constraints) UseType(constraint.ConstraintType, site);
        }
    }

    private void UseAttributes(ICustomAttributeProvider provider, Site site)
    {
        if (!provider.HasCustomAttributes) return;
        foreach (CustomAttribute attribute in provider.CustomAttributes)
        {
            UseMethod(attribute.Constructor, site);
            // typeof(...) and enum arguments are stored by name; decoding an enum needs its assembly.
            try
            {
                foreach (CustomAttributeArgument argument in attribute.ConstructorArguments) UseArgument(argument, site);
                foreach (CustomAttributeNamedArgument named in attribute.Fields) UseArgument(named.Argument, site);
                foreach (CustomAttributeNamedArgument named in attribute.Properties) UseArgument(named.Argument, site);
            }
            catch (Exception error)
            {
                _notes.Add($"Could not read the arguments of [{attribute.AttributeType.FullName}] on {site.Where}, so types they name were not checked: {error.Message}");
            }
        }
    }

    private void UseArgument(CustomAttributeArgument argument, Site site)
    {
        UseType(argument.Type, site);
        switch (argument.Value)
        {
            case TypeReference type: UseType(type, site); break;
            case CustomAttributeArgument boxed: UseArgument(boxed, site); break;
            case CustomAttributeArgument[] items: foreach (CustomAttributeArgument item in items) UseArgument(item, site); break;
        }
    }

    private void UseType(TypeReference? type, Site site)
    {
        if (type == null) return;
        switch (type)
        {
            case GenericParameter:
            case TypeDefinition:
                return;
            case FunctionPointerType pointer:
                UseType(pointer.ReturnType, site);
                foreach (ParameterDefinition parameter in pointer.Parameters) UseType(parameter.ParameterType, site);
                return;
            case GenericInstanceType instance:
                foreach (TypeReference argument in instance.GenericArguments) UseType(argument, site);
                UseType(instance.ElementType, site);
                return;
            case IModifierType modified:
                UseType(modified.ModifierType, site);
                UseType(modified.ElementType, site);
                return;
            case TypeSpecification specification:
                UseType(specification.ElementType, site);
                return;
        }

        Lookup lookup = Resolve(type);
        if (lookup.IsOwn) return;
        string key = "T:" + ScopeName(type) + ":" + type.FullName;
        if (Skip(key, site)) return;
        if (lookup.NotSupplied) { Count(_unchecked, lookup.Assembly, key); return; }
        Count(_checked, lookup.Assembly, key);
        if (lookup.Type == null) { Add(BindingFindingKind.MissingType, lookup.Assembly, type.FullName, lookup.Detail, site); return; }
        string? problem = TypeAccessProblem(lookup.Type, site.From);
        if (problem != null) Add(BindingFindingKind.InaccessibleType, lookup.Assembly, type.FullName, problem, site);
    }

    private void UseField(FieldReference field, Site site)
    {
        if (field is FieldDefinition) return;
        UseType(field.DeclaringType, site);
        UseType(field.FieldType, site);
        TypeReference owner = Open(field.DeclaringType);
        if (owner is TypeDefinition || owner is TypeSpecification || owner is GenericParameter) return;
        Lookup lookup = Resolve(owner);
        if (lookup.IsOwn) return;
        string display = field.FieldType.FullName + " " + owner.FullName + "::" + field.Name;
        string key = "F:" + ScopeName(owner) + ":" + display;
        if (Skip(key, site)) return;
        if (lookup.NotSupplied) { Count(_unchecked, lookup.Assembly, key); return; }
        Count(_checked, lookup.Assembly, key);
        if (lookup.Type == null) return; // the missing type is the finding
        if (!_members.TryGetValue(key, out var result))
            _members[key] = result = Search(lookup.Type, t => t.Fields.FirstOrDefault(f => f.Name == field.Name && Same(f.FieldType, field.FieldType)));
        if (result.Found is FieldDefinition found)
        {
            string? problem = MemberAccessProblem((int)(found.Attributes & FieldAttributes.FieldAccessMask), found.DeclaringType, site.From);
            if (problem != null) Add(BindingFindingKind.InaccessibleField, Name(found.DeclaringType), display, problem, site);
        }
        else Add(BindingFindingKind.MissingField, lookup.Assembly, display, FieldHint(lookup.Type, field, result.StoppedAt), site);
    }

    private void UseMethod(MethodReference method, Site site)
    {
        if (method is GenericInstanceMethod instance)
        {
            foreach (TypeReference argument in instance.GenericArguments) UseType(argument, site);
            UseMethod(instance.ElementMethod, site);
            return;
        }
        if (method is MethodDefinition) return;
        UseType(method.DeclaringType, site);
        UseType(method.ReturnType, site);
        foreach (ParameterDefinition parameter in method.Parameters) UseType(parameter.ParameterType, site);
        TypeReference owner = Open(method.DeclaringType);
        // Methods of array types (Get, Set, Address, the multi-dimensional constructors) are supplied by the runtime.
        if (owner is TypeDefinition || owner is TypeSpecification || owner is GenericParameter) return;
        Lookup lookup = Resolve(owner);
        if (lookup.IsOwn) return;
        string display = MethodDisplay(method, owner);
        string key = "M:" + ScopeName(owner) + ":" + display;
        if (Skip(key, site)) return;
        if (lookup.NotSupplied) { Count(_unchecked, lookup.Assembly, key); return; }
        Count(_checked, lookup.Assembly, key);
        if (lookup.Type == null) return;
        if (!_members.TryGetValue(key, out var result))
            _members[key] = result = Search(lookup.Type, t => t.Methods.FirstOrDefault(m => Matches(m, method)));
        if (result.Found is MethodDefinition found)
        {
            string? problem = MemberAccessProblem((int)(found.Attributes & MethodAttributes.MemberAccessMask), found.DeclaringType, site.From);
            if (problem != null) Add(BindingFindingKind.InaccessibleMethod, Name(found.DeclaringType), display, problem, site);
        }
        else Add(BindingFindingKind.MissingMethod, lookup.Assembly, display, MethodHint(lookup.Type, method, result.StoppedAt), site);
    }

    // A reference seen from code is marked; the metadata sweep then handles only what code never reached.
    private bool Skip(string key, Site site) => !_seen.Add(key) && site.Sweep;

    private static void Count(Dictionary<string, HashSet<string>> counts, string assembly, string key)
    {
        if (!counts.TryGetValue(assembly, out HashSet<string>? keys)) counts[assembly] = keys = new HashSet<string>(StringComparer.Ordinal);
        keys.Add(key);
    }

    private void Add(BindingFindingKind kind, string assembly, string member, string? detail, Site site)
    {
        string key = kind + "|" + assembly + "|" + member;
        if (!_entries.TryGetValue(key, out Entry? entry)) _entries[key] = entry = new Entry(kind, assembly, member, detail);
        if (site.Sweep && entry.UsedBy.Count > 0) return;
        if (entry.Seen.Add(site.Where)) entry.UsedBy.Add(site.Where);
    }

    private Lookup Resolve(TypeReference type)
    {
        if (type is TypeDefinition) return Lookup.Own;
        string key = ScopeName(type) + "|" + type.FullName;
        if (_types.TryGetValue(key, out Lookup? cached)) return cached;
        Lookup result;
        if (type.DeclaringType != null)
        {
            Lookup outer = Resolve(type.DeclaringType);
            if (outer.IsOwn || outer.NotSupplied) result = outer;
            else if (outer.Type == null) result = Lookup.Missing(outer.Assembly, "its declaring type " + type.DeclaringType.FullName + " is missing");
            else
            {
                TypeDefinition? nested = outer.Type.NestedTypes.FirstOrDefault(n => n.Name == type.Name);
                result = nested != null ? Lookup.Found(nested) : Lookup.Missing(outer.Assembly, null);
            }
        }
        else if (type.Scope is AssemblyNameReference scope) result = FindTopLevel(scope.Name, type.Namespace, type.Name);
        else result = Lookup.Own; // a module of the mod itself
        _types[key] = result;
        return result;
    }

    private Lookup FindTopLevel(string assemblyName, string ns, string name)
    {
        for (int hop = 0; hop < 16; hop++)
        {
            AssemblyDefinition? assembly = _supplied.Get(assemblyName);
            if (assembly == null) return Lookup.Unchecked(assemblyName);
            foreach (ModuleDefinition module in assembly.Modules)
                if (module.GetType(ns, name) is { } found) return Lookup.Found(found);
            ExportedType? forwarder = assembly.Modules.SelectMany(m => m.ExportedTypes)
                .FirstOrDefault(e => e.IsForwarder && e.DeclaringType == null && e.Namespace == ns && e.Name == name);
            if (forwarder?.Scope is AssemblyNameReference target) { assemblyName = target.Name; continue; }
            string[] elsewhere = assembly.Modules.SelectMany(m => m.Types).Where(t => t.Name == name).Select(t => t.FullName).Take(3).ToArray();
            return Lookup.Missing(assembly.Name.Name, elsewhere.Length > 0 ? "a type with this name exists as " + string.Join(", ", elsewhere) : null);
        }
        return Lookup.Missing(assemblyName, "type forwarding does not end");
    }

    // Looks in the type, then in its base types, as the runtime binds a member reference. StoppedAt names the assembly of
    // a base type that was not supplied, so the member might still be declared there.
    private (IMemberDefinition? Found, string? StoppedAt) Search(TypeDefinition start, Func<TypeDefinition, IMemberDefinition?> find)
    {
        TypeDefinition? type = start;
        for (int depth = 0; type != null && depth < 64; depth++)
        {
            if (find(type) is { } found) return (found, null);
            if (type.BaseType == null) return (null, null);
            TypeReference open = Open(type.BaseType);
            if (open is TypeDefinition local) { type = local; continue; }
            Lookup baseLookup = Resolve(open);
            // Game members are not declared on the framework's root types, so an unsupplied one is not worth a note.
            if (baseLookup.NotSupplied) return (null, open.FullName is "System.Object" or "System.ValueType" or "System.Enum" ? null : baseLookup.Assembly);
            type = baseLookup.Type;
        }
        return (null, null);
    }

    private IEnumerable<TypeDefinition> Chain(TypeDefinition start)
    {
        TypeDefinition? type = start;
        for (int depth = 0; type != null && depth < 64; depth++)
        {
            yield return type;
            if (type.BaseType == null) yield break;
            TypeReference open = Open(type.BaseType);
            type = open as TypeDefinition ?? Resolve(open).Type;
        }
    }

    private string? FieldHint(TypeDefinition type, FieldReference field, string? stoppedAt)
    {
        var hints = new List<string>();
        foreach (TypeDefinition t in Chain(type))
        {
            foreach (FieldDefinition f in t.Fields.Where(f => f.Name == field.Name))
                hints.Add($"{t.FullName}::{f.Name} has type {f.FieldType.FullName}");
            foreach (PropertyDefinition p in t.Properties.Where(p => p.Name == field.Name))
                hints.Add($"{t.FullName}::{p.Name} is a property");
        }
        return Hint(hints, stoppedAt);
    }

    private string? MethodHint(TypeDefinition type, MethodReference method, string? stoppedAt)
    {
        var sameName = new List<string>();
        var hints = new List<string>();
        string? accessorOf = method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Name.Substring(4) : null;
        foreach (TypeDefinition t in Chain(type))
        {
            foreach (MethodDefinition m in t.Methods.Where(m => m.Name == method.Name))
                sameName.Add((m.HasThis != method.HasThis ? (m.HasThis ? "instance " : "static ") : "") + MethodDisplay(m, t));
            if (accessorOf != null)
                foreach (FieldDefinition f in t.Fields.Where(f => f.Name == accessorOf))
                    hints.Add($"{t.FullName}::{f.Name} is a field");
        }
        if (sameName.Count > 0) hints.Insert(0, "same name: " + string.Join(", ", sameName.Take(5)) + (sameName.Count > 5 ? ", ..." : ""));
        return Hint(hints, stoppedAt);
    }

    private static string? Hint(List<string> hints, string? stoppedAt)
    {
        if (stoppedAt != null) hints.Add($"a base type is in {stoppedAt}, which was not supplied, so it was not searched");
        return hints.Count == 0 ? null : string.Join("; ", hints);
    }

    private string? TypeAccessProblem(TypeDefinition type, TypeDefinition? from)
    {
        for (TypeDefinition? t = type; t != null; t = t.DeclaringType)
        {
            bool visible = InternalsVisible(t.Module.Assembly);
            bool ok = t.IsNested
                ? t.IsNestedPublic
                  || ((t.IsNestedFamily || t.IsNestedFamilyOrAssembly) && DerivesFrom(from, t.DeclaringType))
                  || ((t.IsNestedAssembly || t.IsNestedFamilyOrAssembly) && visible)
                  || (t.IsNestedFamilyAndAssembly && visible && DerivesFrom(from, t.DeclaringType))
                : t.IsPublic || visible;
            if (ok) continue;
            string access = !t.IsNested ? "internal" : t.IsNestedPrivate ? "private" : t.IsNestedFamily ? "protected" : t.IsNestedAssembly ? "internal"
                : t.IsNestedFamilyAndAssembly ? "private protected" : "protected internal";
            return t == type ? access : $"its declaring type {t.FullName} is {access}";
        }
        return null;
    }

    // The member access values are the same in FieldAttributes and MethodAttributes.
    private string? MemberAccessProblem(int access, TypeDefinition declaring, TypeDefinition? from)
    {
        bool visible = InternalsVisible(declaring.Module.Assembly);
        bool ok = access switch
        {
            6 => true,                                                   // public
            5 => visible || DerivesFrom(from, declaring),                // protected internal
            4 => DerivesFrom(from, declaring),                           // protected
            3 => visible,                                                // internal
            2 => visible && DerivesFrom(from, declaring),                // private protected
            _ => false,                                                  // private, compiler-controlled
        };
        if (ok) return null;
        return access switch { 5 => "protected internal", 4 => "protected", 3 => "internal", 2 => "private protected", 1 => "private", _ => "compiler-controlled" };
    }

    private bool DerivesFrom(TypeDefinition? from, TypeDefinition target)
    {
        for (TypeDefinition? outer = from; outer != null; outer = outer.DeclaringType)
            foreach (TypeDefinition t in Chain(outer))
                if (t == target) return true;
        return false;
    }

    private bool InternalsVisible(AssemblyDefinition target)
    {
        if (_internalsVisible.TryGetValue(target, out bool visible)) return visible;
        visible = false;
        try
        {
            foreach (CustomAttribute attribute in target.CustomAttributes)
                if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"
                    && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is string friend
                    && friend.Split(',')[0].Trim().Equals(_mod.Name.Name, StringComparison.OrdinalIgnoreCase))
                    visible = true;
        }
        catch (Exception error) { _notes.Add($"Could not read the attributes of {target.Name.Name}: {error.Message}"); }
        _internalsVisible[target] = visible;
        return visible;
    }

    // The runtime compares a member reference with a definition by name, calling convention, generic arity and
    // signature; type names are compared, not the assemblies that define them.
    private static bool Matches(MethodDefinition definition, MethodReference reference)
    {
        if (definition.Name != reference.Name || definition.HasThis != reference.HasThis) return false;
        if (definition.GenericParameters.Count != reference.GenericParameters.Count) return false;
        if (definition.Parameters.Count != reference.Parameters.Count) return false;
        if (!Same(definition.ReturnType, reference.ReturnType)) return false;
        for (int i = 0; i < definition.Parameters.Count; i++)
            if (!Same(definition.Parameters[i].ParameterType, reference.Parameters[i].ParameterType)) return false;
        return true;
    }

    private static bool Same(TypeReference? a, TypeReference? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is GenericParameter ga) return b is GenericParameter gb && ga.Position == gb.Position && ga.Type == gb.Type;
        if (b is GenericParameter) return false;
        if (a is FunctionPointerType fa)
        {
            if (b is not FunctionPointerType fb || fa.Parameters.Count != fb.Parameters.Count || !Same(fa.ReturnType, fb.ReturnType)) return false;
            for (int i = 0; i < fa.Parameters.Count; i++)
                if (!Same(fa.Parameters[i].ParameterType, fb.Parameters[i].ParameterType)) return false;
            return true;
        }
        if (a is TypeSpecification sa)
        {
            if (b is not TypeSpecification sb || a.GetType() != b.GetType()) return false;
            switch (a)
            {
                case ArrayType array when array.Rank != ((ArrayType)b).Rank: return false;
                case GenericInstanceType instance:
                    var other = (GenericInstanceType)b;
                    if (instance.GenericArguments.Count != other.GenericArguments.Count) return false;
                    for (int i = 0; i < instance.GenericArguments.Count; i++)
                        if (!Same(instance.GenericArguments[i], other.GenericArguments[i])) return false;
                    break;
                case IModifierType modified when !Same(modified.ModifierType, ((IModifierType)b).ModifierType): return false;
            }
            return Same(sa.ElementType, sb.ElementType);
        }
        if (b is TypeSpecification) return false;
        if (a.Name != b.Name || a.Namespace != b.Namespace) return false;
        return Same(a.DeclaringType, b.DeclaringType);
    }

    private static TypeReference Open(TypeReference type) => type is GenericInstanceType instance ? instance.ElementType : type;

    private static string ScopeName(TypeReference type) => type.Scope?.Name ?? "";

    private static string Name(TypeDefinition type) => type.Module.Assembly.Name.Name;

    private static string MethodDisplay(MethodReference method, TypeReference owner) =>
        method.ReturnType.FullName + " " + owner.FullName + "::" + method.Name
        + (method.GenericParameters.Count > 0 ? "<" + string.Join(",", method.GenericParameters.Select(p => "!!" + p.Position)) + ">" : "")
        + "(" + string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName)) + ")";

    private static string MethodName(MethodDefinition method) =>
        method.DeclaringType.FullName + "::" + method.Name + "(" + string.Join(", ", method.Parameters.Select(p => p.ParameterType.Name)) + ")";
}
