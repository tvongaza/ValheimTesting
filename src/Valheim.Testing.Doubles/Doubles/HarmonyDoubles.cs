// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// HarmonyLib as BepInEx 5 ships it (HarmonyX 2.9.0), as far as linked mod sources need it: the patch attributes with
// every constructor and the target they record, a Harmony instance whose PatchAll lists the patch classes it finds
// (nothing is patched and no target is resolved or checked), and the reflection helpers Traverse and AccessTools,
// which work on real objects as HarmonyX's do.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Valheim.Testing.Doubles;

namespace HarmonyLib
{
    public enum MethodType { Normal, Getter, Setter, Constructor, StaticConstructor, Enumerator }
    public enum ArgumentType { Normal, Ref, Out, Pointer }
    public enum HarmonyPatchType { All, Prefix, Postfix, Transpiler, Finalizer, ReversePatch, ILManipulator }
    public enum HarmonyReversePatchType { Original, Snapshot }

    /// <summary>Harmony's patch priorities: higher runs earlier.</summary>
    public static partial class Priority
    {
        public const int Last = 0, VeryLow = 100, Low = 200, LowerThanNormal = 300, Normal = 400, HigherThanNormal = 500, High = 600, VeryHigh = 700, First = 800;
    }

    /// <summary>What a patch attribute says: the target and the ordering hints.</summary>
    public partial class HarmonyMethod
    {
        public MethodInfo? method;
        public Type? declaringType;
        public string? methodName;
        public MethodType? methodType;
        public Type[]? argumentTypes;
        public int priority = -1;
        public string[]? before;
        public string[]? after;
        public HarmonyReversePatchType? reversePatchType;
        public bool? debug;
        public string? debugEmitPath;
        public bool nonVirtualDelegate;
        public bool? wrapTryCatch;
        /// <summary>A target type named by its assembly-qualified name, resolved only when the patch is applied.</summary>
        internal string? assemblyQualifiedDeclaringTypeName;

        public HarmonyMethod() { }
        /// <summary>The method, with what its own Harmony attributes say.</summary>
        public HarmonyMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            Merge(HarmonyMethodExtensions.GetFromMethod(method)).CopyTo(this);
            this.method = method;
        }
        public HarmonyMethod(MethodInfo method, int priority = -1, string[]? before = null, string[]? after = null, bool? debug = null) : this(method)
        {
            this.priority = priority; this.before = before; this.after = after; this.debug = debug;
        }

