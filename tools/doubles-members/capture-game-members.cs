// Records, for every line of docs/packages/Valheim.Testing.Doubles.members.txt, whether the game has that type or member,
// into tests/Valheim.Testing.Doubles.Tests/GameBytes/game-members.txt (issue #305). It reads the pinned game, BepInEx and
// Jotunn assemblies as metadata: nothing is loaded or run, and no game process starts. Run it after the index changes
// (MemberIndexTests rewrites the index) or after a game, BepInEx or Jotunn update, then commit both files:
//
//   dotnet run --no-cache tools/doubles-members/capture-game-members.cs -- <Valheim>/valheim_Data/Managed <BepInEx>/core <Jotunn.dll>
//
// MemberIndexTests then refuses a doubled member the game lacks unless it carries [TestOnly], and a [TestOnly] one it has.
#:package System.Reflection.MetadataLoadContext@10.0.0
#:property PublishAot=false
#:project ../../tests/Valheim.Testing.Doubles.Tests/Valheim.Testing.Doubles.Tests.csproj
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

if (args.Length != 3) { Console.Error.WriteLine("usage: capture-game-members.cs <game Managed folder> <BepInEx core folder> <Jotunn.dll>"); return 2; }
string root = Root();
string managed = Path.GetFullPath(args[0]), core = Path.GetFullPath(args[1]), jotunn = Path.GetFullPath(args[2]);
string game = Path.Combine(managed, "assembly_valheim.dll");
foreach (string required in new[] { game, Path.Combine(core, "BepInEx.dll"), Path.Combine(core, "0Harmony.dll"), jotunn })
    if (!File.Exists(required)) { Console.Error.WriteLine("missing " + required); return 2; }

// Types come from the game's folder (first, so a name the game and a loader both define is the game's), BepInEx, the
// HarmonyX that BepInEx loads (not its 0Harmony20 shim) and Jotunn; the loader's other assemblies only resolve references.
var sources = Directory.GetFiles(managed, "*.dll").OrderBy(p => p, StringComparer.Ordinal)
    .Concat(new[] { Path.Combine(core, "BepInEx.dll"), Path.Combine(core, "0Harmony.dll"), jotunn }).ToList();
var paths = sources.Concat(Directory.GetFiles(core, "*.dll"))
    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
var types = new Dictionary<string, Type>(StringComparer.Ordinal);
int unreadable = 0;
foreach (string path in sources)
{
    Type[] defined;
    try { defined = context.LoadFromAssemblyPath(path).GetTypes(); }
    catch (ReflectionTypeLoadException partial) { defined = partial.Types.Where(t => t != null).ToArray()!; }
    catch (BadImageFormatException) { continue; }
    foreach (var type in defined)
    {
        string name;
        try { if (!MemberIndex.IsDoubledNamespace(type.Namespace)) continue; name = MemberIndex.TypeName(type); }
        catch (FileNotFoundException) { continue; }
        types.TryAdd(name, type);
    }
}

// Every member of the game type and its bases, private ones too: a mod built against the publicized game calls those.
var memberCache = new Dictionary<Type, List<(string Kind, string Signature)>>();
List<(string Kind, string Signature)> GameMembers(Type type)
{
    if (memberCache.TryGetValue(type, out var known)) return known;
    var list = new List<(string, string)>();
    for (var t = type; t != null && t.FullName != "System.Object"; t = t.BaseType)
        foreach (var m in MemberIndex.Members(t, visibleOnly: false))
        {
            if (t != type && m is ConstructorInfo) continue;
            try { list.Add((Group(MemberIndex.Kind(m)), MemberIndex.Signature(m, anySetter: true))); } catch (FileNotFoundException) { unreadable++; }
        }
    return memberCache[type] = list;
}
static string Group(string kind) => kind is "field" or "property" ? "value" : kind;
static string Bare(string signature) => signature.EndsWith(MemberIndex.ReadOnly, StringComparison.Ordinal) ? signature[..^MemberIndex.ReadOnly.Length] : signature;
// A static class and a sealed class of statics (Unity's Time, Debug) read the same to mod code.
static string KindWord(string description) => description.Split(" : ")[0].Replace("static class", "class");

