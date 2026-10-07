// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// The game's soft asset references (SoftReferenceableAssets.dll): what ZoneSystem.ZoneLocation.m_prefab holds since
// Valheim 0.217, a handle a location loads its template through and gives back.
using Valheim.Testing.Doubles;

namespace SoftReferenceableAssets
{
    /// <summary>The outcome of <see cref="SoftReference{T}.Load"/>, as the game's.</summary>
    public enum LoadResult { Succeeded, Failed, Aborted }

    /// <summary>
    /// A handle to an asset that is loaded on demand, as the game's struct: <see cref="Name"/> is the asset's file name,
    /// <see cref="Asset"/> the loaded object, and <see cref="Load"/>/<see cref="Release"/> take and give back a reference.
    /// A test makes one with the <see cref="SoftReference{T}(string, T)"/> constructor, which the game does not have (it
    /// resolves an asset id through its bundle loader); give it the template when the code under test reads
    /// <see cref="Asset"/>. Copies of one handle share its reference count, as the game's copies share their asset's count
    /// in the loader. The default handle stands for none: not valid, a null name, and Load fails.
    /// </summary>
    public partial struct SoftReference<T> : System.IEquatable<SoftReference<T>> where T : UnityEngine.Object
    {
        private readonly Entry? m_entry;

        [TestOnly] public SoftReference(string name, T? asset = null) => m_entry = new Entry(name, asset);

        /// <summary>The asset, or null when the handle has none; the game loads it on first read.</summary>
        public T? Asset => m_entry?.Asset;
        public bool IsValid => m_entry != null;
        public bool IsLoading => false;
        /// <summary>Loaded: a reference is held.</summary>
        public bool IsLoaded => References > 0;
        public string? Name => m_entry?.Name;
        /// <summary>References taken with <see cref="Load"/> or <see cref="HoldReference"/> and not released.</summary>
        [TestOnly] public int References => m_entry?.References ?? 0;

        /// <summary>
        /// Takes a reference, as the game's loader does for a valid handle (a name-only test handle loads too; its
        /// <see cref="Asset"/> stays null). The default handle fails.
        /// </summary>
        public LoadResult Load()
        {
            if (m_entry == null) return LoadResult.Failed;
            m_entry.References++;
            return LoadResult.Succeeded;
        }
        public void HoldReference() { if (m_entry != null) m_entry.References++; }
        public void Release() { if (m_entry is { References: > 0 }) m_entry.References--; }

        public bool Equals(SoftReference<T> other) => ReferenceEquals(m_entry, other.m_entry);
        public override bool Equals(object? obj) => obj is SoftReference<T> other && Equals(other);
        public override int GetHashCode() => m_entry?.GetHashCode() ?? 0;
        public static bool operator ==(SoftReference<T> left, SoftReference<T> right) => left.Equals(right);
        public static bool operator !=(SoftReference<T> left, SoftReference<T> right) => !left.Equals(right);
        public override string ToString() => m_entry?.Name ?? "[null]";

        private sealed class Entry
        {
            public readonly string Name;
            public readonly T? Asset;
            public int References;
            public Entry(string name, T? asset) { Name = name; Asset = asset; }
        }
    }
}
