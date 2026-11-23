using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neuterradise.App.SystemServices.MediaTools;

public sealed record JpegXlInfo(int Width, int Height, int Orientation, int BitDepth, bool Animated,
    bool HasAlpha, uint LoopCount);

public sealed record JpegXlFrame(byte[] Rgba, TimeSpan Duration);

public sealed record JpegXlImage(JpegXlInfo Info, int Width, int Height, IReadOnlyList<JpegXlFrame> Frames);

/// <summary>Shared libjxl adapter for runtime rendering and the isolated Profiling Worker.</summary>
public static unsafe partial class JpegXlDecoder
{
    public const string RelativeNativeDirectory = "tools/jpeg-xl";
    private const int MaximumInputBytes = 256 * 1024 * 1024;
    private const int MaximumSourcePixels = 48_000_000;
    private const int MaximumEdge = 16384;
    private const int MaximumFrames = 240;
    private const int MaximumOutputBytes = 64 * 1024 * 1024;
    private const uint MaximumNativeBytes = 512 * 1024 * 1024;
    private const int BasicInfo = 0x40;
    private const int ColorEncoding = 0x100;
    private const int Frame = 0x400;
    private const int FullImage = 0x1000;
    private const int NeedOutput = 5;
    private static readonly Lock DecodeGate = new();
    private static readonly Lazy<string> VerifiedNativeDirectory = new(VerifyNativeDirectory);

    static JpegXlDecoder() => NativeLibrary.SetDllImportResolver(
        typeof(JpegXlDecoder).Assembly, ResolveNativeLibrary);

    public static bool HasSignature(ReadOnlySpan<byte> header) =>
        header is [0xff, 0x0a, ..] or
            [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', 32, 13, 10, 0x87, 10, ..];

    public static JpegXlInfo ReadInfo(FileStream stream) =>
        Read(stream, 0, 0, readAnimation: false, default).Info;

    public static JpegXlImage Decode(string path, int maximumWidth, int maximumHeight, bool readAnimation = false,
        CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream, maximumWidth, maximumHeight, readAnimation, cancellationToken);
    }

    public static JpegXlImage Decode(FileStream stream, int maximumWidth, int maximumHeight, bool readAnimation = false,
        CancellationToken cancellationToken = default) =>
        Read(stream, Math.Clamp(maximumWidth, 1, 4096), Math.Clamp(maximumHeight, 1, 4096), readAnimation, cancellationToken);

    private static JpegXlImage Read(FileStream input, int maximumWidth, int maximumHeight, bool readAnimation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length is < 2 or > MaximumInputBytes)
            throw new InvalidDataException("JPEG XL exceeds the input size limit or has a truncated header.");

