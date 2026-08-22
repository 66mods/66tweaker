using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Tweaker.Domain.Legacy;

namespace Tweaker.LegacyImporter;

internal static class Program
{
    private const long MaximumFixtureBytes = 1_048_576;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private static readonly string[] FixturePaths =
    [
        "66mods Tweaks v40012(RUN AS ADMIN).bat",
        "Fixes/Fix Disabled WiFi (RUN AS ADMIN).bat",
        "Fixes/Fix Fortnite Not Starting (RUN AS ADMIN).bat"
    ];

    private static int Main(string[] args)
    {
        if ((args.Length != 3 && args.Length != 4) || (!args[0].Equals("hash", StringComparison.Ordinal) && !args[0].Equals("draft", StringComparison.Ordinal) && !args[0].Equals("bundle", StringComparison.Ordinal)) || (args[0].Equals("bundle", StringComparison.Ordinal) && args.Length != 4))
        {
            Console.Error.WriteLine("Usage: Tweaker.LegacyImporter <hash|draft> <source-root> <output-path> OR bundle <source-root> <json-output> <cs-output>");
            return 2;
        }

        var sourceRoot = args[1];
        var outputPath = args[2];
        try
        {
            if (args[0].Equals("bundle", StringComparison.Ordinal))
            {
                LegacyBundleGenerator.Write(sourceRoot, outputPath, args[3]);
            }
            else if (args[0].Equals("hash", StringComparison.Ordinal))
            {
                WriteSourceHashes(sourceRoot, outputPath);
            }
            else
            {
                LegacyManifestWriter.WriteDraft(sourceRoot, outputPath);
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void WriteSourceHashes(string sourceRoot, string outputPath)
    {
        var root = Path.GetFullPath(sourceRoot);
        var canonicalRoot = ValidateExistingPathWithoutReparsePoints(root);

        var output = Path.GetFullPath(outputPath);
        EnsureContained(root, output, "Output path");
        var outputDirectory = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output path must have a directory.");
        var canonicalOutputDirectory = ValidateExistingPathWithoutReparsePoints(outputDirectory);
        EnsureContained(canonicalRoot, canonicalOutputDirectory, "Output path");

        var files = FixturePaths.Select(relativePath =>
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureContained(root, fullPath, "Fixture path");
            var hash = ComputeBoundedSha256(fullPath, canonicalRoot);
            return new SourceHash(relativePath, hash.Bytes, hash.Sha256);
        }).ToArray();

        var json = JsonSerializer.Serialize(new SourceHashDocument(files), new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(output, Encoding.UTF8.GetBytes(json + Environment.NewLine), canonicalRoot);
    }

    private static HashResult ComputeBoundedSha256(string path, string canonicalRoot)
    {
        using var checkedPath = OpenExistingPathWithoutFollowingReparsePoint(path);
        EnsureContained(canonicalRoot, checkedPath.CanonicalPath, "Fixture path");
        using var stream = checkedPath.OpenReadStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaximumFixtureBytes)
            {
                throw new InvalidDataException($"Fixture exceeds the {MaximumFixtureBytes}-byte limit: {path}");
            }

            hash.AppendData(buffer, 0, read);
        }

        return new HashResult(total, Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static string ReadBoundedLatin1Text(string path, string canonicalRoot)
    {
        var canonicalPath = ValidateExistingPathWithoutReparsePoints(path);
        EnsureContained(canonicalRoot, canonicalPath, "Fixture path");
        using var checkedPath = OpenExistingPathWithoutFollowingReparsePoint(path);
        EnsureContained(canonicalRoot, checkedPath.CanonicalPath, "Fixture path");
        using var stream = checkedPath.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true, bufferSize: 4_096, leaveOpen: false);
        var builder = new StringBuilder();
        var buffer = new char[4_096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (builder.Length > LegacyBatParser.MaximumInputCharacters - read)
            {
                throw new InvalidDataException("Frozen fixture exceeds the parser input limit.");
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }
    internal static void WriteAtomically(string output, byte[] content, string canonicalRoot)
    {
        var directory = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output path must have a directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            using (var checkedTemporary = OpenExistingPathWithoutFollowingReparsePoint(temporary))
            {
                EnsureContained(canonicalRoot, checkedTemporary.CanonicalPath, "Output path");
            }

            if (File.Exists(output))
            {
                using var checkedOutput = OpenExistingPathWithoutFollowingReparsePoint(output);
                EnsureContained(canonicalRoot, checkedOutput.CanonicalPath, "Output path");
                File.Replace(temporary, output, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, output);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static string ValidateExistingPathWithoutReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("Path must have a volume or UNC root.", nameof(path));
        var current = root;
        string? canonicalPath = null;

        foreach (var segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty))
        {
            if (segment.Length > 0)
            {
                current = Path.Combine(current, segment);
            }

            using var checkedPath = OpenExistingPathWithoutFollowingReparsePoint(current);
            canonicalPath = checkedPath.CanonicalPath;
        }

        return canonicalPath ?? throw new DirectoryNotFoundException($"Required path was not found: {path}");
    }

    private static CheckedPath OpenExistingPathWithoutFollowingReparsePoint(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new FileNotFoundException("Required path was not found.", path);
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Reparse points are not allowed: {path}");
            }

            return new CheckedPath(path, Path.GetFullPath(path), handle: null);
        }

        var handle = CreateFile(
            path,
            desiredAccess: GenericRead,
            shareMode: FileShareReadWriteDelete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: OpenExisting,
            flagsAndAttributes: FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Unable to open path without following reparse points: {path}", new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException($"Unable to inspect path attributes: {path}", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException($"Reparse points are not allowed: {path}");
            }

            return new CheckedPath(path, GetFinalPathName(handle, path), handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string GetFinalPathName(SafeFileHandle handle, string requestedPath)
    {
        var result = new StringBuilder(capacity: 32_768);
        var length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        if (length == 0 || length >= (uint)result.Capacity)
        {
            throw new IOException($"Unable to resolve the canonical path: {requestedPath}", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return result.ToString();
    }

    internal static void EnsureContained(string root, string candidate, string description)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedRoot = trimmedRoot + Path.DirectorySeparatorChar;
        if (!candidate.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{description} escapes the supplied source root.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class CheckedPath(string requestedPath, string canonicalPath, SafeFileHandle? handle) : IDisposable
    {
        public string CanonicalPath { get; } = canonicalPath;

        public FileStream OpenReadStream()
        {
            return handle is null
                ? new FileStream(requestedPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81_920, FileOptions.SequentialScan)
                : new FileStream(handle, FileAccess.Read, bufferSize: 81_920, isAsync: false);
        }

        public void Dispose()
        {
            handle?.Dispose();
        }
    }

    private sealed record HashResult(long Bytes, string Sha256);

    private sealed record SourceHash(string Path, long Bytes, string Sha256);

    private sealed record SourceHashDocument(IReadOnlyList<SourceHash> Files);
}