using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace 거래플랜.Shared.Contracts;

public static class InstallRootPathIdentity
{
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = NormalizeLexicalPath(path);
        var missingSegments = new Stack<string>();
        var existingPath = fullPath;
        FileAttributes existingAttributes;

        while (!TryGetAttributes(existingPath, out existingAttributes))
        {
            var leafName = Path.GetFileName(existingPath);
            var parentPath = Path.GetDirectoryName(existingPath);
            if (string.IsNullOrEmpty(leafName) ||
                string.IsNullOrEmpty(parentPath) ||
                string.Equals(
                    existingPath,
                    parentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"경로의 기존 상위 디렉터리를 확인하지 못했습니다: {fullPath}");
            }

            missingSegments.Push(leafName);
            existingPath = NormalizeLexicalPath(parentPath);
        }

        if (missingSegments.Count > 0 &&
            (existingAttributes & FileAttributes.Directory) == 0)
        {
            throw new IOException(
                $"경로의 기존 상위 항목이 디렉터리가 아닙니다: {existingPath}");
        }

        AssertExistingAncestorsAreNotReparsePoints(existingPath);
        var resolvedExistingPath = GetFinalExistingPath(existingPath);
        AssertExistingAncestorsAreNotReparsePoints(resolvedExistingPath);

        var resolvedPath = resolvedExistingPath;
        while (missingSegments.Count > 0)
            resolvedPath = Path.Combine(resolvedPath, missingSegments.Pop());

        return NormalizeLexicalPath(resolvedPath);
    }

    public static bool PathsOverlap(string left, string right)
    {
        var leftPath = Resolve(left);
        var rightPath = Resolve(right);
        if (string.Equals(
                leftPath,
                rightPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPrefix = leftPath + Path.DirectorySeparatorChar;
        var rightPrefix = rightPath + Path.DirectorySeparatorChar;
        return leftPath.StartsWith(
                   rightPrefix,
                   StringComparison.OrdinalIgnoreCase) ||
               rightPath.StartsWith(
                   leftPrefix,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLexicalPath(string path)
    {
        var fullPath = RemoveExtendedPathPrefix(Path.GetFullPath(path))
            .Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void AssertExistingAncestorsAreNotReparsePoints(
        string existingPath)
    {
        var currentPath = existingPath;
        while (!string.IsNullOrEmpty(currentPath))
        {
            var attributes = File.GetAttributes(currentPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"설치 경로가 재분석 지점을 통과합니다: {currentPath}");
            }

            var parentPath = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(parentPath) ||
                string.Equals(
                    currentPath,
                    parentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            currentPath = parentPath;
        }
    }

    private static string GetFinalExistingPath(string existingPath)
    {
        if (!OperatingSystem.IsWindows())
            return existingPath;

        const uint fileShareRead = 0x00000001;
        const uint fileShareWrite = 0x00000002;
        const uint fileShareDelete = 0x00000004;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFile(
            fileName: existingPath,
            desiredAccess: 0,
            shareMode:
                fileShareRead | fileShareWrite | fileShareDelete,
            securityAttributes: IntPtr.Zero,
            creationDisposition: openExisting,
            flagsAndAttributes: fileFlagBackupSemantics,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"기존 경로의 final path handle을 열지 못했습니다: {existingPath}");
        }

        var buffer = new StringBuilder(512);
        var result = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Capacity));
        if (result == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"기존 경로의 final path를 확인하지 못했습니다: {existingPath}");
        }

        if (result >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)result + 1));
            result = GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Capacity));
            if (result == 0 || result >= buffer.Capacity)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"기존 경로의 final path를 확인하지 못했습니다: {existingPath}");
            }
        }

        return RemoveExtendedPathPrefix(buffer.ToString());
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(
                extendedUncPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(
            extendedPrefix,
            StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags = 0);
}
