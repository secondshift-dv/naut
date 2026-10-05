using System.Runtime.InteropServices;

namespace Neuterradise.App.Ui.Figure;

internal static class FigureD3D
{
    // Two unordered-access clear methods precede depth/stencil clear in the context COM interface.
    internal const int ClearDepthStencilViewSlot = 53;
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferDescription
    {
        public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Subresource { public IntPtr Data; public uint Pitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct InputElement
    {
        public IntPtr Semantic;
        public uint SemanticIndex, Format, Slot, Offset, Classification, InstanceStep;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Viewport { public float X, Y, Width, Height, MinimumDepth, MaximumDepth; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RasterizerDescription
    {
        public uint FillMode, CullMode;
        public int FrontCounterClockwise, DepthBias;
        public float DepthBiasClamp, SlopeScaledDepthBias;
        public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SamplerDescription
    {
        public uint Filter, AddressU, AddressV, AddressW;
        public float MipBias;
        public uint MaxAnisotropy, Comparison;
        public float BorderR, BorderG, BorderB, BorderA, MinLod, MaxLod;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DepthOperation { public uint Fail, DepthFail, Pass, Function; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DepthDescription
    {
        public int Enabled;
        public uint WriteMask, Function;
        public int StencilEnabled;
        public byte ReadMask, WriteStencilMask;
        public DepthOperation Front, Back;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendTarget
    {
        public int Enabled;
        public uint Source, Destination, Operation, SourceAlpha, DestinationAlpha, AlphaOperation;
        public byte WriteMask;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendDescription
    {
        public int AlphaToCoverage, IndependentBlend;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public BlendTarget[] Targets;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateBuffer(IntPtr self, ref BufferDescription desc, ref Subresource data, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateTexture(IntPtr self, ref TextureDescription desc, [In] Subresource[]? data, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateView(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateShader(IntPtr self, IntPtr bytecode, nuint length, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateLayout(IntPtr self, [In] InputElement[] elements, uint count, IntPtr bytecode, nuint length, out IntPtr layout);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateRasterizer(IntPtr self, ref RasterizerDescription desc, out IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateSampler(IntPtr self, ref SamplerDescription desc, out IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateDepth(IntPtr self, ref DepthDescription desc, out IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateBlend(IntPtr self, ref BlendDescription desc, out IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetOne(IntPtr self, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetShader(IntPtr self, IntPtr value, IntPtr instances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetArray(IntPtr self, uint slot, uint count, [In] IntPtr[] values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetVertexBuffers(IntPtr self, uint slot, uint count, [In] IntPtr[] values, [In] uint[] strides, [In] uint[] offsets);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetIndexBuffer(IntPtr self, IntPtr buffer, uint format, uint offset);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetTopology(IntPtr self, uint topology);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetTargets(IntPtr self, uint count, [In] IntPtr[] values, IntPtr depth);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetViewports(IntPtr self, uint count, ref Viewport viewport);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetDepth(IntPtr self, IntPtr state, uint stencilReference);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void SetBlend(IntPtr self, IntPtr state, [In] float[] factor, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ClearDepth(IntPtr self, IntPtr view, uint flags, float depth, byte stencil);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DrawIndexed(IntPtr self, uint count, uint start, int vertex);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void Update(IntPtr self, IntPtr resource, uint subresource, IntPtr box, IntPtr data, uint pitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void GetResource(IntPtr self, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void GetTextureDescription(IntPtr self, out TextureDescription desc);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void CopyRegion(IntPtr self, IntPtr destination, uint destinationSubresource, uint x, uint y, uint z,
        IntPtr source, uint sourceSubresource, ref Box box);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int Map(IntPtr self, IntPtr resource, uint subresource, uint type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void Unmap(IntPtr self, IntPtr resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr BlobData(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nuint BlobSize(IntPtr self);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
    internal static extern int D3DCompile([In] byte[] source, nuint size, string name, IntPtr defines,
        IntPtr include, string entry, string target, uint flags, uint effectFlags, out IntPtr code, out IntPtr errors);
}
