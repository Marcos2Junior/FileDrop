using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileDropMcp;

public static class PathGuard
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string LocalAbsolute(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && (value.Length < 3 || value[1] != ':' || value.AsSpan(2).Contains(':'))))
            throw new DeliveryException("path_not_allowed");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    public static bool Within(string path, string root) => path.Equals(root, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);

    public static void NoLinks(string value)
    {
        var full = LocalAbsolute(value);
        for (FileSystemInfo? item = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full); item is not null;
             item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
        {
            if (item.Exists && (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.LinkTarget is not null))
                throw new DeliveryException("path_not_allowed");
        }
    }

    public static FileStream OpenAuthorized(string path, McpOptions options)
    {
        var full = LocalAbsolute(path);
        if (!options.Roots.Any(root => Within(full, root))) throw new DeliveryException("path_not_allowed");
        NoLinks(full);
        if (!File.Exists(full)) throw new DeliveryException("file_not_found");
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        try
        {
            // Check the opened handle, as well as the requested path, before reading any content.
            if (OperatingSystem.IsWindows())
            {
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new DeliveryException("path_not_allowed");
                var actual = buffer.ToString();
                if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
                actual = LocalAbsolute(actual);
                if (!options.Roots.Any(root => Within(actual, root))) throw new DeliveryException("path_not_allowed");
                NoLinks(actual);
            }
            if (stream.Length is < 1 or > McpOptions.MaxFileBytes) throw new DeliveryException("file_size_invalid");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
