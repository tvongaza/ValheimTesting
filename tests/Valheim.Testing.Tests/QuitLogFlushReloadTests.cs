using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

// The adapter's QuitLogFlush across a ScriptEngine reload: the same source loaded twice (fresh statics), with one
// process-wide BepInEx listener list. Compiled here against a working stub of the BepInEx and Unity members it uses.
public class QuitLogFlushReloadTests
{
    private const string StubSource = """
        namespace BepInEx.Logging
        {
            public interface ILogListener : System.IDisposable { void LogEvent(object sender, LogEventArgs eventArgs); }
            public class LogEventArgs : System.EventArgs { }
            public class DiskLogListener : ILogListener
            {
                public int Flushes;
                public System.IO.TextWriter LogWriter { get; protected set; }
                public DiskLogListener() { LogWriter = new Counting(this); }
                public void LogEvent(object sender, LogEventArgs eventArgs) { }
                public void Dispose() { }
                private sealed class Counting : System.IO.StringWriter { private readonly DiskLogListener _o; public Counting(DiskLogListener o) => _o = o; public override void Flush() => _o.Flushes++; }
            }
            public class ManualLogSource { public void LogInfo(object data) { } }
            public static class Logger
            {
                public static readonly System.Collections.Generic.List<ILogListener> All = new();
                public static System.Collections.Generic.ICollection<ILogListener> Listeners => All;
                public static ManualLogSource CreateLogSource(string sourceName) => new();
                public static void Log() { foreach (var l in All.ToArray()) l.LogEvent(null, new LogEventArgs()); }
            }
        }
        namespace UnityEngine
        {
            public sealed class Application { public static event System.Action quitting; public static void Quit() => quitting?.Invoke(); }
        }
        """;

    [Fact] public void ReloadsAndSeveralAdaptersShareOneListenerAndTheQuitKeepsIt()
    {
        string[] keys = ["Valheim.Testing.Adapter.QuitLogFlush.ArmedBy", "Valheim.Testing.Adapter.QuitLogFlush.Users"];
        foreach (string key in keys) AppDomain.CurrentDomain.SetData(key, null); // process-wide, like the game's
        try
        {
            var context = new Reload();
            Type logger = context.Stub.GetType("BepInEx.Logging.Logger")!;
            var listeners = (System.Collections.IList)logger.GetField("All")!.GetValue(null)!;
            int Flushers() => listeners.Cast<object>().Count(l => l.GetType().Name == "FlushListener");

            // A reload with Disable in OnDestroy: the listener goes with the old load and comes back with the new one.
            Type first = context.Adapter("MyMod.TestAdapter.1"), second = context.Adapter("MyMod.TestAdapter.2");
            Call(first, "Enable"); Call(first, "Enable");
            Assert.Equal(1, Flushers());
            Call(first, "Disable");
            Assert.Equal(0, Flushers());
            Assert.False((bool)first.GetProperty("Enabled")!.GetValue(null)!);
            Call(second, "Enable");
            Assert.Equal(1, Flushers());

            // A reload without Disable (fresh statics): still one listener, and the old load counts as enabled.
            Type third = context.Adapter("MyMod.TestAdapter.3");
            Call(third, "Enable");
            Assert.Equal(1, Flushers());
            Call(third, "Disable");
            Assert.Equal(1, Flushers()); // the second load never disabled, so its flush stays

            // Another mod's adapter compiled from the same source: disabling one leaves the other's flush.
            Type other = context.Adapter("Roads.TestAdapter");
            Call(other, "Enable"); Call(other, "Disable");
            Assert.Equal(1, Flushers());
            Assert.True((bool)second.GetProperty("Enabled")!.GetValue(null)!);

            // Any load's quit signal arms the flush for all; Disable at quit (OnDestroy) keeps it.
            Call(other, "Enable");
            object disk = Activator.CreateInstance(context.Stub.GetType("BepInEx.Logging.DiskLogListener")!)!;
            listeners.Insert(0, disk);
            other.GetMethod("Quitting")!.Invoke(null, ["Roads.TestAdapter OnApplicationQuit"]);
            Assert.Equal("Roads.TestAdapter OnApplicationQuit", second.GetProperty("ArmedBy")!.GetValue(null));
            Call(other, "Disable"); Call(second, "Disable");
            int Flushes() => (int)disk.GetType().GetField("Flushes")!.GetValue(disk)!;
            int before = Flushes();
            logger.GetMethod("Log")!.Invoke(null, null);
            Assert.Equal(before + 1, Flushes());
            Assert.Equal(1, Flushers());
        }
        finally
        {
            foreach (string key in keys) AppDomain.CurrentDomain.SetData(key, null);
        }
    }

    private static void Call(Type type, string method) => type.GetMethod(method)!.Invoke(null, null);

    private sealed class Reload : AssemblyLoadContext
    {
        private static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
        private readonly byte[] _stub = Compile("BepInExStub", StubSource);
        public Assembly Stub { get; }

        public Reload() : base(isCollectible: true) => Stub = LoadFromStream(new MemoryStream(_stub));

        public Type Adapter(string name)
        {
            using var source = typeof(QuitLogFlushReloadTests).Assembly.GetManifestResourceStream("Adapter.QuitLogFlush.cs")!;
            string text = new StreamReader(source).ReadToEnd();
            return LoadFromStream(new MemoryStream(Compile(name, text, _stub))).GetType("Valheim.Testing.Adapter.QuitLogFlush")!;
        }

        protected override Assembly? Load(AssemblyName name) => name.Name == "BepInExStub" ? Stub : null;

        private static byte[] Compile(string name, string source, params byte[][] references)
        {
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
                Framework.Concat(references.Select(r => MetadataReference.CreateFromImage(r))),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var image = new MemoryStream();
            var result = compilation.Emit(image);
            if (!result.Success)
                throw new InvalidOperationException(name + " did not compile:" + Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return image.ToArray();
        }
    }
}
