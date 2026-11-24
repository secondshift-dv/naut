using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Neuterradise.Release.Contracts;

/// <summary>Resolves local update roots by handle rather than drive-letter or short-name spelling.
/// Existing reparse components are rejected, including ancestors of a not-yet-created root.</summary>
public static class UpdatePathGuard
{
    public static string Resolve(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new IOException("Update path has no volume root.");
        if (!OperatingSystem.IsWindows())
            return full;
        var current = root;
        string? physical = null;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = -1; i < parts.Length; i++)
        {
            if (i >= 0) current = Path.Combine(current, parts[i]);
            using var handle = CreateFile(current, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (i >= 0 && physical is not null && error is 2 or 3)
                    return Path.TrimEndingDirectorySeparator(Path.Combine(physical, Path.Combine(parts[i..])));
                throw new IOException("Update path identity could not be opened.", new Win32Exception(error));
            }
            if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8))
                throw new IOException("Update path attributes could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((attributes.Attributes & 0x400) != 0)
                throw new IOException("Update path contains a reparse component.");
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("Update path physical identity is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
            physical = buffer.ToString();
        }
        return Path.TrimEndingDirectorySeparator(physical!);
    }

    public static void RequireDisjoint(params string[] paths)
    {
        var resolved = paths.Select(Resolve).ToArray();
        for (var i = 0; i < resolved.Length; i++)
            for (var j = i + 1; j < resolved.Length; j++)
                if (Contains(resolved[i], resolved[j]) || Contains(resolved[j], resolved[i]))
                    throw new IOException("Update roots overlap physically.");
    }

    /// <summary>Rename a non-reparse directory through its open handle. Ancestors are held
    /// without delete sharing, and an existing destination is never replaced.</summary>
    public static void MoveSibling(string source, string destination)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(source) ?? throw new IOException("Source has no parent.");
        if (!string.Equals(parent, Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update rename must remain within one directory.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Update replacement requires Windows handle authority.");
        var handles = new List<SafeFileHandle>();
        try
        {
            var root = Path.GetPathRoot(parent)!;
            var current = root;
            handles.Add(OpenPinned(current, 0x80));
            foreach (var part in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                handles.Add(OpenPinned(current, 0x80));
            }
            using var sourceHandle = OpenPinned(source, 0x10080);
            if (Path.Exists(destination))
                throw new IOException("Update rename destination already exists.");
            RenameHandle(sourceHandle, destination, replace: false);
        }
        finally
        {
            for (var i = handles.Count - 1; i >= 0; i--)
                handles[i].Dispose();
        }
    }

    public static IDisposable PinDirectory(string path, bool create = false)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new IOException("Update directory has no volume root.");
        var pins = new DirectoryPins();
        try
        {
            var current = root;
            pins.Add(OpenPinned(current, 0x80));
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                pins.Add(OpenPinned(current, 0x80));
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        public void Add(SafeFileHandle handle) => _handles.Add(handle);
        public void Dispose()
        {
            for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
            _handles.Clear();
        }
    }

    internal static SafeFileHandle OpenRegularFile(string path, uint access, uint share, uint disposition,
        uint additionalFlags = 0)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, disposition, 0x02200000 | additionalFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Update file could not be opened.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8)
            || (attributes.Attributes & 0x410) != 0)
        {
            handle.Dispose();
            throw new IOException("Update file must be regular and non-reparse.");
        }
        return handle;
    }

    internal static FileStream OpenReadFile(string path) =>
        new(OpenRegularFile(path, 0x80000000, 1, 3), FileAccess.Read);

    public static void CopyNewFile(string source, string destination)
    {
        using var sourcePins = PinDirectory(Path.GetDirectoryName(Path.GetFullPath(source))!);
        using var destinationPins = PinDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var input = OpenReadFile(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    /// <summary>Copy into a newly created tree without following or overwriting filesystem entries.</summary>
    public static void CopyNewTree(string source, string destination, CancellationToken cancellationToken = default)
    {
        RequireDisjoint(source, destination);
        using var sourcePins = PinDirectory(source);
        using var destinationPins = PinDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        CopyDirectory(source, destination);

        void CopyDirectory(string from, string to)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sourceDirectory = OpenPinned(from, 0x80);
            if (!CreateDirectory(to, IntPtr.Zero))
                throw new IOException("Update staging directory must be newly created.", new Win32Exception(Marshal.GetLastWin32Error()));
            using var destinationDirectory = OpenPinned(to, 0x80);
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(to, Path.GetFileName(entry));
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Update payload contains a reparse entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                    CopyDirectory(entry, target);
                else
                {
                    using var input = OpenReadFile(entry);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.WriteThrough);
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
            }
        }
    }

    /// <summary>Flush journal content and replace its name through the same pinned file handle.</summary>
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        using var pins = PinDirectory(Path.GetDirectoryName(path)!, create: true);
        var temp = path + ".tmp";
        // Production writers hold the operation lease. Unlink stale temporary evidence
        // without opening the existing file with truncating access.
        File.Delete(temp);
        using var handle = OpenRegularFile(temp, 0x40010000, 1, 1, 0x80000000);
        await using var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        RenameHandle(handle, path, replace: true);
        stream.Flush(flushToDisk: true);
    }

    private static void RenameHandle(SafeFileHandle handle, string destination, bool replace)
    {
        var name = Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var size = checked(nameOffset + name.Length + sizeof(char));
        var info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, info, size);
            Marshal.WriteByte(info, replace ? (byte)1 : (byte)0);
            Marshal.WriteInt32(info, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, info, (uint)size))
                throw new IOException("Update rename failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>Deletes only entries opened without reparse traversal, holding ancestor directory handles throughout.</summary>
    public static void DeleteTree(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Update cleanup requires Windows handle authority.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full) ?? throw new IOException("Cannot delete a volume root.");
        var root = Path.GetPathRoot(parent)!;
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = root;
            handles.Add(OpenPinned(current, 0x80));
            foreach (var part in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Directory.Exists(current)) { Resolve(current); return; }
                handles.Add(OpenPinned(current, 0x80));
            }
            DeleteEntry(full, true, cancellationToken);
        }
        finally
        {
            for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        }
    }

    private static void DeleteEntry(string path, bool root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = CreateFile(path, 0x10080, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return;
            throw new IOException("Update cleanup entry could not be opened.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8))
            throw new IOException("Update cleanup entry attributes could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        var directory = (attributes.Attributes & 0x10) != 0;
        var reparse = (attributes.Attributes & 0x400) != 0;
        if (root && (!directory || reparse)) throw new IOException("Update cleanup root is not a physical directory.");
        if (directory && !reparse)
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                DeleteEntry(child, false, cancellationToken);
        var disposition = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(disposition, 1);
            if (!SetFileInformationByHandle(handle, 4, disposition, 4))
                throw new IOException("Update cleanup entry could not be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(disposition); }
    }

    private static SafeFileHandle OpenPinned(string path, uint access)
    {
        var handle = CreateFile(path, access | 1u, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Update directory could not be pinned.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8)
            || (attributes.Attributes & 0x410) != 0x10)
        {
            handle.Dispose();
            throw new IOException("Update rename requires a physical non-reparse directory.");
        }
        return handle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
        IntPtr info, uint size);

    private static bool Contains(string ancestor, string child) =>
        string.Equals(ancestor, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTag info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
