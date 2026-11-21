using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class UnicodePatch
{
    private const string UpstreamHash = "183BABDD54F873F6FABBFB375E17A69C2F0BE0A87EC548EC6B22760D4FEBF76E";
    private const string UnicodeTextType = "Microsoft.UI.Xaml.Documents.UnicodeText";

    public static void Apply(string original, string patched)
    {
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))) != UpstreamHash)
            throw new InvalidOperationException("Uno 6.6.184 Skia assembly hash does not match pinned upstream.");

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(original)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        using var assembly = AssemblyDefinition.ReadAssembly(original, new ReaderParameters { AssemblyResolver = resolver });
        if (assembly.Name.Name != "Uno.UI" || assembly.Name.HasPublicKey)
            throw new InvalidOperationException("Unsupported Uno Skia assembly identity.");
        var type = assembly.MainModule.GetType(UnicodeTextType);
        var constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var instructions = constructor.Body.Instructions;
        var readSites = instructions.Where(instruction => IsCharCall(instruction, "ConvertToUtf32")).ToArray();
        var advance = instructions.Single(instruction => IsCharCall(instruction, "IsSurrogate"));
        if (readSites.Length != 2 || readSites[0].Next.OpCode != OpCodes.Stloc_S
            || advance.Previous.OpCode != OpCodes.Ldloc_S || advance.Previous.Previous.OpCode != OpCodes.Ldloc_S)
            throw new InvalidOperationException("UnicodeText code point loop changed.");

        var read = AddCodePointReader(type, (MethodReference)readSites[0].Operand, (MethodReference)advance.Operand);
        foreach (var site in readSites)
            site.Operand = read;

        // Advance two UTF-16 units only for a decoded supplementary scalar. Like Uno 7's
        // ReadCodepoint, a lone surrogate participates in font/script lookup as U+FFFD.
        var loadScalar = advance.Previous.Previous;
        loadScalar.Operand = readSites[0].Next.Operand;
        advance.Previous.OpCode = OpCodes.Ldc_I4;
        advance.Previous.Operand = 0xffff;
        advance.OpCode = OpCodes.Cgt;
        advance.Operand = null;

        Directory.CreateDirectory(Path.GetDirectoryName(patched)!);
        assembly.Write(patched);
        using var verified = AssemblyDefinition.ReadAssembly(patched);
        var verifiedType = verified.MainModule.GetType(UnicodeTextType);
        var loop = verifiedType.Methods.Single(method => method.IsConstructor && !method.IsStatic).Body.Instructions;
        if (loop.Count(instruction => instruction.Operand is MethodReference method && method.Name == read.Name) != 2
            || loop.Any(instruction => IsCharCall(instruction, "ConvertToUtf32") || IsCharCall(instruction, "IsSurrogate")))
            throw new InvalidOperationException("Uno Unicode patch failed post-write verification.");
        Console.WriteLine("UNO_SKIA_UNICODE_PATCH=PASS");
        Console.WriteLine("PATCHED_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(patched))).ToLowerInvariant());
    }

    private static bool IsCharCall(Instruction instruction, string name) =>
        instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference method
        && method.DeclaringType.FullName == "System.Char" && method.Name == name
        && method.Parameters.Count == 2 && method.Parameters[0].ParameterType.MetadataType == MetadataType.String;

    private static MethodDefinition AddCodePointReader(TypeDefinition type, MethodReference convert, MethodReference isSurrogate)
    {
        var types = type.Module.TypeSystem;
        var read = new MethodDefinition("ReadCodePointOrReplacement", MethodAttributes.Private | MethodAttributes.Static, types.Int32);
        read.Parameters.Add(new ParameterDefinition("text", ParameterAttributes.None, types.String));
        read.Parameters.Add(new ParameterDefinition("index", ParameterAttributes.None, types.Int32));
        type.Methods.Add(read);

        MethodReference CharTest(string name)
        {
            var method = new MethodReference(name, types.Boolean, convert.DeclaringType);
            method.Parameters.Add(new ParameterDefinition(types.String));
            method.Parameters.Add(new ParameterDefinition(types.Int32));
            return method;
        }
        var length = new MethodReference("get_Length", types.Int32, types.String) { HasThis = true };
        var chars = new MethodReference("get_Chars", types.Char, types.String) { HasThis = true };
        chars.Parameters.Add(new ParameterDefinition(types.Int32));

        var il = read.Body.GetILProcessor();
        var replacement = il.Create(OpCodes.Ldc_I4, 0xfffd);
        var bmp = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, isSurrogate);
        il.Emit(OpCodes.Brfalse_S, bmp);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, CharTest("IsHighSurrogate"));
        il.Emit(OpCodes.Brfalse_S, replacement);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, length);
        il.Emit(OpCodes.Bge_S, replacement);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Call, CharTest("IsLowSurrogate"));
        il.Emit(OpCodes.Brfalse_S, replacement);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, convert);
        il.Emit(OpCodes.Ret);
        il.Append(replacement);
        il.Emit(OpCodes.Ret);
        il.Append(bmp);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, chars);
        il.Emit(OpCodes.Ret);
        return read;
    }
}
