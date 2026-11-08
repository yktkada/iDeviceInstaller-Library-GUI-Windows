using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IdeviceGui.Services.Scanning;

/// <summary>文件系统身份 = <c>(VolumeSerial, FileIdHigh, FileIdLow)</c>（docs/03 §3.2）。</summary>
internal readonly record struct FileIdentity(ulong VolumeSerial, ulong FileIdHigh, ulong FileIdLow);

/// <summary>
/// 读文件身份：<c>CreateFileW(access = 0, share = Read|Write|Delete)</c> +
/// <c>GetFileInformationByHandleEx(FileIdInfo)</c>。
/// <para>
/// **只申请元数据访问（access=0）**，不要读写权限——实测（2026-10-29，`踩坑记录.md` C01）：
/// 申请访问权限的写法在受限环境下会失败，而只取元数据可以成功；共享模式全开也不会挡住其它进程。
/// </para>
/// </summary>
internal static class FileIdentityReader
{
    private const uint ShareAll = 0x1 | 0x2 | 0x4;          // FILE_SHARE_READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const int FileIdInfoClass = 0x12;                // FileIdInfo

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;   // FILE_ID_128 的前 8 字节
        public ulong FileIdHigh;  // 后 8 字节
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        uint dwBufferSize);

    /// <summary>取不到身份（网络盘 / 不支持的文件系统）时返回 false，调用方退化为路径身份。</summary>
    public static bool TryRead(string path, out FileIdentity identity)
    {
        identity = default;
        try
        {
            using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
                return false;
            identity = new FileIdentity(info.VolumeSerialNumber, info.FileIdHigh, info.FileIdLow);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>路径归一：绝对路径 + 全小写（Windows 不区分大小写，docs/03 §4）。</summary>
internal static class ScanPaths
{
    public static string Normalize(string path) => Path.GetFullPath(path).ToLowerInvariant();
}

/// <summary>流式 SHA1（不整文件读进内存）。实测 87 MB / 170 ms ≈ 512 MB/s（docs/03 §3.3.1）。</summary>
