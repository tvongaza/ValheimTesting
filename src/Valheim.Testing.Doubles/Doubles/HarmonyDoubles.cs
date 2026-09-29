// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// HarmonyLib, as far as linked mod sources need it: the patch attributes with every overload and the target they
// record, a Harmony instance whose PatchAll lists the patch classes it finds (nothing is patched), and the reflection
// helpers Traverse and AccessTools, which work on real objects as Harmony's do.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HarmonyLib
{
    public enum MethodType { Normal, Getter, Setter, Constructor, StaticConstructor, Enumerator, Async }
    public enum ArgumentType { Normal, Ref, Out, Pointer }
    public enum HarmonyPatchType { All, Prefix, Postfix, Transpiler, Finalizer, ReversePatch }

    /// <summary>Harmony's patch priorities: higher runs earlier.</summary>
    public static partial class Priority
    {
        public const int Last = 0, VeryLow = 100, Low = 200, LowerThanNormal = 300, Normal = 400, HigherThanNormal = 500, High = 600, VeryHigh = 700, First = 800;
    }

    /// <summary>What a patch attribute says: the target and the ordering hints.</summary>
    public partial class HarmonyMethod
    {
        public MethodInfo? method;
        public string? category;
        public Type? declaringType;
        public string? methodName;
        public MethodType? methodType;
        public Type[]? argumentTypes;
        public int priority = -1;
        public string[]? before;
        public string[]? after;
        public HarmonyPatchType? reversePatchType;
        public bool? debug;
        public HarmonyMethod() { }
        public HarmonyMethod(MethodInfo method) { this.method = method; }

        /// <summary>Several attributes' information in one, later ones filling what earlier ones left unset, as Harmony merges a class's patch attributes.</summary>
        public static HarmonyMethod Merge(List<HarmonyMethod> attributes)
        {
            var result = new HarmonyMethod();
            foreach (var info in attributes)
            {
                if (info.method != null) result.method = info.method;
                if (info.category != null) result.category = info.category;
                if (info.declaringType != null) result.declaringType = info.declaringType;
                if (info.methodName != null) result.methodName = info.methodName;
                if (info.methodType != null) result.methodType = info.methodType;
                if (info.argumentTypes != null) result.argumentTypes = info.argumentTypes;
                if (info.priority != -1) result.priority = info.priority;
                if (info.before != null) result.before = info.before;
                if (info.after != null) result.after = info.after;
                if (info.reversePatchType != null) result.reversePatchType = info.reversePatchType;
                if (info.debug != null) result.debug = info.debug;
            }
            return result;
        }
        public override string ToString() =>
            $"{declaringType?.FullName ?? "?"}.{methodName ?? (methodType?.ToString() ?? "?")}" +
            (argumentTypes == null ? "" : "(" + string.Join(", ", argumentTypes.Select(t => t.Name).ToArray()) + ")");
    }

    public static partial class HarmonyMethodExtensions
    {
        /// <summary>The information of every Harmony attribute on the type, in declaration order.</summary>
        public static List<HarmonyMethod> GetFromType(Type type) =>
            type.GetCustomAttributes(true).OfType<HarmonyAttribute>().Select(a => a.info).ToList();
        public static List<HarmonyMethod> GetFromMethod(MethodBase method) =>
            method.GetCustomAttributes(true).OfType<HarmonyAttribute>().Select(a => a.info).ToList();
    }

    /// <summary>Base of Harmony's attributes: <see cref="info"/> holds what the attribute says.</summary>
    public partial class HarmonyAttribute : Attribute
    {
        public HarmonyMethod info = new();
    }

    /// <summary>A patch's target, with every constructor Harmony 2 has, so linked patch classes compile; the attribute records the target in <see cref="HarmonyAttribute.info"/>.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = true)]
    public partial class HarmonyPatch : HarmonyAttribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type declaringType) { info.declaringType = declaringType; }
        public HarmonyPatch(Type declaringType, Type[] argumentTypes) { info.declaringType = declaringType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, string methodName) { info.declaringType = declaringType; info.methodName = methodName; }
        public HarmonyPatch(Type declaringType, string methodName, params Type[] argumentTypes) { info.declaringType = declaringType; info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.declaringType = declaringType; info.methodName = methodName; Arguments(argumentTypes, argumentVariations); }
        public HarmonyPatch(Type declaringType, MethodType methodType) { info.declaringType = declaringType; info.methodType = methodType; }
        public HarmonyPatch(Type declaringType, MethodType methodType, params Type[] argumentTypes) { info.declaringType = declaringType; info.methodType = methodType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.declaringType = declaringType; info.methodType = methodType; Arguments(argumentTypes, argumentVariations); }
        public HarmonyPatch(Type declaringType, string methodName, MethodType methodType) { info.declaringType = declaringType; info.methodName = methodName; info.methodType = methodType; }
        public HarmonyPatch(string methodName) { info.methodName = methodName; }
        public HarmonyPatch(string methodName, params Type[] argumentTypes) { info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.methodName = methodName; Arguments(argumentTypes, argumentVariations); }
        public HarmonyPatch(string methodName, MethodType methodType) { info.methodName = methodName; info.methodType = methodType; }
        public HarmonyPatch(MethodType methodType) { info.methodType = methodType; }
        public HarmonyPatch(MethodType methodType, params Type[] argumentTypes) { info.methodType = methodType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.methodType = methodType; Arguments(argumentTypes, argumentVariations); }
        public HarmonyPatch(Type[] argumentTypes) { info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type[] argumentTypes, ArgumentType[] argumentVariations) { Arguments(argumentTypes, argumentVariations); }
        /// <summary>A target named by its type's name, resolved as Harmony does (<see cref="AccessTools.TypeByName"/>).</summary>
        public HarmonyPatch(string typeName, string methodName, MethodType methodType = MethodType.Normal, Type[]? argumentTypes = null, ArgumentType[]? argumentVariations = null)
        {
            info.declaringType = AccessTools.TypeByName(typeName); info.methodName = methodName; info.methodType = methodType;
            if (argumentTypes != null) Arguments(argumentTypes, argumentVariations ?? new ArgumentType[0]);
        }

        // As Harmony: Ref and Out make by-ref types, Pointer a pointer type.
        private void Arguments(Type[] argumentTypes, ArgumentType[] argumentVariations)
        {
            if (argumentVariations.Length != 0 && argumentVariations.Length != argumentTypes.Length)
                throw new ArgumentException("argumentVariations must be as long as argumentTypes", nameof(argumentVariations));
            var types = new Type[argumentTypes.Length];
            for (int i = 0; i < types.Length; i++)
            {
                var variation = argumentVariations.Length == 0 ? ArgumentType.Normal : argumentVariations[i];
                types[i] = variation switch
                {
                    ArgumentType.Ref or ArgumentType.Out => argumentTypes[i].MakeByRefType(),
                    ArgumentType.Pointer => argumentTypes[i].MakePointerType(),
                    _ => argumentTypes[i],
                };
            }
            info.argumentTypes = types;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public partial class HarmonyPriority : HarmonyAttribute { public HarmonyPriority(int priority) { info.priority = priority; } }
    /// <summary>Run this patch before the patches of these Harmony ids.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public partial class HarmonyBefore : HarmonyAttribute { public HarmonyBefore(params string[] before) { info.before = before; } }
    /// <summary>Run this patch after the patches of these Harmony ids.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public partial class HarmonyAfter : HarmonyAttribute { public HarmonyAfter(params string[] after) { info.after = after; } }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyTranspiler : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyFinalizer : Attribute { }

    /// <summary>
    /// A Harmony instance. <see cref="PatchAll()"/> finds the patch classes (classes with a <see cref="HarmonyPatch"/>
    /// attribute) as Harmony does and records them with their merged target in <see cref="Patches"/>; nothing is patched
    /// and targets are not resolved, so a test calls a prefix or postfix itself. Unpatching forgets the records.
    /// </summary>
    public partial class Harmony
    {
        private static readonly Dictionary<string, List<(Type PatchClass, HarmonyMethod Target)>> s_patches = new();
        public string Id { get; }
        public Harmony(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id cannot be null or empty", nameof(id));
            Id = id;
        }
        /// <summary>The patch classes this id has "applied", with the target their attributes name.</summary>
        public IReadOnlyList<(Type PatchClass, HarmonyMethod Target)> Patches => s_patches.TryGetValue(Id, out var list) ? list : new List<(Type, HarmonyMethod)>();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void PatchAll() => PatchAll(Assembly.GetCallingAssembly());
        public void PatchAll(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.Where(t => t != null).ToArray()!; }
            foreach (var type in types) Record(type);
        }
        // Records one patch class when it carries a HarmonyPatch attribute.
        private void Record(Type type)
        {
            var infos = HarmonyMethodExtensions.GetFromType(type);
            if (!type.GetCustomAttributes(typeof(HarmonyPatch), true).Any()) return;
            if (!s_patches.TryGetValue(Id, out var list)) s_patches[Id] = list = new();
            if (!list.Any(p => p.PatchClass == type)) list.Add((type, HarmonyMethod.Merge(infos)));
        }
        public void UnpatchSelf() => s_patches.Remove(Id);
        public void UnpatchAll(string? harmonyID = null) { if (harmonyID == null) s_patches.Clear(); else s_patches.Remove(harmonyID); }
        public static bool HasAnyPatches(string harmonyID) => s_patches.TryGetValue(harmonyID, out var list) && list.Count > 0;
    }

    /// <summary>Harmony's reflection helpers: members of any visibility, found on the type or its base types.</summary>
    public static partial class AccessTools
    {
        public static readonly BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.GetField | BindingFlags.SetField | BindingFlags.GetProperty | BindingFlags.SetProperty;
        public static readonly BindingFlags allDeclared = all | BindingFlags.DeclaredOnly;

        private static T? Walk<T>(Type? type, Func<Type, T?> find) where T : class
        {
            for (var t = type; t != null; t = t.BaseType) { var found = find(t); if (found != null) return found; }
            return null;
        }
        public static FieldInfo? DeclaredField(Type type, string name) => type.GetField(name, allDeclared);
        public static FieldInfo? Field(Type type, string name) => Walk(type, t => t.GetField(name, allDeclared));
        public static PropertyInfo? DeclaredProperty(Type type, string name) => type.GetProperty(name, allDeclared);
        public static PropertyInfo? Property(Type type, string name) => Walk(type, t => t.GetProperty(name, allDeclared));
        /// <summary>A method by name, and by parameter types when given. Without them an overloaded name is ambiguous and throws, as in Harmony.</summary>
        public static MethodInfo? DeclaredMethod(Type type, string name, Type[]? parameters = null) =>
            parameters == null ? type.GetMethod(name, allDeclared) : type.GetMethod(name, allDeclared, null, parameters, null);
        public static MethodInfo? Method(Type type, string name, Type[]? parameters = null) => Walk(type, t => DeclaredMethod(t, name, parameters));
        public static Type? Inner(Type type, string name) => Walk(type, t => t.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic));
        /// <summary>The argument types for a call: an argument's own type, object for a null.</summary>
        public static Type[] GetTypes(object?[]? args) => args == null ? Type.EmptyTypes : args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
        /// <summary>A type by full name or, failing that, by simple name, from every loaded assembly.</summary>
        public static Type? TypeByName(string name)
        {
            var type = Type.GetType(name, false);
            if (type != null) return type;
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray()!; } }).ToList();
            return types.FirstOrDefault(t => t.FullName == name) ?? types.FirstOrDefault(t => t.Name == name);
        }
    }

    /// <summary>
    /// Harmony's Traverse: walks fields, properties, methods and nested types of any visibility by name. A missing member
    /// gives an empty traverse whose value is null (it does not throw), as in Harmony; setting the value of a method throws.
    /// </summary>
    public partial class Traverse
    {
        private readonly Type? m_type;
        private readonly object? m_root;
        private readonly MemberInfo? m_info;
        private readonly MethodBase? m_method;
        private readonly object?[]? m_params;

        public Traverse() { }
        public Traverse(Type? type) { m_type = type; }
        public Traverse(object? root) { m_root = root; m_type = root?.GetType(); }
        private Traverse(object? root, MemberInfo info, object?[]? index)
        {
            m_root = root; m_info = info; m_params = index;
            m_type = root?.GetType() ?? (info as FieldInfo)?.FieldType ?? (info as PropertyInfo)?.PropertyType;
        }
        private Traverse(object? root, MethodInfo method, object?[]? parameters) { m_root = root; m_type = method.ReturnType; m_method = method; m_params = parameters; }

        public static Traverse Create(Type type) => new(type);
        public static Traverse Create<T>() => Create(typeof(T));
        public static Traverse Create(object? root) => new(root);
        public static Traverse CreateWithType(string name) => new(AccessTools.TypeByName(name));

        /// <summary>The member's value, the method's result (called with the arguments given to <see cref="Method(string, object[])"/>), or the root.</summary>
        public object? GetValue()
        {
            if (m_info is FieldInfo field) return field.GetValue(m_root);
            if (m_info is PropertyInfo property) return property.GetValue(m_root, AccessTools.all, null, m_params, System.Globalization.CultureInfo.CurrentCulture);
            if (m_method != null) return m_method.Invoke(m_root, m_params);
            if (m_root == null && m_type != null) return m_type;
            return m_root;
        }
        public T GetValue<T>() => GetValue() is T value ? value : default!;
        /// <summary>Calls the method with these arguments instead of the ones given to <see cref="Method(string, object[])"/>.</summary>
        public object? GetValue(params object?[] arguments)
        {
            if (m_method == null) throw new InvalidOperationException("cannot get method value without method");
            return m_method.Invoke(m_root, arguments);
        }
        public T GetValue<T>(params object?[] arguments) => GetValue(arguments) is T value ? value : default!;

        public Traverse SetValue(object? value)
        {
            if (m_info is FieldInfo field) field.SetValue(m_root, value, AccessTools.all, null, System.Globalization.CultureInfo.CurrentCulture);
            if (m_info is PropertyInfo property) property.SetValue(m_root, value, AccessTools.all, null, m_params, System.Globalization.CultureInfo.CurrentCulture);
            if (m_method != null) throw new InvalidOperationException($"cannot set value of method {m_method.DeclaringType?.FullName}.{m_method.Name}");
            return this;
        }

        // The object the next step looks in: a member's value, a static member's owner, or this traverse's root.
        private Traverse Resolve()
        {
            if (m_root == null)
            {
                if (m_info is FieldInfo { IsStatic: true } || m_info is PropertyInfo property && property.GetGetMethod(true)?.IsStatic == true || m_method is { IsStatic: true })
                    return new Traverse(GetValue());
                if (m_info == null && m_method == null) return this;
            }
            return m_info == null && m_method == null ? this : new Traverse(GetValue());
        }

        public Traverse Type(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (m_type == null) return new Traverse();
            var type = AccessTools.Inner(m_type, name);
            return type == null ? new Traverse() : new Traverse(type);
        }
        public Traverse Field(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var resolved = Resolve();
            if (resolved.m_type == null) return new Traverse();
            var field = AccessTools.Field(resolved.m_type, name);
            if (field == null || (!field.IsStatic && resolved.m_root == null)) return new Traverse();
            return new Traverse(resolved.m_root, field, null);
        }
        public Traverse<T> Field<T>(string name) => new(Field(name));
        public List<string> Fields()
        {
            var resolved = Resolve();
            return resolved.m_type == null ? new List<string>() : resolved.m_type.GetFields(AccessTools.allDeclared).Select(f => f.Name).ToList();
        }
        public Traverse Property(string name, object?[]? index = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var resolved = Resolve();
            if (resolved.m_type == null) return new Traverse();
            var property = AccessTools.Property(resolved.m_type, name);
            if (property == null || (resolved.m_root == null && property.GetGetMethod(true)?.IsStatic != true)) return new Traverse();
            return new Traverse(resolved.m_root, property, index);
        }
        public Traverse<T> Property<T>(string name, object?[]? index = null) => new(Property(name, index));
        public List<string> Properties()
        {
            var resolved = Resolve();
            return resolved.m_type == null ? new List<string>() : resolved.m_type.GetProperties(AccessTools.allDeclared).Select(p => p.Name).ToList();
        }
        /// <summary>A method found by its name and the types of <paramref name="arguments"/>; <see cref="GetValue()"/> calls it with them.</summary>
        public Traverse Method(string name, params object?[] arguments)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var resolved = Resolve();
            if (resolved.m_type == null) return new Traverse();
            var method = AccessTools.Method(resolved.m_type, name, AccessTools.GetTypes(arguments));
            return method == null ? new Traverse() : new Traverse(resolved.m_root, method, arguments);
        }
        public Traverse Method(string name, Type[] paramTypes, object?[]? arguments = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var resolved = Resolve();
            if (resolved.m_type == null) return new Traverse();
            var method = AccessTools.Method(resolved.m_type, name, paramTypes);
            return method == null ? new Traverse() : new Traverse(resolved.m_root, method, arguments);
        }
        public List<string> Methods()
        {
            var resolved = Resolve();
            return resolved.m_type == null ? new List<string>() : resolved.m_type.GetMethods(AccessTools.allDeclared).Select(m => m.Name).ToList();
        }

        public bool FieldExists() => m_info is FieldInfo;
        public bool PropertyExists() => m_info is PropertyInfo;
        public bool MethodExists() => m_method != null;
        public bool TypeExists() => m_type != null;
        public override string ToString() => (m_method ?? GetValue())?.ToString() ?? "";
    }

    /// <summary>A typed view of a traverse's field or property.</summary>
    public partial class Traverse<T>
    {
        private readonly Traverse m_traverse;
        public Traverse(Traverse traverse) { m_traverse = traverse; }
        public T Value { get => m_traverse.GetValue<T>(); set => m_traverse.SetValue(value); }
    }
}
