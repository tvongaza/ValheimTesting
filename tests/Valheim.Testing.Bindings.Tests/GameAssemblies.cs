using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// A miniature "assembly_valheim" in three builds, compiled at test time, and two mods built against the first:
/// <list type="bullet">
/// <item>v1: what the mods were built against, publicized (every member public).</item>
/// <item>v1 real: the same game unpublicized: <c>Hidden</c> and <c>m_secret</c> are private.</item>
/// <item>v2: an update that removes, renames, retypes and changes members, makes some private or protected, moves
/// one field to the base class and forwards <c>Helper</c> to assembly_utils.</item>
/// </list>
/// <c>MyMod</c> does not declare IgnoresAccessChecksTo; <c>MyModDeclared</c> is the same code and declares it.
/// </summary>
public sealed class GameAssemblies : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vt-bindings-" + Guid.NewGuid().ToString("N"));
    public string V1 => Path.Combine(Root, "v1");
    public string V1Real => Path.Combine(Root, "v1-real");
    public string V2 => Path.Combine(Root, "v2");
    public string UtilsOnly => Path.Combine(Root, "utils-only");
    public string Mod => Path.Combine(Root, "mods", "MyMod.dll");
    public string ModDeclared => Path.Combine(Root, "mods", "MyModDeclared.dll");

    public GameAssemblies()
    {
        byte[] v1 = Compile("assembly_valheim", Game(v2: false, publicized: true));
        byte[] utils = Compile("assembly_utils", "public static class Helper { public static int Value() => 2; }");
        Write(V1, "assembly_valheim.dll", v1);
        Write(V1Real, "assembly_valheim.dll", Compile("assembly_valheim", Game(v2: false, publicized: false)));
        Write(V2, "assembly_utils.dll", utils);
        Write(V2, "assembly_valheim.dll", Compile("assembly_valheim", Game(v2: true, publicized: false), utils));
        Write(UtilsOnly, "assembly_utils.dll", utils);
        Write(Path.GetDirectoryName(Mod)!, "MyMod.dll", Compile("MyMod", ModSource, v1));
        Write(Path.GetDirectoryName(Mod)!, "MyModDeclared.dll", Compile("MyModDeclared", DeclaresIgnoresAccessChecks + ModSource, v1));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static string Game(bool v2, bool publicized)
    {
        string secret = v2 || !publicized ? "private" : "public";
        string Pick(string before, string after) => v2 ? after : before;
        return $$"""
            using System.Collections.Generic;
            {{Pick("", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Helper))]")}}

            public class Character
            {
                public float m_health;
                {{Pick("", "public int m_moved;")}}
            }

            public class Terminal : Character
            {
                {{Pick("public string m_input;", "")}}
                {{Pick("public int m_moved;", "")}}
                {{Pick("public float m_speed;", "public double m_speed;")}}
                public int m_kept;
                public int Health { get; set; }
                {{Pick("public int Level { get; set; }", "public int Level;")}}
                public void {{Pick("InputText", "InputTextRenamed")}}() { }
                public void Overloaded(int value) { }
                {{Pick("public void Overloaded(string value) { }", "")}}
                public void ByRef(ref int a, out string b, int[] c, int[,] d) { b = ""; }
                public void Shape({{Pick("int[]", "int[,]")}} values) { }
                public void Pass({{Pick("ref int", "int")}} value) { }
                public void Collect(List<{{Pick("string", "int")}}> items) { }
                public static T Get<T>(T value) => value;
                {{secret}} void Hidden() { }
                {{secret}} int m_secret;
                {{Pick("public", "protected")}} void Guarded() { }
                public class Nested { public int m_value; }
                {{Pick("public class GoneNested { }", "")}}
            }

            public class Inventory<T>
            {
                public T m_item;
                public void Add(T item) { }
                public List<T> All() => new List<T>();
            }

            public struct Coord { public int x; }

            {{Pick("public class Removed { }", "")}}
            {{Pick("public static class Helper { public static int Value() => 1; }", "")}}
            """;
    }

    private const string DeclaresIgnoresAccessChecks = """
        [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("assembly_valheim")]
        namespace System.Runtime.CompilerServices
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            internal sealed class IgnoresAccessChecksToAttribute : System.Attribute
            {
                public IgnoresAccessChecksToAttribute(string assemblyName) { AssemblyName = assemblyName; }
                public string AssemblyName { get; }
            }
        }

        """;

    // No using directives: the declared variant puts an assembly attribute and a namespace in front of this text.
    private const string ModSource = """
        namespace MyMod
        {
            public static class Patches
            {
                public static void UseInput(Terminal t) { t.m_input = "x"; }
                public static float UseSpeed(Terminal t) => t.m_speed;
                public static void UseRename(Terminal t) { t.InputText(); }
                public static int UseLevel(Terminal t) => t.Level;
                public static int UseKept(Terminal t) => t.m_kept + t.Health + t.m_moved + (int)t.m_health;
                public static void UseOverloads(Terminal t) { t.Overloaded(1); t.Overloaded("s"); }
                public static void UseSignatures(Terminal t)
                {
                    int a = 0;
                    t.ByRef(ref a, out string b, new int[1], new int[1, 1]);
                    t.Shape(new int[1]);
                    t.Pass(ref a);
                    t.Collect(new System.Collections.Generic.List<string>());
                }
                public static int UseGenerics(Inventory<string> items)
                {
                    items.Add("x");
                    return Terminal.Get<int>(1) + new Inventory<Coord>().m_item.x + items.All().Count;
                }
                public static int UseNested(Terminal.Nested n) => n.m_value;
                public static object UseGoneNested() => new Terminal.GoneNested();
                public static object UseRemoved() => new Removed();
                public static int UseForwarded() => Helper.Value();
                public static void UseHidden(Terminal t) { t.Hidden(); t.m_secret = 1; }
                public static void UseGuarded(Terminal t) { t.Guarded(); }
            }

            public class MyTerminal : Terminal
            {
                public void CallGuarded() { Guarded(); }
            }
        }
        """;

    // Only the core library: its types are defined there, so the mods reference it, and it is never supplied.
    private static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    private static byte[] Compile(string name, string source, params byte[][] references)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source) },
            Framework.Concat(references.Select(r => MetadataReference.CreateFromImage(r))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        if (!result.Success)
            throw new InvalidOperationException(name + " did not compile:" + Environment.NewLine
                + string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return image.ToArray();
    }

    private static void Write(string directory, string file, byte[] image)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, file), image);
    }
}
