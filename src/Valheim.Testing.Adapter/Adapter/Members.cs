// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter. Plain .NET reflection
// only (no Unity or game types), so it also compiles into ordinary test projects.
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Exact reflected access to game members that the shipped game keeps private, even where publicized build references
    /// show them as public. Every lookup names one member (a method by its exact parameter types) and throws when it is
    /// missing or has another type, so a game update fails the test loudly instead of reading a default.
    /// </summary>
    public static class Members
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The value of <paramref name="instance"/>'s field <paramref name="name"/> (declared on its type or a base type).</summary>
        public static T Field<T>(object instance, string name)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return Read<T>(FindField(instance.GetType(), name, Instance), instance);
        }

        /// <summary>The value of <paramref name="type"/>'s static field <paramref name="name"/>.</summary>
        public static T StaticField<T>(Type type, string name) => Read<T>(FindField(type, name, Static), null);

        /// <summary>
        /// The method <paramref name="name"/> declared on <paramref name="type"/> or, nearest first, a base type (private ones
        /// included) with exactly these parameter types. Overloads never match by name alone, and a parameter the arguments
        /// would only widen to (a <c>short</c> for an <c>int</c>) does not match.
        /// </summary>
        public static MethodInfo Method(Type type, string name, params Type[] parameters)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            // Compared by hand rather than through a binder: the default binder accepts widening conversions, and a type's
            // own lookup does not return a base type's private methods.
            for (Type? current = type; current != null; current = current.BaseType)
                foreach (var method in current.GetMethods(Instance | Static | BindingFlags.DeclaredOnly))
                    if (method.Name == name && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)) return method;
            throw new MissingMethodException(type.FullName, name + "(" + string.Join(", ", parameters.Select(p => p.Name)) + ")");
        }

        /// <summary>Invokes <paramref name="method"/> and rethrows what it threw, not reflection's wrapper.</summary>
        public static object? Call(MethodInfo method, object? instance, params object?[] arguments)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            try { return method.Invoke(instance, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        /// <summary>
        /// A method's identity as the Harmony census reports it: <c>DeclaringType::Name(ParameterType,...)</c>, types as
        /// <see cref="Type.ToString"/> writes them (a nested type as <c>Outer+Inner</c>).
        /// </summary>
        public static string Describe(MethodBase method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return (method.DeclaringType?.ToString() ?? "<global>") + "::" + method.Name +
                "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.ToString())) + ")";
        }

        private static FieldInfo FindField(Type type, string name, BindingFlags flags)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            // GetField does not return a base type's private field, so walk the hierarchy.
            for (Type? current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }

        private static T Read<T>(FieldInfo field, object? instance)
        {
            object? value = field.GetValue(instance);
            if (value is T typed) return typed;
            // A null reference (or empty Nullable) is a value, as long as the field could hold a T.
            if (value == null && default(T) == null && (typeof(T).IsAssignableFrom(field.FieldType) || field.FieldType.IsAssignableFrom(typeof(T))))
                return default!;
            throw new InvalidCastException(field.DeclaringType?.FullName + "." + field.Name + " is " + field.FieldType + ", not " + typeof(T) + ".");
        }
    }
}
