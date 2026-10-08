using System.Runtime.InteropServices;
using SharpAssimp;
using SharpAssimp.Unmanaged;

// The wrapper's default file-open callback uses ANSI strings. Assimp requests UTF-8 paths.
// Keep every callback and cursor alive until native import has released the custom IO system.
sealed class Utf8ModelIO : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenCallback(IntPtr system, IntPtr path, IntPtr mode);
    private readonly Func<string, byte[]> _readFile;
    private readonly Dictionary<IntPtr, Cursor> _files = [];
    private readonly OpenCallback _open;
    private readonly AiFileCloseProc _close;
    private readonly AiFileReadProc _read;
    private readonly AiFileWriteProc _write;
    private readonly AiFileTellProc _tell;
    private readonly AiFileTellProc _size;
    private readonly AiFileSeek _seek;
    private readonly AiFileFlushProc _flush;
    public IntPtr Pointer { get; private set; }

    public Utf8ModelIO(Func<string, byte[]> readFile)
    {
        _readFile = readFile;
        _open = Open;
        _close = (_, file) => Close(file);
        _read = Read;
        _write = (_, _, _, _) => UIntPtr.Zero;
        _tell = file => _files.TryGetValue(file, out var cursor) ? new UIntPtr((uint)cursor.Position) : UIntPtr.Zero;
        _size = file => _files.TryGetValue(file, out var cursor) ? new UIntPtr((uint)cursor.Data.Length) : UIntPtr.Zero;
        _seek = Seek;
        _flush = _ => { };
        var system = new AiFileIO { OpenProc = Marshal.GetFunctionPointerForDelegate(_open),
            CloseProc = Marshal.GetFunctionPointerForDelegate(_close), UserData = IntPtr.Zero };
        Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AiFileIO>());
        Marshal.StructureToPtr(system, Pointer, false);
    }

    private IntPtr Open(IntPtr system, IntPtr path, IntPtr mode)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (_files.Count >= 512 || Marshal.PtrToStringUTF8(mode) is not ("r" or "rb" or "rt"))
                return IntPtr.Zero;
            var bytes = _readFile(Marshal.PtrToStringUTF8(path) ?? throw new InvalidDataException("Missing model path."));
            var file = new AiFile {
                ReadProc = Marshal.GetFunctionPointerForDelegate(_read), WriteProc = Marshal.GetFunctionPointerForDelegate(_write),
                TellProc = Marshal.GetFunctionPointerForDelegate(_tell), FileSizeProc = Marshal.GetFunctionPointerForDelegate(_size),
                SeekProc = Marshal.GetFunctionPointerForDelegate(_seek), FlushProc = Marshal.GetFunctionPointerForDelegate(_flush),
                UserData = IntPtr.Zero };
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AiFile>());
            Marshal.StructureToPtr(file, pointer, false);
            _files.Add(pointer, new Cursor(bytes));
            return pointer;
        }
        catch (Exception)
        {
            if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
            return IntPtr.Zero;
        }
    }

    private UIntPtr Read(IntPtr file, IntPtr buffer, UIntPtr size, UIntPtr count)
    {
        try
        {
            if (!_files.TryGetValue(file, out var cursor) || size == UIntPtr.Zero || buffer == IntPtr.Zero)
                return UIntPtr.Zero;
            var elements = Math.Min(count.ToUInt64(), (ulong)(cursor.Data.Length - cursor.Position) / size.ToUInt64());
            var bytes = checked((int)(elements * size.ToUInt64()));
            Marshal.Copy(cursor.Data, cursor.Position, buffer, bytes);
            cursor.Position += bytes;
            return new UIntPtr(elements);
        }
        catch (Exception) { return UIntPtr.Zero; }
    }

    private ReturnCode Seek(IntPtr file, UIntPtr offset, Origin origin)
    {
        try
        {
            if (!_files.TryGetValue(file, out var cursor)) return ReturnCode.Failure;
            var signed = unchecked((long)offset.ToUInt64());
            var position = origin switch { Origin.Set => signed, Origin.Current => checked(cursor.Position + signed),
                Origin.End => checked(cursor.Data.Length + signed), _ => -1 };
            if (position < 0 || position > cursor.Data.Length) return ReturnCode.Failure;
            cursor.Position = (int)position;
            return ReturnCode.Success;
        }
        catch (Exception) { return ReturnCode.Failure; }
    }

    private void Close(IntPtr file)
    {
        if (_files.Remove(file)) Marshal.FreeHGlobal(file);
    }

    public void Dispose()
    {
        foreach (var file in _files.Keys.ToArray()) Close(file);
        if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
        GC.KeepAlive(_open); GC.KeepAlive(_close); GC.KeepAlive(_read); GC.KeepAlive(_write);
        GC.KeepAlive(_tell); GC.KeepAlive(_size); GC.KeepAlive(_seek); GC.KeepAlive(_flush);
    }

    private sealed class Cursor(byte[] data)
    {
        public byte[] Data { get; } = data;
        public int Position { get; set; }
    }
}