        internal static readonly FieldInfo[] s_fields =
            typeof(HarmonyMethod).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => f.Name != nameof(method)).ToArray();

        /// <summary>
        /// Several attributes' information in one, as HarmonyX merges a class's attributes: every value an attribute sets
        /// overwrites the earlier ones, except a priority left at -1.
        /// </summary>
        public static HarmonyMethod Merge(List<HarmonyMethod> attributes)
        {
            var result = new HarmonyMethod();
            foreach (var info in attributes) info.CopyTo(result);
            return result;
        }
        public override string ToString() => "HarmonyMethod[" + string.Join(", ", s_fields.Select(f => $"{f.Name}={f.GetValue(this)}").ToArray()) + "]";
    }

    public static partial class HarmonyMethodExtensions
    {
        /// <summary>Copies every value <paramref name="from"/> sets (not null; a priority other than -1) onto <paramref name="to"/>.</summary>
        public static void CopyTo(this HarmonyMethod from, HarmonyMethod to)
        {
            if (to is null) return;
            foreach (var field in HarmonyMethod.s_fields)
            {
                object? value = field.GetValue(from);
                if (value is null || (field.Name == nameof(HarmonyMethod.priority) && (int)value == -1)) continue;
                field.SetValue(to, value);
            }
        }
        public static HarmonyMethod Clone(this HarmonyMethod original) { var copy = new HarmonyMethod(); original.CopyTo(copy); return copy; }
        /// <summary><paramref name="detail"/>'s values over <paramref name="master"/>'s.</summary>
        public static HarmonyMethod Merge(this HarmonyMethod master, HarmonyMethod? detail)
        {
            if (detail is null) return master;
            var result = master.Clone();
            detail.CopyTo(result);
            return result;
        }
        /// <summary>The information of every Harmony attribute on the type.</summary>
        public static List<HarmonyMethod> GetFromType(Type type) =>
            type.GetCustomAttributes(true).OfType<HarmonyAttribute>().Select(a => a.info.Clone()).ToList();
        public static HarmonyMethod GetMergedFromType(Type type) => HarmonyMethod.Merge(GetFromType(type));
        public static List<HarmonyMethod> GetFromMethod(MethodBase method) =>
            method.GetCustomAttributes(true).OfType<HarmonyAttribute>().Select(a => a.info.Clone()).ToList();
        public static HarmonyMethod GetMergedFromMethod(MethodBase method) => HarmonyMethod.Merge(GetFromMethod(method));
    }

    /// <summary>Base of Harmony's attributes: <see cref="info"/> holds what the attribute says.</summary>
    public partial class HarmonyAttribute : Attribute
    {
        public HarmonyMethod info = new();
    }

    /// <summary>A patch's target, with every constructor HarmonyX 2.9 has, so linked patch classes compile; the attribute records the target in <see cref="HarmonyAttribute.info"/>.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = true)]
    public partial class HarmonyPatch : HarmonyAttribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type declaringType) { info.declaringType = declaringType; }
        public HarmonyPatch(Type declaringType, Type[] argumentTypes) { info.declaringType = declaringType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, string methodName) { info.declaringType = declaringType; info.methodName = methodName; }
        public HarmonyPatch(Type declaringType, string methodName, params Type[] argumentTypes) { info.declaringType = declaringType; info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.declaringType = declaringType; info.methodName = methodName; Arguments(argumentTypes, argumentVariations); }
        /// <summary>A target type named by its assembly-qualified name; HarmonyX resolves it only when patching, so it is recorded, not resolved.</summary>
        public HarmonyPatch(string assemblyQualifiedDeclaringType, string methodName) { info.assemblyQualifiedDeclaringTypeName = assemblyQualifiedDeclaringType; info.methodName = methodName; }
        public HarmonyPatch(string assemblyQualifiedDeclaringType, string methodName, MethodType methodType, Type[]? argumentTypes = null, ArgumentType[]? argumentVariations = null)
        {
            info.assemblyQualifiedDeclaringTypeName = assemblyQualifiedDeclaringType; info.methodName = methodName; info.methodType = methodType;
            if (argumentTypes != null) Arguments(argumentTypes, argumentVariations);
        }
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

        // As HarmonyX: no variations keeps the types; more variations than types throws; Ref and Out make by-ref types,
        // Pointer a pointer type. A variation list shorter than the types runs out of range, as it does in HarmonyX.
        private void Arguments(Type[] argumentTypes, ArgumentType[]? argumentVariations)
        {
            if (argumentVariations is not { Length: > 0 } variations) { info.argumentTypes = argumentTypes; return; }
            if (argumentTypes.Length < variations.Length)
                throw new ArgumentException("argumentVariations contains more elements than argumentTypes", nameof(argumentVariations));
            var types = new Type[argumentTypes.Length];
            for (int i = 0; i < types.Length; i++)
                types[i] = variations[i] switch
                {
                    ArgumentType.Ref or ArgumentType.Out => argumentTypes[i].MakeByRefType(),
                    ArgumentType.Pointer => argumentTypes[i].MakePointerType(),
                    _ => argumentTypes[i],
                };
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
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public partial class HarmonyReversePatch : HarmonyAttribute { public HarmonyReversePatch(HarmonyReversePatchType type = HarmonyReversePatchType.Original) { info.reversePatchType = type; } }
    [AttributeUsage(AttributeTargets.Class)] public partial class HarmonyPatchAll : HarmonyAttribute { }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)] public partial class HarmonyDebug : HarmonyAttribute { public HarmonyDebug() { info.debug = true; } }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)] public partial class HarmonyWrapSafe : HarmonyAttribute { public HarmonyWrapSafe() { info.wrapTryCatch = true; } }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyPrepare : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyCleanup : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyTargetMethod : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyTargetMethods : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyTranspiler : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyILManipulator : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public partial class HarmonyFinalizer : Attribute { }
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
    public partial class HarmonyArgument : Attribute
    {
        public string? OriginalName { get; private set; }
        public int Index { get; private set; }
        public string? NewName { get; private set; }
        public HarmonyArgument(string originalName) : this(originalName, null) { }
        public HarmonyArgument(int index) : this(index, null) { }
        public HarmonyArgument(string originalName, string? newName) { OriginalName = originalName; Index = -1; NewName = newName; }
        public HarmonyArgument(int index, string? name) { Index = index; NewName = name; }
    }

    /// <summary>
    /// A Harmony instance. <see cref="PatchAll()"/> finds the patch classes as HarmonyX does (classes with any Harmony
    /// attribute; <see cref="PatchAll(Type)"/> takes the class even without one) and records each with its merged target
    /// (method type Normal when unset) in <see cref="Patches"/>. Nothing is patched and no target is resolved or checked, so
    /// a test that only calls PatchAll proves nothing about the patch: call its prefix or postfix yourself.
    /// </summary>
    public partial class Harmony : IDisposable
    {
        private static readonly Dictionary<string, List<(Type PatchClass, HarmonyMethod Target)>> s_patches = new();
        public string Id { get; }
        public Harmony(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id cannot be null or empty");
            Id = id;
        }
        /// <summary>The patch classes this id has "applied", with the target their attributes name.</summary>
        [TestOnly] public IReadOnlyList<(Type PatchClass, HarmonyMethod Target)> Patches => s_patches.TryGetValue(Id, out var list) ? list : new List<(Type PatchClass, HarmonyMethod Target)>();
        /// <summary>Records the calling assembly's patch classes. Patches nothing and checks no target: <see cref="HasAnyPatches"/> turns true even when the target method does not exist in the game.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void PatchAll() => PatchAll(Assembly.GetCallingAssembly());
        public void PatchAll(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
            foreach (var type in types) Record(type, allowUnannotated: false);
        }
        public void PatchAll(Type type) => Record(type, allowUnannotated: true);
        public static Harmony CreateAndPatchAll(Type type, string? harmonyInstanceId = null) { var h = new Harmony(harmonyInstanceId ?? $"harmony-auto-{Guid.NewGuid()}"); h.PatchAll(type); return h; }
        public static Harmony CreateAndPatchAll(Assembly assembly, string? harmonyInstanceId = null) { var h = new Harmony(harmonyInstanceId ?? $"harmony-auto-{Guid.NewGuid()}"); h.PatchAll(assembly); return h; }
        private void Record(Type type, bool allowUnannotated)
        {
            var infos = HarmonyMethodExtensions.GetFromType(type);
            if (!allowUnannotated && infos.Count == 0) return;
            var target = HarmonyMethod.Merge(infos);
            target.methodType ??= MethodType.Normal;
            if (!s_patches.TryGetValue(Id, out var list)) s_patches[Id] = list = new();
            if (!list.Any(p => p.PatchClass == type)) list.Add((type, target));
        }
        public void UnpatchSelf() => UnpatchID(Id);
        public static void UnpatchID(string harmonyID)
        {
            if (string.IsNullOrEmpty(harmonyID)) throw new ArgumentNullException(nameof(harmonyID), "UnpatchID was called with a null or empty harmonyID.");
            s_patches.Remove(harmonyID);
        }
        public static void UnpatchAll() => s_patches.Clear();
        [Obsolete("Use UnpatchSelf() to unpatch the current instance. The functionality to unpatch either other ids or EVERYTHING has been moved the static methods UnpatchID() and UnpatchAll() respectively", true)]
        public void UnpatchAll(string? harmonyID = null) { if (harmonyID == null) s_patches.Clear(); else if (harmonyID.Length > 0) s_patches.Remove(harmonyID); }
        /// <summary>Whether this id recorded a patch class. Recorded, not applied: true says nothing about whether the target exists in the game.</summary>
        public static bool HasAnyPatches(string harmonyID) => s_patches.TryGetValue(harmonyID, out var list) && list.Count > 0;
        void IDisposable.Dispose() => UnpatchSelf();
    }

    /// <summary>
    /// HarmonyX's reflection helpers: members of any visibility, looked up on the type and then on each base type. A missing
    /// member (or a null type or name) gives null. <see cref="Method(Type, string, Type[], Type[])"/> without parameter types
    /// takes the one method of that name; with overloads it falls back to the overload without parameters, and throws
    /// <see cref="AmbiguousMatchException"/> when there is none.
    /// </summary>
    public static partial class AccessTools
    {
        public static readonly BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.GetField | BindingFlags.SetField | BindingFlags.GetProperty | BindingFlags.SetProperty;
        public static readonly BindingFlags allDeclared = all | BindingFlags.DeclaredOnly;

        public static T? FindIncludingBaseTypes<T>(Type type, Func<Type, T?> func) where T : class
        {
            for (Type? t = type; t != null; t = t.BaseType) if (func(t) is { } found) return found;
            return null;
        }
        public static FieldInfo? DeclaredField(Type type, string name) => type == null || name == null ? null : type.GetField(name, allDeclared);
        public static FieldInfo? Field(Type type, string name) => type == null || name == null ? null : FindIncludingBaseTypes(type, t => t.GetField(name, all));
        public static PropertyInfo? DeclaredProperty(Type type, string name) => type == null || name == null ? null : type.GetProperty(name, allDeclared);
        public static PropertyInfo? Property(Type type, string name) => type == null || name == null ? null : FindIncludingBaseTypes(type, t => t.GetProperty(name, all));
        public static MethodInfo? DeclaredMethod(Type type, string name, Type[]? parameters = null, Type[]? generics = null)
        {
            if (type == null || name == null) return null;
            var found = parameters == null ? type.GetMethod(name, allDeclared) : type.GetMethod(name, allDeclared, null, parameters, new ParameterModifier[0]);
            return found != null && generics != null ? found.MakeGenericMethod(generics) : found;
        }
        public static MethodInfo? Method(Type type, string name, Type[]? parameters = null, Type[]? generics = null)
        {
            if (type == null || name == null) return null;
            MethodInfo? found;
            if (parameters != null) found = FindIncludingBaseTypes(type, t => t.GetMethod(name, all, null, parameters, new ParameterModifier[0]));
            else
            {
                try { found = FindIncludingBaseTypes(type, t => t.GetMethod(name, all)); }
                catch (AmbiguousMatchException error)
                {
                    found = FindIncludingBaseTypes(type, t => t.GetMethod(name, all, null, Type.EmptyTypes, new ParameterModifier[0]))
                        ?? throw new AmbiguousMatchException($"Ambiguous match in Harmony patch for {type}:{name}", error);
                }
            }
            return found != null && generics != null ? found.MakeGenericMethod(generics) : found;
        }
        public static Type? Inner(Type type, string name) => type == null || name == null ? null : FindIncludingBaseTypes(type, t => t.GetNestedType(name, all));
        /// <summary>The argument types for a call: an argument's own type, object for a null.</summary>
        public static Type[] GetTypes(object?[]? parameters) => parameters == null ? new Type[0] : parameters.Select(p => p?.GetType() ?? typeof(object)).ToArray();
        public static List<string> GetFieldNames(Type type) => type == null ? new List<string>() : type.GetFields(allDeclared).Select(f => f.Name).ToList();
        public static List<string> GetPropertyNames(Type type) => type == null ? new List<string>() : type.GetProperties(allDeclared).Select(p => p.Name).ToList();
        public static List<string> GetMethodNames(Type type) => type == null ? new List<string>() : type.GetMethods(allDeclared).Select(m => m.Name).ToList();
        /// <summary>A type by name as <see cref="Type.GetType(string)"/> finds it, else by full name, else by simple name, from every loaded assembly.</summary>
        public static Type? TypeByName(string name)
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.FullName!.StartsWith("Microsoft.VisualStudio"))
                .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToArray(); } });
            return Type.GetType(name, false) ?? types.FirstOrDefault(t => t.FullName == name) ?? types.FirstOrDefault(t => t.Name == name);
        }
    }

    /// <summary>
    /// HarmonyX's Traverse: walks fields, properties, methods and nested types of any visibility by name. A missing member
    /// gives an empty traverse whose value is null (it does not throw); a typed read casts, so a value of another type
    /// throws <see cref="InvalidCastException"/>; setting the value of a method throws.
    /// </summary>
    public partial class Traverse
    {
        private readonly Type? m_type;
        private readonly object? m_root;
        private readonly MemberInfo? m_info;
        private readonly MethodBase? m_method;
        private readonly object?[]? m_params;

        private Traverse() { }
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

        /// <summary>The member's value, the method's result (called with the arguments given to <see cref="Method(string, object[])"/>), or the root (the type when there is no root).</summary>
        public object? GetValue() => m_info switch
        {
            FieldInfo field => field.GetValue(m_root),
            PropertyInfo property => property.GetValue(m_root, AccessTools.all, null, m_params, System.Globalization.CultureInfo.CurrentCulture),
            _ => m_method != null ? m_method.Invoke(m_root, m_params) : (object?)m_root ?? m_type,
        };
        public T GetValue<T>() => GetValue() is { } value ? (T)value : default!;
        /// <summary>Calls the method with these arguments instead of the ones given to <see cref="Method(string, object[])"/>.</summary>
        public object? GetValue(params object?[] arguments) => (m_method ?? throw new Exception("cannot get method value without method")).Invoke(m_root, arguments);
        public T GetValue<T>(params object?[] arguments) => (T)GetValue(arguments)!;
        public Type? GetValueType() => (m_info as FieldInfo)?.FieldType ?? (m_info as PropertyInfo)?.PropertyType;

        public Traverse SetValue(object? value)
        {
            if (m_info is FieldInfo field) field.SetValue(m_root, value, AccessTools.all, null, System.Globalization.CultureInfo.CurrentCulture);
            if (m_info is PropertyInfo property) property.SetValue(m_root, value, AccessTools.all, null, m_params, System.Globalization.CultureInfo.CurrentCulture);
            if (m_method != null) throw new Exception($"cannot set value of method {m_method.DeclaringType?.FullName}::{m_method.Name}");
            return this;
        }

        // Where the next step looks: into this traverse's value, or at the type itself for a type traverse without a
        // static member selected.
        private Traverse Resolve()
        {
            bool isStatic = m_info is FieldInfo { IsStatic: true } || (m_info is PropertyInfo p && p.GetGetMethod(true)?.IsStatic == true) || m_method is { IsStatic: true };
            if (m_root == null && !isStatic && m_type != null) return this;
            return new Traverse(GetValue());
        }

        public Traverse Type(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            return m_type != null && AccessTools.Inner(m_type, name) is { } type ? new Traverse(type) : new Traverse();
        }
        public Traverse Field(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var at = Resolve();
            if (at.m_type == null) return new Traverse();
            var field = AccessTools.Field(at.m_type, name);
            if (field == null || (!field.IsStatic && at.m_root == null)) return new Traverse();
            return new Traverse(at.m_root, field, null);
        }
        public Traverse<T> Field<T>(string name) => new(Field(name));
        public List<string> Fields() => AccessTools.GetFieldNames(Resolve().m_type!);
        public Traverse Property(string name, object?[]? index = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var at = Resolve();
            return at.m_type != null && AccessTools.Property(at.m_type, name) is { } property ? new Traverse(at.m_root, property, index) : new Traverse();
        }
        public Traverse<T> Property<T>(string name, object?[]? index = null) => new(Property(name, index));
        public List<string> Properties() => AccessTools.GetPropertyNames(Resolve().m_type!);
        /// <summary>A method found by its name and the types of <paramref name="arguments"/>; <see cref="GetValue()"/> calls it with them.</summary>
        public Traverse Method(string name, params object?[] arguments)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var at = Resolve();
            return at.m_type != null && AccessTools.Method(at.m_type, name, AccessTools.GetTypes(arguments)) is { } method ? new Traverse(at.m_root, method, arguments) : new Traverse();
        }
        public Traverse Method(string name, Type[] paramTypes, object?[]? arguments = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var at = Resolve();
            return at.m_type != null && AccessTools.Method(at.m_type, name, paramTypes) is { } method ? new Traverse(at.m_root, method, arguments) : new Traverse();
        }
        public List<string> Methods() => AccessTools.GetMethodNames(Resolve().m_type!);

        public bool FieldExists() => m_info is FieldInfo;
        public bool PropertyExists() => m_info is PropertyInfo;
        public bool MethodExists() => m_method != null;
        public bool TypeExists() => m_type != null;
        public override string? ToString() => (m_method ?? GetValue())?.ToString();
    }

    /// <summary>A typed view of a traverse's field or property; reading a value of another type throws, as in HarmonyX.</summary>
    public partial class Traverse<T>
    {
        private readonly Traverse m_traverse;
        public Traverse(Traverse traverse) { m_traverse = traverse; }
        public T Value { get => m_traverse.GetValue<T>(); set => m_traverse.SetValue(value); }
    }
}