string indexPath = Path.Combine(root, "docs/packages/Valheim.Testing.Doubles.members.txt");
var output = new List<string>
{
    "# The game's verdict on each line of docs/packages/Valheim.Testing.Doubles.members.txt: game (the game has this signature,",
    "# or a read-only double of a writable member), differs (the game has the name with the signatures in the last column,",
    "# including a writable double of a member the game has \"{ get; }\") or absent. Written by",
    "# tools/doubles-members/capture-game-members.cs from metadata; MemberIndexTests reads it. Inputs:",
    "# game assembly_valheim.dll sha256 " + Sha256(game) + " (" + Path.GetFileName(Path.GetDirectoryName(managed)) + ")",
    "# game version " + GameVersion(game),
    "# game network version " + NetworkVersion(context.LoadFromAssemblyPath(game)),
    "# loader BepInEx.dll " + AssemblyName.GetAssemblyName(Path.Combine(core, "BepInEx.dll")).Version + ", 0Harmony.dll " + AssemblyName.GetAssemblyName(Path.Combine(core, "0Harmony.dll")).Version,
    "# jotunn Jotunn.dll " + AssemblyName.GetAssemblyName(jotunn).Version,
};
int counted = 0;
foreach (string line in File.ReadAllLines(indexPath))
{
    if (line.StartsWith('#') || line.Length == 0) continue;
    string[] f = line.Split('\t');
    string typeName = f[0], kind = f[1], signature = f[2];
    string verdict, detail = "";
    types.TryGetValue(typeName, out var gameType);
    if (gameType == null) verdict = "absent";
    else if (kind == "type")
    {
        string theirs = MemberIndex.Describe(gameType);
        verdict = KindWord(theirs) == KindWord(signature) ? "game" : "differs";
        if (verdict == "differs") detail = theirs;
    }
    else
    {
        var members = GameMembers(gameType);
        string bare = Bare(signature);
        if (members.Contains((Group(kind), signature))) verdict = "game";
        // Writability (#325): a read-only double of a writable game member is narrower, and code that assigns it already
        // fails to compile against the doubles, so it reads "game"; a writable double of a read-only member differs.
        else if (signature.EndsWith(MemberIndex.ReadOnly, StringComparison.Ordinal) && members.Contains((Group(kind), bare))) verdict = "game";
        else if (Group(kind) == "value" && members.Contains((Group(kind), bare + MemberIndex.ReadOnly)))
        {
            verdict = "differs";
            detail = bare + MemberIndex.ReadOnly;
        }
        else
        {
            string name = MemberIndex.NameOf(signature);
            var same = members.Where(m => MemberIndex.NameOf(m.Signature) == name).Select(m => m.Signature).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            verdict = same.Count == 0 ? "absent" : "differs";
            detail = string.Join(" | ", same);
        }
    }
    output.Add(typeName + "\t" + kind + "\t" + signature + "\t" + verdict + (detail.Length > 0 ? "\t" + detail : ""));
    counted++;
}
string capturePath = Path.Combine(root, "tests/Valheim.Testing.Doubles.Tests/GameBytes/game-members.txt");
File.WriteAllText(capturePath, string.Join("\n", output) + "\n");
if (unreadable > 0) Console.Error.WriteLine($"warning: {unreadable} game members name a type in an assembly that is not in the inputs and were skipped");
Console.WriteLine($"{counted} index lines: {output.Count(l => l.EndsWith("\tgame"))} game, {output.Count(l => l.Contains("\tdiffers"))} differ, {output.Count(l => l.EndsWith("\tabsent"))} absent -> {capturePath}");
return 0;

// The game's version string as Version.CurrentVersion builds it: the three int constants its static constructor passes to
// the GameVersion that it stores in CurrentVersion's backing field. Read from the IL, so no game code runs.
static string GameVersion(string assemblyPath)
{
    using var stream = File.OpenRead(assemblyPath);
    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
    var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
    foreach (var typeHandle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(typeHandle);
        if (md.GetString(type.Name) != "Version" || md.GetString(type.Namespace).Length != 0) continue;
        foreach (var methodHandle in type.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            if (md.GetString(method.Name) != ".cctor") continue;
            var il = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe, method.RelativeVirtualAddress).GetILBytes()!;
            var ints = new List<int>();
            for (int i = 0; i < il.Length;)
            {
                byte op = il[i++];
                if (op >= 0x16 && op <= 0x1E) ints.Add(op - 0x16);                                   // ldc.i4.0 .. ldc.i4.8
                else if (op == 0x15) ints.Add(-1);                                                    // ldc.i4.m1
                else if (op == 0x1F) ints.Add((sbyte)il[i++]);                                       // ldc.i4.s
                else if (op == 0x20) { ints.Add(BitConverter.ToInt32(il, i)); i += 4; }              // ldc.i4
                else if (op == 0x73) i += 4;                                                          // newobj: keep the arguments
                else if (op is 0x72 or 0x28 or 0x7E or 0x8D or 0xD0 or 0x7D or 0x7B) { i += 4; ints.Clear(); } // ldstr call ldsfld newarr ldtoken stfld ldfld
                else if (op is 0x00 or 0x25 or 0x26 or 0x2A || (op >= 0x9B && op <= 0xA2)) ints.Clear();       // nop dup pop ret stelem.*
                else if (op == 0x80)                                                                  // stsfld
                {
                    int token = BitConverter.ToInt32(il, i); i += 4;
                    var field = md.GetFieldDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.FieldDefinitionHandle(token & 0xFFFFFF));
                    if (md.GetString(field.Name) == "<CurrentVersion>k__BackingField" && ints.Count >= 3)
                        return string.Join(".", ints.Skip(ints.Count - 3));
                    ints.Clear();
                }
                else throw new InvalidOperationException($"Version's static constructor uses IL opcode 0x{op:X2}, which this reader does not know; read CurrentVersion another way.");
            }
        }
    }
    throw new InvalidOperationException("assembly_valheim has no Version.CurrentVersion set in its static constructor.");
}

static string NetworkVersion(Assembly game) =>
    game.GetType("Version")?.GetField("c_networkVersion") is { IsLiteral: true } field ? field.GetRawConstantValue()!.ToString()!
        : throw new InvalidOperationException("assembly_valheim's Version has no const c_networkVersion; read the network version another way.");

static string Sha256(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
