using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>Reads a game assembly's <see cref="GameBuild"/> from its metadata, without loading it or running game code.</summary>
internal static class GameAssemblyVersion
{
    private const string NetworkVersionField = "c_networkVersion", CurrentVersionField = "<CurrentVersion>k__BackingField";

    /// <summary>
    /// <c>Version.c_networkVersion</c> (a <c>const uint</c>) from the constant table, and, when found, the three ints
    /// <c>Version</c>'s static constructor passes to the <c>GameVersion</c> it stores in <c>CurrentVersion</c>. Never throws for a
    /// bad image: anything unreadable is an unknown build naming <paramref name="source"/>.
    /// </summary>
    internal static GameBuild Read(Stream image, string source)
    {
        try
        {
            using var pe = new PEReader(image, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) return new(null, null, $"{source} is not a .NET assembly");
            var reader = pe.GetMetadataReader();
            var version = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                .Where(type => reader.GetString(type.Name) == "Version" && type.Namespace.IsNil && type.GetDeclaringType().IsNil).ToList();
            if (version.Count != 1) return new(null, null, $"{source} declares no Version type");
            var constant = version[0].GetFields().Select(reader.GetFieldDefinition)
                .Where(field => reader.GetString(field.Name) == NetworkVersionField).ToList();
            if (constant.Count != 1 || (constant[0].Attributes & System.Reflection.FieldAttributes.Literal) == 0 || constant[0].GetDefaultValue().IsNil)
                return new(null, null, $"{source} has no const Version.{NetworkVersionField}");
            var value = reader.GetConstant(constant[0].GetDefaultValue());
            if (value.TypeCode != ConstantTypeCode.UInt32)
                return new(null, null, $"{source}'s Version.{NetworkVersionField} is {value.TypeCode}, not a uint");
            uint network = reader.GetBlobReader(value.Value).ReadUInt32();
            return new(network, CurrentVersion(pe, reader, version[0]), null);
        }
        catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            return new(null, null, $"{source} is not a readable .NET assembly ({error.Message})");
        }
    }

    /// <summary>
    /// The game version string, or null when the static constructor does not set it in the one shape the game uses: exactly three int
    /// constants, a <c>newobj</c>, then the <c>stsfld</c> into the backing field.
    /// </summary>
    private static string? CurrentVersion(PEReader pe, MetadataReader reader, TypeDefinition version)
    {
        foreach (var method in version.GetMethods().Select(reader.GetMethodDefinition))
        {
            if (reader.GetString(method.Name) != ".cctor" || method.RelativeVirtualAddress == 0) continue;
            byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
            var ints = new List<int>();
            int[]? constructed = null;
            for (int at = 0; at < il.Length;)
            {
                if (!OpCodeAt(il, ref at, out var code)) return null;
                int operand = OperandSize(code, il, at);
                if (operand < 0 || at + operand > il.Length) return null;
                if (Int32Constant(code, il, at) is { } number) { ints.Add(number); constructed = null; }
                else if (code == OpCodes.Newobj.Value) { constructed = ints.Count == 3 ? ints.ToArray() : null; ints.Clear(); }
                else if (code == OpCodes.Stsfld.Value)
                {
                    var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, at));
                    if (constructed != null && handle.Kind == HandleKind.FieldDefinition &&
                        reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name) == CurrentVersionField)
                        return string.Join('.', constructed);
                    ints.Clear(); constructed = null;
                }
                else { ints.Clear(); constructed = null; }
                at += operand;
            }
        }
        return null;
    }

    // The operand size of every IL opcode, from the runtime's own opcode table.
    private static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes).GetFields()
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => code.Value, code => code.OperandType);

    private static bool OpCodeAt(byte[] il, ref int at, out short code)
    {
        code = il[at] == 0xFE && at + 1 < il.Length ? (short)(0xFE00 | il[at + 1]) : il[at];
        at += code > 0xFF || code < 0 ? 2 : 1;
        return Operands.ContainsKey(code);
    }

    private static int OperandSize(short code, byte[] il, int at) => Operands[code] switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => at + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, at) : -1,
        _ => 4,
    };

    private static int? Int32Constant(short code, byte[] il, int at)
    {
        if (code >= OpCodes.Ldc_I4_M1.Value && code <= OpCodes.Ldc_I4_8.Value) return code - OpCodes.Ldc_I4_0.Value;
        if (code == OpCodes.Ldc_I4_S.Value) return (sbyte)il[at];
        if (code == OpCodes.Ldc_I4.Value) return BitConverter.ToInt32(il, at);
        return null;
    }
}