        // Keep the input mapped and let libjxl emit stripes directly into a target-sized output.
        // One decode at a time bounds native working memory even when thumbnails arrive in bursts.
        lock (DecodeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var map = MemoryMappedFile.CreateFromFile(input, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: true);
            using var view = map.CreateViewAccessor(0, input.Length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                return Process(pointer + view.PointerOffset, (int)input.Length, maximumWidth, maximumHeight, readAnimation, cancellationToken);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    private static JpegXlImage Process(byte* input, int length, int maximumWidth, int maximumHeight, bool readAnimation,
        CancellationToken cancellationToken)
    {
        if (!HasSignature(new ReadOnlySpan<byte>(input, length)))
            throw new InvalidDataException("JPEG XL signature is missing.");

        var metadataOnly = maximumWidth == 0;
        nuint allocated = 0;
        var manager = new MemoryManager { Opaque = &allocated, Allocate = &Allocate, Release = &Release };
        var decoder = JxlDecoderCreate(&manager);
        if (decoder == 0) throw new InvalidDataException("JPEG XL decoder could not be initialized.");

        try
        {
            Check(JxlDecoderSubscribeEvents(decoder, metadataOnly ? BasicInfo : BasicInfo | ColorEncoding | Frame | FullImage));
            if (metadataOnly)
                Check(JxlDecoderSetKeepOrientation(decoder, 1));
            else
            {
                Check(JxlDecoderSetCms(decoder, *JxlGetDefaultCms()));
                Check(JxlDecoderSetDesiredIntensityTarget(decoder, 255));
                Check(JxlDecoderSetUnpremultiplyAlpha(decoder, 1));
            }

            Check(JxlDecoderSetInput(decoder, input, (nuint)length));
            JxlDecoderCloseInput(decoder);
            JpegXlInfo? info = null;
            var frames = new List<JpegXlFrame>();
            var nativeInfo = default(NativeInfo);
            var output = default(StripeOutput);
            var duration = TimeSpan.Zero;
            var frameHeader = default(FrameHeader);
            var format = new PixelFormat { Channels = 4, Type = 2 };
            byte[]? rgba = null;
            var pin = default(GCHandle);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (JxlDecoderProcessInput(decoder))
                    {
                        case BasicInfo:
                            Check(JxlDecoderGetBasicInfo(decoder, &nativeInfo));
                            info = ValidateInfo(nativeInfo);
                            if (metadataOnly) return new JpegXlImage(info, 0, 0, []);
                            var scale = Math.Min(1d, Math.Min((double)maximumWidth / info.Width, (double)maximumHeight / info.Height));
                            output.SourceWidth = info.Width;
                            output.SourceHeight = info.Height;
                            output.Width = Math.Max(1, (int)Math.Round(info.Width * scale));
                            output.Height = Math.Max(1, (int)Math.Round(info.Height * scale));
                            break;
                        case ColorEncoding:
                            var color = default(NativeColorEncoding);
                            JxlColorEncodingSetToSRGB(&color, nativeInfo.ColorChannels == 1 ? 1 : 0);
                            Check(JxlDecoderSetOutputColorProfile(decoder, &color, null, 0));
                            break;
                        case Frame:
                            Check(JxlDecoderGetFrameHeader(decoder, &frameHeader));
                            if (info?.Animated == true)
                                duration = TimeSpan.FromSeconds(Math.Min(60,
                                    (double)frameHeader.Duration * nativeInfo.TickDenominator / nativeInfo.TickNumerator));
                            break;
                        case NeedOutput:
                            var bytes = output.Width * output.Height * 4;
                            if (bytes == 0 || frames.Count >= MaximumFrames || ((long)frames.Count + 1) * bytes > MaximumOutputBytes)
                                throw new InvalidDataException("JPEG XL exceeds the decoded frame limit.");
                            rgba = new byte[bytes];
                            pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                            output.Pixels = (byte*)pin.AddrOfPinnedObject();
                            Check(JxlDecoderSetImageOutCallback(decoder, &format, &WriteStripe, &output));
                            break;
                        case FullImage when rgba is not null:
                            pin.Free();
                            output.Pixels = null;
                            frames.Add(new JpegXlFrame(rgba, duration));
                            rgba = null;
                            if (!readAnimation && info?.Animated == true)
                                return new JpegXlImage(info, output.Width, output.Height, frames);
                            break;
                        case 0 when info is not null && frames.Count > 0:
                            return new JpegXlImage(info, output.Width, output.Height, frames);
                        default:
                            throw new InvalidDataException("JPEG XL is truncated, invalid, or exceeds the decoder budget.");
                    }
                }
            }
            finally
            {
                if (pin.IsAllocated) pin.Free();
            }
        }
        finally
        {
            JxlDecoderDestroy(decoder);
        }
    }

