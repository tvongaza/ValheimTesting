using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Valheim.Testing.Game;

/// <summary>Reads a game assembly's <see cref="GameBuild"/> from its metadata, without loading it or running game code.</summary>
internal static class GameAssemblyVersion
{
    internal const long AssemblyLimit = 32L << 20;

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
            var world = WorldSupportOf(pe, reader, version[0], source, out string? worldUnknown);
            uint? network = null;
            string? unknown = null;
            try
            {
                var constant = version[0].GetFields().Select(reader.GetFieldDefinition)
                    .Where(field => reader.GetString(field.Name) == NetworkVersionField).ToList();
                if (constant.Count != 1 || (constant[0].Attributes & System.Reflection.FieldAttributes.Literal) == 0 || constant[0].GetDefaultValue().IsNil)
                    unknown = $"{source} has no const Version.{NetworkVersionField}";
                else
                {
                    var value = reader.GetConstant(constant[0].GetDefaultValue());
                    if (value.TypeCode != ConstantTypeCode.UInt32)
                        unknown = $"{source}'s Version.{NetworkVersionField} is {value.TypeCode}, not a uint";
                    else network = reader.GetBlobReader(value.Value).ReadUInt32();
                }
            }
            catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
            { unknown = $"{source} has unreadable network metadata ({error.Message})"; }
            string? gameVersion;
            try { gameVersion = CurrentVersion(pe, reader, version[0]); }
            catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
            { gameVersion = null; }

            return new(network, gameVersion, unknown) { WorldSupport = world, WorldUnknown = worldUnknown };
        }
        catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            return new(null, null, $"{source} is not a readable .NET assembly ({error.Message})");
        }
    }

    internal static async Task<GameBuild> ReadInstallAsync(string install, CancellationToken cancellation)
    {
        string source = install;
        try
        {
            source = InstallPins.GameAssembly(install);
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);
            if (file.Length > AssemblyLimit) return new(null, null, $"{source} exceeds the game assembly read limit");
            var bytes = new byte[file.Length];
            await file.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            using var image = new MemoryStream(bytes, writable: false);
            return Read(image, source + " on this machine");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return new(null, null, $"{source}: {error.Message}"); }
    }

    // Recognizes the complete signed interval predicate verified against installed game metadata. This is deliberately
    // not an IL interpreter: a different body is unknown, even when its constants happen to have familiar names.
    private static WorldSupport? WorldSupportOf(PEReader pe, MetadataReader reader, TypeDefinition version, string source, out string? unknown)
    {
        unknown = $"{source} has unrecognized world-format metadata or compatibility predicate";
        try
        {
            var worlds = version.GetNestedTypes().Where(handle => reader.GetString(reader.GetTypeDefinition(handle).Name) == "World").ToArray();
            if (worlds.Length != 1) return null;
            var handle = worlds[0];
            var world = reader.GetTypeDefinition(handle);
            if (world.BaseType.Kind != HandleKind.TypeReference) return null;
            var baseType = reader.GetTypeReference((TypeReferenceHandle)world.BaseType);
            if (reader.GetString(baseType.Name) != "Enum" || reader.GetString(baseType.Namespace) != "System") return null;
            var values = world.GetFields().Select(reader.GetFieldDefinition).Where(field => reader.GetString(field.Name) == "value__").ToArray();
            if (values.Length != 1 || !reader.GetBlobBytes(values[0].Signature).SequenceEqual(new byte[] { 6, 8 })) return null;
            bool WorldType(ref BlobReader signature) => signature.ReadByte() == 0x11 && signature.ReadTypeHandle() == handle; // ELEMENT_TYPE_VALUETYPE, exact enum
            int? Constant(TypeDefinition type, string name)
            {
                var fields = type.GetFields().Select(reader.GetFieldDefinition).Where(field => reader.GetString(field.Name) == name).ToArray();
                if (fields.Length != 1) return null;
                var field = fields[0];
                var flags = System.Reflection.FieldAttributes.Static | System.Reflection.FieldAttributes.Literal;
                if ((field.Attributes & flags) != flags || field.GetDefaultValue().IsNil) return null;
                var sig = reader.GetBlobReader(field.Signature);
                if (sig.ReadSignatureHeader().Kind != SignatureKind.Field || !WorldType(ref sig) || sig.RemainingBytes != 0) return null;
                var constant = reader.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode != ConstantTypeCode.Int32 || reader.GetBlobBytes(constant.Value).Length != 4) return null;
                return reader.GetBlobReader(constant.Value).ReadInt32();
            }
            var min = Constant(world, "OldestForwardCompatible");
            var max = Constant(version, "c_WorldVersion");
            var chunked = Constant(world, "ChunkedSave");
            if (min == null || max == null || chunked == null || min > chunked || chunked > max) return null;
            var methods = version.GetMethods().Select(reader.GetMethodDefinition).Where(method => reader.GetString(method.Name) == "IsWorldVersionCompatible").ToArray();
            if (methods.Length != 1) return null;
            var method = methods[0];
            if ((method.Attributes & System.Reflection.MethodAttributes.Static) == 0 || method.GetGenericParameters().Count != 0 || method.RelativeVirtualAddress == 0 ||
                (method.ImplAttributes & (System.Reflection.MethodImplAttributes.CodeTypeMask | System.Reflection.MethodImplAttributes.ManagedMask)) != System.Reflection.MethodImplAttributes.IL) return null;
            var signature = reader.GetBlobReader(method.Signature);
            var header = signature.ReadSignatureHeader();
            if (header.IsInstance || header.IsGeneric || header.CallingConvention != SignatureCallingConvention.Default || signature.ReadCompressedInteger() != 1 ||
                signature.ReadSignatureTypeCode() != SignatureTypeCode.Boolean || !WorldType(ref signature) || signature.RemainingBytes != 0) return null;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil || body.ExceptionRegions.Length != 0) return null;
            byte[] il = body.GetILBytes() ?? [];
            int at = 0;
            bool Code(OpCode expected)
            {
                if (at >= il.Length || !OpCodeAt(il, ref at, out short actual)) return false;
                return actual == expected.Value;
            }
            int? Integer()
            {
                if (at >= il.Length || !OpCodeAt(il, ref at, out short code)) return null;
                int size = OperandSize(code, il, at);
                if (size < 0 || at + size > il.Length) return null;
                int? value = Int32Constant(code, il, at); at += size;
                return value;
            }
            if (!Code(OpCodes.Ldarg_0) || Integer() != max) return null;
            if (at >= il.Length || !OpCodeAt(il, ref at, out short branch)) return null;
            int branchTarget;
            if (branch == OpCodes.Bgt_S.Value && at < il.Length) { branchTarget = at + 1 + (sbyte)il[at]; at++; }
            else if (branch == OpCodes.Bgt.Value && at + 4 <= il.Length) { branchTarget = at + 4 + BitConverter.ToInt32(il, at); at += 4; }
            else return null;
            if (!Code(OpCodes.Ldarg_0) || Integer() != min || !Code(OpCodes.Clt) || !Code(OpCodes.Ldc_I4_0) || !Code(OpCodes.Ceq) || !Code(OpCodes.Ret) || at != branchTarget ||
                !Code(OpCodes.Ldc_I4_0) || !Code(OpCodes.Ret) || at != il.Length) return null;
            unknown = null;
            return new(min.Value, max.Value, chunked.Value, source, FileHash.Sha256(pe.GetEntireImage().GetContent().ToArray()));
        }
        catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException or OverflowException)
        { return null; }
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
