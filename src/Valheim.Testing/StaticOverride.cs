using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Valheim.Testing;

/// <summary>
/// Sets static fields, static properties and environment variables for one test and puts back the values they had
/// before, not a default the test assumes:
/// <code>using var plain = StaticOverride.Set(() => MyMod.Meander, 0f).And(() => MyMod.Enabled, false).AndEnvironment("MYMOD_DEBUG", "1");</code>
/// Dispose restores in reverse order, restores every entry even when one restore throws, and does nothing the second
/// time. If a step of the chain throws (a readonly field, say), what the chain had already set is restored before the
/// error reaches the caller, since its using statement never ran. <see cref="Keep{T}"/> records a value without changing it, for statics the code under test changes itself.
/// Statics and environment variables are process-wide, so tests that override them must not run in parallel with
/// tests that read them.
/// </summary>
public sealed class StaticOverride : IDisposable
{
    private readonly List<(string Name, Action Restore)> _restores = new();
    private bool _disposed;

    private StaticOverride() { }

    /// <summary>Sets <paramref name="member"/> (<c>() => Type.Member</c>) to <paramref name="value"/> until disposed.</summary>
    public static StaticOverride Set<T>(Expression<Func<T>> member, T value) => new StaticOverride().And(member, value);
    /// <summary>Records <paramref name="member"/> and restores it on dispose, whatever the test or the code under test sets.</summary>
    public static StaticOverride Keep<T>(Expression<Func<T>> member) => new StaticOverride().AndKeep(member);
    /// <summary>Sets an environment variable (<see langword="null"/> removes it) until disposed.</summary>
    public static StaticOverride Environment(string name, string? value) => new StaticOverride().AndEnvironment(name, value);

    public StaticOverride And<T>(Expression<Func<T>> member, T value) => Step(() =>
    {
        var (name, get, set) = Access(member);
        Record(name, get, set);
        set(value);
    });

    public StaticOverride AndKeep<T>(Expression<Func<T>> member) => Step(() =>
    {
        var (name, get, set) = Access(member);
        Record(name, get, set);
    });

    public StaticOverride AndEnvironment(string name, string? value) => Step(() =>
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('=') >= 0) throw new ArgumentException("An environment variable name is required, without '='.", nameof(name));
        Record("environment " + name, () => System.Environment.GetEnvironmentVariable(name), v => System.Environment.SetEnvironmentVariable(name, v));
        System.Environment.SetEnvironmentVariable(name, value);
    });

    // A chain that throws part-way never reaches the caller's using, so nothing would dispose it: put back what the
    // chain has set so far before the error leaves.
    private StaticOverride Step(Action step)
    {
        try { step(); return this; }
        catch (Exception setup)
        {
            try { Dispose(); }
            catch (Exception restore) { throw new AggregateException("StaticOverride setup failed, and restoring what it had already set failed too.", setup, restore); }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        for (int i = _restores.Count - 1; i >= 0; i--)
        {
            try { _restores[i].Restore(); }
            catch (Exception error) { (errors ??= new()).Add(new InvalidOperationException($"Could not restore {_restores[i].Name}: {error.Message}", error)); }
        }
        if (errors != null) throw new AggregateException("StaticOverride could not restore every value.", errors);
    }

    private void Record<T>(string name, Func<T> get, Action<T> set)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaticOverride));
        T before = get();
        _restores.Add((name, () => set(before)));
    }

    private static (string Name, Func<T> Get, Action<T> Set) Access<T>(Expression<Func<T>> member)
    {
        if (member == null) throw new ArgumentNullException(nameof(member));
        if (member.Body is not MemberExpression { Expression: null } access)
            throw new ArgumentException("Pass a static field or property, as () => Type.Member.", nameof(member));
        string name = access.Member.DeclaringType?.Name + "." + access.Member.Name;
        switch (access.Member)
        {
            case FieldInfo field when field.IsLiteral || field.IsInitOnly:
                throw new ArgumentException($"{name} is a constant or readonly field and cannot be overridden.", nameof(member));
            case FieldInfo field:
                return (name, () => (T)field.GetValue(null)!, v => field.SetValue(null, v));
            case PropertyInfo property when property.GetSetMethod(nonPublic: true) is { } setter:
                var getter = property.GetGetMethod(nonPublic: true) ?? throw new ArgumentException($"{name} has no getter.", nameof(member));
                return (name, () => (T)getter.Invoke(null, null)!, v => setter.Invoke(null, new object?[] { v }));
            case PropertyInfo:
                throw new ArgumentException($"{name} has no setter and cannot be overridden.", nameof(member));
            default:
                throw new ArgumentException("Pass a static field or property, as () => Type.Member.", nameof(member));
        }
    }
}