    private static JpegXlInfo ValidateInfo(NativeInfo value)
    {
        if (value.Width == 0 || value.Height == 0 || value.Width > MaximumEdge || value.Height > MaximumEdge
            || (ulong)value.Width * value.Height > MaximumSourcePixels
            || value.Orientation is < 1 or > 8 || value.ColorChannels is not (1 or 3)
            || (value.Animated != 0 && (value.TickNumerator == 0 || value.TickDenominator == 0)))
            throw new InvalidDataException("JPEG XL dimensions or color and animation parameters are unsupported.");
        return new((int)value.Width, (int)value.Height, (int)value.Orientation, (int)value.BitDepth,
            value.Animated != 0, value.AlphaBits != 0, value.LoopCount);
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidDataException("JPEG XL decoder rejected the input or output format.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WriteStripe(void* context, nuint x, nuint y, nuint count, byte* pixels)
    {
        var output = (StripeOutput*)context;
        var row = ((int)y * output->Height + output->SourceHeight - 1) / output->SourceHeight;
        if (row >= output->Height || row * output->SourceHeight / output->Height != (int)y) return;

        var first = ((int)x * output->Width + output->SourceWidth - 1) / output->SourceWidth;
        var end = ((int)(x + count) * output->Width + output->SourceWidth - 1) / output->SourceWidth;
        var destination = (uint*)output->Pixels + row * output->Width;
        for (var column = first; column < end; column++)
        {
            var source = column * output->SourceWidth / output->Width - (int)x;
            destination[column] = Unsafe.ReadUnaligned<uint>(pixels + source * 4);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* Allocate(void* context, nuint size)
    {
        var total = (nuint*)context;
        if (size > MaximumNativeBytes - *total) return null;
        void* block;
        try { block = NativeMemory.Alloc(size + 16); }
        catch (OutOfMemoryException) { return null; }
        *(nuint*)block = size;
        *total += size;
        return (byte*)block + 16;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(void* context, void* address)
    {
        if (address == null) return;
        var block = (byte*)address - 16;
        *(nuint*)context -= *(nuint*)block;
        NativeMemory.Free(block);
    }

    private static nint ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is not ("jxl" or "jxl_cms")) return 0;
        var path = Path.Combine(VerifiedNativeDirectory.Value, libraryName + ".dll");
        var library = LoadLibraryEx(path, 0, 0x00000900);
        if (library == 0)
            throw new DllNotFoundException($"The packaged JPEG XL component {libraryName} could not be loaded.");
        return library;
    }

    private static string VerifyNativeDirectory()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DllNotFoundException("The packaged JPEG XL decoder requires Windows x64.");

        // The app lives at the runtime root; the Profiling Worker lives directly below it.
        var applicationDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var runtimeDirectory = Path.GetFileName(applicationDirectory).Equals("workers", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(applicationDirectory)! : applicationDirectory;
        var folder = Path.Combine(runtimeDirectory, RelativeNativeDirectory);
        using var stream = typeof(JpegXlDecoder).Assembly.GetManifestResourceStream(
            "Neuterradise.Release.Contracts.jpeg-xl-runtime.lock.json")
            ?? throw new InvalidOperationException("The embedded JPEG XL dependency lock is missing.");
        using var dependency = JsonDocument.Parse(stream);
        foreach (var file in dependency.RootElement.GetProperty("files").EnumerateObject())
        {
            try
            {
                using var library = File.OpenRead(Path.Combine(folder, file.Name));
                if (!Convert.ToHexString(SHA256.HashData(library)).Equals(file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new DllNotFoundException($"The packaged JPEG XL component {file.Name} failed SHA-256 verification.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new DllNotFoundException($"The packaged JPEG XL component {file.Name} is unavailable.", exception);
            }
        }
        return folder;
    }

    private struct StripeOutput { public int SourceWidth, SourceHeight, Width, Height; public byte* Pixels; }
    private struct MemoryManager
    {
        public void* Opaque;
        public delegate* unmanaged[Cdecl]<void*, nuint, void*> Allocate;
        public delegate* unmanaged[Cdecl]<void*, void*, void> Release;
    }
    private struct PixelFormat { public uint Channels, Type, Endianness; public nuint Alignment; }
    private struct NativeColorEncoding { private fixed double _storage[13]; }
    private struct CmsInterface { private fixed long _pointers[8]; }
    private struct FrameHeader { public uint Duration; private fixed uint _remainingFields[13]; }
    private struct NativeInfo
    {
        public uint HasContainer, Width, Height, BitDepth, ExponentBits;
        public float IntensityTarget, MinimumNits;
        public uint RelativeToDisplay;
        public float LinearBelow;
        public uint OriginalProfile, HasPreview, Animated, Orientation, ColorChannels, ExtraChannels, AlphaBits,
            AlphaExponentBits, Premultiplied, PreviewWidth, PreviewHeight, TickNumerator, TickDenominator,
            LoopCount, HasTimecodes, IntrinsicWidth, IntrinsicHeight;
        private fixed byte _padding[100];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string fileName, nint file, uint flags);
    [LibraryImport("jxl")] private static partial nint JxlDecoderCreate(MemoryManager* manager);
    [LibraryImport("jxl")] private static partial void JxlDecoderDestroy(nint decoder);
    [LibraryImport("jxl")] private static partial int JxlDecoderSubscribeEvents(nint decoder, int events);
    [LibraryImport("jxl")] private static partial int JxlDecoderSetKeepOrientation(nint decoder, int keep);
    [LibraryImport("jxl")] private static partial int JxlDecoderSetUnpremultiplyAlpha(nint decoder, int enabled);
    [LibraryImport("jxl")] private static partial int JxlDecoderSetDesiredIntensityTarget(nint decoder, float nits);
    [LibraryImport("jxl")] private static partial int JxlDecoderSetCms(nint decoder, CmsInterface cms);
    [LibraryImport("jxl_cms")] private static partial CmsInterface* JxlGetDefaultCms();
    [LibraryImport("jxl")] private static partial int JxlDecoderSetInput(nint decoder, byte* input, nuint length);
    [LibraryImport("jxl")] private static partial void JxlDecoderCloseInput(nint decoder);
    [LibraryImport("jxl")] private static partial int JxlDecoderProcessInput(nint decoder);
    [LibraryImport("jxl")] private static partial int JxlDecoderGetBasicInfo(nint decoder, NativeInfo* info);
    [LibraryImport("jxl")] private static partial void JxlColorEncodingSetToSRGB(NativeColorEncoding* color, int grey);
    [LibraryImport("jxl")] private static partial int JxlDecoderSetOutputColorProfile(nint decoder, NativeColorEncoding* color, byte* icc, nuint length);
    [LibraryImport("jxl")] private static partial int JxlDecoderGetFrameHeader(nint decoder, FrameHeader* frame);
    [LibraryImport("jxl")] private static partial int JxlDecoderSetImageOutCallback(nint decoder, PixelFormat* format,
        delegate* unmanaged[Cdecl]<void*, nuint, nuint, nuint, byte*, void> callback, void* context);
}
