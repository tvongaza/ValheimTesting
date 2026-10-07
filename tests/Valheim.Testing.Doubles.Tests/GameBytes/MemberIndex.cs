using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

// One line per doubled type and member, for docs/packages/Valheim.Testing.Doubles.members.txt (the doubles' side) and for
// GameBytes/game-members.txt (the game's verdict on each of those lines). Both sides format with this class: the tests
// with the doubles compiled into this assembly, tools/doubles-members/capture-game-members.cs with the game's
// assemblies read as metadata. So a line means the same member on either side.
public static class MemberIndex
{
    /// <summary>The namespaces whose types stand in for game, Unity, BepInEx, Harmony, Jotunn and platform types (and the global one).</summary>
    public static readonly string[] Namespaces = { "UnityEngine", "BepInEx", "HarmonyLib", "Jotunn", "Splatform", "Steamworks", "SoftReferenceableAssets" };

    public const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static bool IsDoubledNamespace(string? ns) => ns == null || Namespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal));

    /// <summary>A type's name as the index writes it: namespace, nested types joined with '.', generic parameters in angle brackets.</summary>
    public static string TypeName(Type type)
    {
        string name = Named(type, type.GetGenericArguments());
        return string.IsNullOrEmpty(type.Namespace) ? name : type.Namespace + "." + name;
    }

    /// <summary>
    /// Visible to a mod's linked sources: anything but private. The doubles compile into the consumer's own assembly, so
    /// their internal members are as callable from mod code as their public ones.
    /// </summary>
    public static bool Visible(MemberInfo member) => member switch
    {
        FieldInfo f => !f.IsPrivate,
        MethodBase m => !m.IsPrivate,
        PropertyInfo p => p.GetAccessors(true).Any(a => Visible(a)),
        EventInfo e => e.AddMethod != null && Visible(e.AddMethod),
        Type t => t.IsNested ? !t.IsNestedPrivate : true,
        _ => false,
    };

    public static bool VisibleType(Type type) => Visible(type) && (!type.IsNested || VisibleType(type.DeclaringType!));

    /// <summary>Public, or protected for a subclass: what the game's own assemblies show a mod.</summary>
    public static bool Public(MemberInfo member) => member switch
    {
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        PropertyInfo p => p.GetAccessors(true).Any(a => Public(a)),
        EventInfo e => e.AddMethod != null && Public(e.AddMethod),
        Type t => (t.IsNested ? t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem : t.IsPublic) && (!t.IsNested || Public(t.DeclaringType!)),
        _ => false,
    };

    /// <summary>The members of <paramref name="type"/> that the index lists: declared here, not compiler-made, not an accessor.</summary>
    public static IEnumerable<MemberInfo> Members(Type type, bool visibleOnly = true)
    {
        if (IsDelegate(type)) yield break;
        foreach (var member in type.GetMembers(Declared))
        {
            if (member is Type) continue;
            if (visibleOnly && !Visible(member)) continue;
            if (member.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute")) continue;
            if (member is MethodInfo method && method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal)) continue;
            if (member is ConstructorInfo ctor && ctor.IsStatic) continue;
            if (member is FieldInfo field && field.IsSpecialName) continue;
            yield return member;
        }
    }

    public static string Kind(MemberInfo member) => member switch
    {
        FieldInfo => "field",
        PropertyInfo => "property",
        ConstructorInfo => "ctor",
        MethodInfo => "method",
        EventInfo => "event",
        _ => "type",
    };

    /// <summary>
    /// The member's signature: name, parameter types and value type. Fields and properties both read <c>name : Type</c>, so
    /// writability is not compared: a writable field the game has as a get-only property still reads "game".
    /// </summary>
    public static string Signature(MemberInfo member)
    {
        switch (member)
        {
            case FieldInfo f: return (f.IsStatic ? "static " : "") + f.Name + " : " + Show(f.FieldType);
            case PropertyInfo p:
            {
                var get = p.GetGetMethod(true); var set = p.GetSetMethod(true);
                bool isStatic = (get ?? set)!.IsStatic;
                var index = p.GetIndexParameters();
                string name = index.Length == 0 ? p.Name : "this[" + string.Join(", ", index.Select(Parameter)) + "]";
                return (isStatic ? "static " : "") + name + " : " + Show(p.PropertyType);
            }
            case ConstructorInfo c: return ".ctor(" + string.Join(", ", c.GetParameters().Select(Parameter)) + ")";
            case MethodInfo m:
            {
                string generic = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
                return (m.IsStatic ? "static " : "") + m.Name + generic + "(" + string.Join(", ", m.GetParameters().Select(Parameter)) + ") : " + Show(m.ReturnType);
            }
            case EventInfo e: return (e.AddMethod?.IsStatic == true ? "static " : "") + e.Name + " : " + Show(e.EventHandlerType!);
            default: throw new ArgumentException("Not an indexed member: " + member);
        }
    }

    /// <summary>The type line's description: its kind and base type.</summary>
    public static string Describe(Type type)
    {
        string kind = IsDelegate(type) ? "delegate" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsInterface ? "interface"
            : type.IsAbstract && type.IsSealed ? "static class" : "class";
        var baseType = type.BaseType;
        bool showBase = baseType != null && !type.IsValueType && !IsDelegate(type) && baseType.FullName != "System.Object";
        return kind + (showBase ? " : " + Show(baseType!) : "");
    }

    /// <summary>The member a signature names, by name and kind, to explain a mismatch: the same name with another signature.</summary>
    public static string NameOf(string signature)
    {
        string s = signature.StartsWith("static ", StringComparison.Ordinal) ? signature.Substring(7) : signature;
        int end = s.IndexOfAny(new[] { '(', ' ', '<', '[' });
        return end < 0 ? s : s.Substring(0, end);
    }

    private static bool IsDelegate(Type type) => type.BaseType?.FullName == "System.MulticastDelegate";

    private static string Parameter(ParameterInfo p)
    {
        if (!p.ParameterType.IsByRef) return (p.CustomAttributes.Any(a => a.AttributeType.Name == "ParamArrayAttribute") ? "params " : "") + Show(p.ParameterType);
        return (p.IsOut ? "out " : p.IsIn ? "in " : "ref ") + Show(p.ParameterType.GetElementType()!);
    }

    /// <summary>
    /// A type as a signature shows it: C# keywords, generic arguments, arrays; a System type by its short name and any
    /// other with its namespace, so <c>UnityEngine.Random</c> and <c>System.Random</c> never read the same.
    /// </summary>
    public static string Show(Type type)
    {
        if (type.IsByRef) return "ref " + Show(type.GetElementType()!);
        if (type.IsArray) return Show(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsPointer) return Show(type.GetElementType()!) + "*";
        if (type.IsGenericParameter) return type.Name;
        switch (type.FullName)
        {
            case "System.Void": return "void";
            case "System.Object": return "object";
            case "System.String": return "string";
            case "System.Boolean": return "bool";
            case "System.Byte": return "byte";
            case "System.SByte": return "sbyte";
            case "System.Int16": return "short";
            case "System.UInt16": return "ushort";
            case "System.Int32": return "int";
            case "System.UInt32": return "uint";
            case "System.Int64": return "long";
            case "System.UInt64": return "ulong";
            case "System.Single": return "float";
            case "System.Double": return "double";
            case "System.Decimal": return "decimal";
            case "System.Char": return "char";
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1") return Show(type.GetGenericArguments()[0]) + "?";
        string named = Named(type, type.IsGenericType ? type.GetGenericArguments() : Array.Empty<Type>());
        string ns = type.Namespace ?? "";
        return ns.Length == 0 || ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ? named : ns + "." + named;
    }

    // A nested type of a generic one carries its parent's arguments first: Dictionary<int, string>.ValueCollection.
    private static string Named(Type type, Type[] args)
    {
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);
        int inherited = 0;
        string prefix = "";
        if (type.IsNested)
        {
            var parent = type.DeclaringType!;
            inherited = parent.IsGenericType ? parent.GetGenericArguments().Length : 0;
            prefix = Named(parent, args.Take(inherited).ToArray()) + ".";
        }
        var own = args.Skip(inherited).ToArray();
        return prefix + name + (own.Length == 0 ? "" : "<" + string.Join(", ", own.Select(Show)) + ">");
    }
}
