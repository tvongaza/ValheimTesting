// Hand-authored game metadata shared by toolkit and scripted acceptance tests. No proprietary assembly bytes.
internal static class GameAssemblyFixture
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Assemblies = new();
    internal static byte[] Create(string gameVersion, object? networkConstant, int minimum = 9, int maximum = 42, int chunked = 40, string shape = "interval") => Assemblies.GetOrAdd($"{gameVersion}|{networkConstant?.GetType().Name}:{networkConstant}|{minimum}|{maximum}|{chunked}|{shape}", _ =>
    {
        var assembly = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("assembly_valheim"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("assembly_valheim");
        var gameVersionType = module.DefineType("GameVersion", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed |
            System.Reflection.TypeAttributes.SequentialLayout, typeof(ValueType));
        var constructor = gameVersionType.DefineConstructor(System.Reflection.MethodAttributes.Public, System.Reflection.CallingConventions.Standard, [typeof(int), typeof(int), typeof(int)]);
        constructor.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        var version = module.DefineType("Version", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract);
        if (networkConstant == null) version.DefineField("c_networkVersion", typeof(uint), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        else version.DefineField("c_networkVersion", networkConstant.GetType(), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static |
            System.Reflection.FieldAttributes.Literal).SetConstant(networkConstant);
        var world = version.DefineNestedType("World", System.Reflection.TypeAttributes.NestedPublic | System.Reflection.TypeAttributes.Sealed, typeof(Enum));
        world.DefineField("value__", typeof(int), System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.SpecialName | System.Reflection.FieldAttributes.RTSpecialName);
        var literal = System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static | System.Reflection.FieldAttributes.Literal;
        Type fieldType = shape == "primitive" ? typeof(int) : world;
        world.DefineField("OldestForwardCompatible", fieldType, literal).SetConstant(minimum);
        if (shape != "missing") world.DefineField("ChunkedSave", fieldType, literal).SetConstant(chunked);
        version.DefineField("c_WorldVersion", fieldType, literal).SetConstant(maximum);
        var predicate = version.DefineMethod("IsWorldVersionCompatible", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            typeof(bool), [shape == "signature" ? typeof(int) : world]);
        var body = predicate.GetILGenerator();
        var no = body.DefineLabel();
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        body.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, shape == "contradictory" ? maximum + 1 : maximum);
        body.Emit(shape == "unsigned" ? System.Reflection.Emit.OpCodes.Bgt_Un_S : System.Reflection.Emit.OpCodes.Bgt_S, no);
        body.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        body.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, minimum);
        body.Emit(System.Reflection.Emit.OpCodes.Clt);
        body.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
        body.Emit(System.Reflection.Emit.OpCodes.Ceq);
        body.Emit(System.Reflection.Emit.OpCodes.Ret);
        body.MarkLabel(no);
        body.Emit(shape == "extra" ? System.Reflection.Emit.OpCodes.Ldc_I4_1 : System.Reflection.Emit.OpCodes.Ldc_I4_0);
        body.Emit(System.Reflection.Emit.OpCodes.Ret);
        world.CreateType();
        var first = version.DefineField("FirstVersionWithNetworkVersion", gameVersionType, System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static);
        var current = version.DefineField("<CurrentVersion>k__BackingField", gameVersionType, System.Reflection.FieldAttributes.Private | System.Reflection.FieldAttributes.Static);
        var il = version.DefineTypeInitializer().GetILGenerator();
        foreach (var (field, parts) in new[] { (first, new[] { 0, 214, 301 }), (current, gameVersion.Split('.').Select(int.Parse).ToArray()) })
        {
            foreach (int part in parts) il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, part);
            il.Emit(System.Reflection.Emit.OpCodes.Newobj, constructor);
            il.Emit(System.Reflection.Emit.OpCodes.Stsfld, field);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        var outer = module.DefineType("Decoys", System.Reflection.TypeAttributes.Public);
        outer.DefineNestedType("Version", System.Reflection.TypeAttributes.NestedPublic).CreateType();
        outer.CreateType();
        gameVersionType.CreateType();
        version.CreateType();
        using var image = new MemoryStream();
        assembly.Save(image);
        return image.ToArray();
    });
}
