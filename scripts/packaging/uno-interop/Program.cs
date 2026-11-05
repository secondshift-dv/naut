using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
{
    throw new ArgumentException("Expected original Uno Win32 assembly path and output patch path.");
}

const string upstreamHash = "d7cf63b3bb97409797ab12fb1cec4c772a724d7614b3fe39358a712b5bd9adea";
const string targetType = "Uno.UI.Runtime.Skia.Win32.Win32UIAutomationInterop";
const string targetMethod = "UiaRaiseStructureChangedEvent";
const string nativeLibrary = "uiautomationcore.dll";

var original = Path.GetFullPath(args[0]);
var patched = Path.GetFullPath(args[1]);
if (string.Equals(original, patched, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Never mutate the upstream assembly or NuGet cache.");
}
if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))),
    upstreamHash, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Uno 6.6.184 Win32 assembly hash does not match pinned upstream.");
}

using var assembly = AssemblyDefinition.ReadAssembly(original, new ReaderParameters { ReadSymbols = false });
var module = assembly.MainModule;
if (assembly.Name.Name != "Uno.UI.Runtime.Skia.Win32" || assembly.Name.HasPublicKey)
{
    throw new InvalidOperationException("Unsupported upstream Uno assembly identity.");
}

var interop = module.Types.Single(type => type.FullName == targetType);
var native = interop.Methods.Single(method => method.Name == targetMethod);
if (!native.IsPInvokeImpl || native.PInvokeInfo is null
    || !string.Equals(native.PInvokeInfo.Module.Name, nativeLibrary, StringComparison.OrdinalIgnoreCase)
    || native.Parameters.Count != 3 || native.ReturnType.MetadataType != MetadataType.Int32
    || native.Parameters[2].ParameterType is not ArrayType array
    || array.ElementType.MetadataType != MetadataType.Int32
    || native.Parameters[2].MarshalInfo?.NativeType != NativeType.SafeArray)
{
    throw new InvalidOperationException("Uno P/Invoke signature changed; review upstream instead of applying the patch.");
}

// The Windows API accepts (provider, type, int* runtimeId, int count), not a SAFEARRAY.
// Preserve the existing COM provider and all UIA notifications; only repair the native ABI.
native.Parameters[2].MarshalInfo = null;
native.Parameters.Add(new ParameterDefinition("cRuntimeIdLen", Mono.Cecil.ParameterAttributes.None,
    module.TypeSystem.Int32));

var callCount = 0;
foreach (var type in module.Types)
{
    foreach (var method in type.Methods.Where(method => method.HasBody))
    {
        foreach (var instruction in method.Body.Instructions.ToArray())
        {
            if (instruction.OpCode != OpCodes.Call
                || instruction.Operand is not MethodReference candidate
                || candidate.Name != targetMethod
                || candidate.DeclaringType.FullName != targetType)
            {
                continue;
            }

            if (method.DeclaringType.FullName != "Uno.UI.Runtime.Skia.Win32.Win32Accessibility"
                || candidate.Parameters.Count != 4)
            {
                throw new InvalidOperationException($"Unrecognized caller: {method.DeclaringType.FullName}.{method.Name}, arguments={candidate.Parameters.Count}; refuse to patch.");
            }

            // Original stack is [provider, eventType, int[]]. Append the array length for Win32.
            var il = method.Body.GetILProcessor();
            il.InsertBefore(instruction, il.Create(OpCodes.Dup));
            il.InsertBefore(instruction, il.Create(OpCodes.Ldlen));
            il.InsertBefore(instruction, il.Create(OpCodes.Conv_I4));
            instruction.Operand = native;
            callCount++;
        }
    }
}
if (callCount != 2)
{
    throw new InvalidOperationException($"Expected exactly two Uno UIA callers; found {callCount}.");
}

Directory.CreateDirectory(Path.GetDirectoryName(patched)!);
assembly.Write(patched);

// Verify the generated metadata, including every caller, before allowing packaging.
using var verified = AssemblyDefinition.ReadAssembly(patched);
var verifiedType = verified.MainModule.Types.Single(type => type.FullName == targetType);
var verifiedNative = verifiedType.Methods.Single(method => method.Name == targetMethod);
if (verifiedNative.Parameters.Count != 4 || !verifiedNative.IsPInvokeImpl
    || verifiedNative.Parameters[2].MarshalInfo?.NativeType == NativeType.SafeArray
    || verifiedNative.Parameters[3].ParameterType.MetadataType != MetadataType.Int32)
{
    throw new InvalidOperationException("Generated Uno P/Invoke ABI failed post-write verification.");
}
var verifiedCalls = verified.MainModule.Types
    .SelectMany(type => type.Methods.Where(method => method.HasBody))
    .SelectMany(method => method.Body.Instructions)
    .Count(instruction => instruction.OpCode == OpCodes.Call
        && instruction.Operand is MethodReference candidate
        && candidate.Name == targetMethod && candidate.DeclaringType.FullName == targetType
        && candidate.Parameters.Count == 4);
if (verifiedCalls != 2)
{
    throw new InvalidOperationException("Patched Uno call-site verification failed.");
}
Console.WriteLine("UNO_UIA_INTEROP_PATCH=PASS");
Console.WriteLine("UPSTREAM_SHA256=" + upstreamHash);
Console.WriteLine("PATCHED_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(patched))).ToLowerInvariant());
Console.WriteLine("UIA_CALLERS=" + verifiedCalls);
