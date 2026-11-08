using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AfcHelper;

/// <summary>clean 的执行结果。警告不致命（07 §3.2：清理失败不阻断安装）。</summary>
internal readonly record struct CleanSummary(int Removed, IReadOnlyList<string> Warnings);

/// <summary>
/// 一次 AFC 会话：device → lockdown 握手 → com.apple.afc 服务 → afc 客户端。
/// 所有 native 句柄只在 <see cref="Dispose"/> 释放，连接中途失败也会把已拿到的句柄交回去。
/// 路径纪律（总览 §6.4）：AFC 内只用 /PublicStaging 正斜杠路径，绝不删该目录以外的东西。
/// </summary>
internal sealed class AfcSession : IDisposable
{
    /// <summary>AFC 根是 /var/mobile/Media，设备上的 /private/var/... 绝对路径在这里不可用。</summary>
    private const string StagingPath = "/PublicStaging";
    private const string StagingPrefix = StagingPath + "/";
    private const string AfcServiceName = "com.apple.afc";
    private const string ClientLabel = "AfcHelper";

    /// <summary>AFC_E_OBJECT_NOT_FOUND：目录/对象不存在。残留清理里属正常。</summary>
    private const int AfcObjectNotFound = 8;

    /// <summary>目录递归的深度上限，防病态目录结构把 helper 拖死。</summary>
    private const int MaxDepth = 32;

    /// <summary>native 返回的字符串数组/字典的条目上限，防脏指针读飞。</summary>
    private const int MaxArrayEntries = 8192;

    private IntPtr _device;
    private IntPtr _afc;
    private bool _disposed;

    private AfcSession()
    {
    }

    public static bool TryOpen(string udid, [NotNullWhen(true)] out AfcSession? session, out string error)
    {
        session = new AfcSession();
        if (session.Open(udid, out error)) return true;
        session.Dispose();
        session = null;
        return false;
    }

    private bool Open(string udid, out string error)
    {
        error = "";
        var rc = WithUtf8(udid, p => Native.idevice_new_with_options(out _device, p, Native.LookupUsbmux));
        if (rc != 0)
        {
            error = $"连接设备失败（idevice_new_with_options={rc}）";
            return false;
        }

        IntPtr lockdown = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        try
        {
            rc = WithUtf8(ClientLabel, p => Native.lockdownd_client_new_with_handshake(_device, out lockdown, p));
            if (rc != 0)
            {
                error = $"lockdown 握手失败（{rc}）";
                return false;
            }

            rc = WithUtf8(AfcServiceName, p => Native.lockdownd_start_service(lockdown, p, out service));
            if (rc != 0)
            {
                error = $"启动 AFC 服务失败（{rc}）";
                return false;
            }

            rc = Native.afc_client_new(_device, service, out _afc);
            if (rc != 0)
            {
                error = $"创建 AFC 客户端失败（{rc}）";
                return false;
            }

            return true;
        }
        finally
        {
            // 服务描述符与 lockdown 客户端用完即还；afc 客户端自己持有底层连接。
            if (service != IntPtr.Zero) Native.lockdownd_service_descriptor_free(service);
            if (lockdown != IntPtr.Zero) Native.lockdownd_client_free(lockdown);
        }
    }

    /// <summary>
    /// 清空 /PublicStaging（07 §3.2）：目录不存在（错误码 8）算正常；
    /// 删除单项失败只记 warning，不中断；AFC 完全连不上由 <see cref="TryOpen"/> 报 unreachable。
    /// </summary>
    public CleanSummary CleanStaging()
    {
        var warnings = new List<string>();
        var infoRc = ReadDirectory(StagingPath, out var info);
        if (infoRc != 0)
        {
            if (infoRc != AfcObjectNotFound)
                warnings.Add($"{StagingPath} 不可读（AFC 错误码 {infoRc}）");
            return new CleanSummary(0, warnings);
        }

        var entries = ReadStrings(info);
        Native.afc_dictionary_free(info);

        var removed = 0;
        foreach (var name in entries)
        {
            if (name is "." or "..") continue;
            if (!IsSafeEntryName(name))
            {
                warnings.Add($"跳过可疑条目名：{name}");
                continue;
            }

            var path = StagingPrefix + name;
            if (DeleteEntry(path, 0, warnings)) removed++;
            else warnings.Add($"删除失败：{path}");
        }

        return new CleanSummary(removed, warnings);
    }

    /// <summary>
    /// 读 Media 分区容量：优先 afc_get_device_info 的 FSTotalBytes / FSFreeBytes，
    /// 回退根目录 afc_get_file_info（老版本可能只在 st_size 里给总量）。
    /// </summary>
    public bool TryReadSpace(out long totalBytes, out long freeBytes, out string error)
    {
        totalBytes = 0;
        freeBytes = 0;
        error = "";

        if (TryReadSpaceFromDeviceInfo(out totalBytes, out freeBytes)) return true;

        // 回退：根目录信息（AFC 里 "/" 即 Media 分区根）。
        var rc = GetFileInfo("/", out var rootInfo);
        if (rc == 0)
        {
            var dict = ReadPairs(rootInfo);
            Native.afc_dictionary_free(rootInfo);
            if (TryGetByteCount(dict, "FSTotalBytes", out totalBytes) &&
                TryGetByteCount(dict, "FSFreeBytes", out freeBytes))
            {
                return true;
            }
        }

        error = rc == 0
            ? "AFC 未返回 FSFreeBytes/FSTotalBytes"
            : $"读取根目录信息失败（AFC 错误码 {rc}）";
        totalBytes = 0;
        freeBytes = 0;
        return false;
    }

    private bool TryReadSpaceFromDeviceInfo(out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;

        var rc = Native.afc_get_device_info(_afc, out var info);
        if (rc != 0) return false;

        var dict = ReadPairs(info);
        Native.afc_dictionary_free(info);

        return TryGetByteCount(dict, "FSTotalBytes", out totalBytes) &&
               TryGetByteCount(dict, "FSFreeBytes", out freeBytes);
    }

    /// <summary>
    /// 删除一项：符号链接只删链接本身（绝不进去删目标）；目录先递归清子项再删自己；
    /// 目录读取失败说明可能是普通文件，直接删。
    /// </summary>
    private bool DeleteEntry(string path, int depth, List<string> warnings)
    {
        if (!IsInsideStaging(path))
        {
            warnings.Add($"拒绝删除 /PublicStaging 以外的路径：{path}");
            return false;
        }

        if (depth > MaxDepth)
        {
            warnings.Add($"目录过深，跳过：{path}");
            return false;
        }

        if (IsSymbolicLink(path))
        {
            // 链接本身可以删，但绝不能跟随它删目标（目标可能在 /PublicStaging 之外）。
            return RemovePath(path) == 0;
        }

        var dirRc = ReadDirectory(path, out var dirInfo);
        if (dirRc == 0)
        {
            var children = ReadStrings(dirInfo);
            Native.afc_dictionary_free(dirInfo);
            foreach (var child in children)
            {
                if (child is "." or "..") continue;
                if (!IsSafeEntryName(child))
                {
                    warnings.Add($"跳过可疑条目名：{child}");
                    continue;
                }

                var childPath = path + "/" + child;
                if (!DeleteEntry(childPath, depth + 1, warnings))
                    warnings.Add($"删除失败：{childPath}");
            }
        }

        return RemovePath(path) == 0;
    }

    private bool IsSymbolicLink(string path)
    {
        var rc = GetFileInfo(path, out var info);
        if (rc != 0) return false;

        var dict = ReadPairs(info);
        Native.afc_dictionary_free(info);
        return dict.TryGetValue("st_ifmt", out var kind) &&
               string.Equals(kind, "S_IFLNK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>条目名必须是不含分隔符的普通名字，杜绝 ../ 与绝对路径注入。</summary>
    private static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name is "." or "..") return false;
        if (name.Contains('/') || name.Contains('\\')) return false;
        return true;
    }

    /// <summary>只允许 /PublicStaging 本身及其子项（正斜杠、逐级路径，不含 ".."）。</summary>
    private static bool IsInsideStaging(string path)
    {
        if (!path.StartsWith(StagingPrefix, StringComparison.Ordinal)) return false;
        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment is ".." or ".") return false;
        }
        return true;
    }

    private int ReadDirectory(string path, out IntPtr info)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return Native.afc_read_directory(_afc, ptr, out info);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private int GetFileInfo(string path, out IntPtr info)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return Native.afc_get_file_info(_afc, ptr, out info);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private int RemovePath(string path) =>
        WithUtf8(path, p => Native.afc_remove_path(_afc, p));

    // ── native 互操作小工具 ────────────────────────────────────────────────

    private static int WithUtf8(string text, Func<IntPtr, int> call)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return call(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>NULL 结尾的 char** → 字符串列表（目录项、扁平化字典都长这样）。</summary>
    private static List<string> ReadStrings(IntPtr array)
    {
        var list = new List<string>();
        if (array == IntPtr.Zero) return list;

        for (var i = 0; i < MaxArrayEntries; i++)
        {
            var item = Marshal.ReadIntPtr(array, i * IntPtr.Size);
            if (item == IntPtr.Zero) break;
            list.Add(Marshal.PtrToStringUTF8(item) ?? "");
        }

        return list;
    }

    /// <summary>NULL 结尾的 char** 是交替的 key/value，转成字典。</summary>
    private static Dictionary<string, string> ReadPairs(IntPtr array)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flat = ReadStrings(array);
        for (var i = 0; i + 1 < flat.Count; i += 2)
            dict[flat[i]] = flat[i + 1];
        return dict;
    }

    private static bool TryGetByteCount(IReadOnlyDictionary<string, string> dict, string key, out long value)
    {
        value = 0;
        return dict.TryGetValue(key, out var text) &&
               long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_afc != IntPtr.Zero)
        {
            Native.afc_client_free(_afc);
            _afc = IntPtr.Zero;
        }
        if (_device != IntPtr.Zero)
        {
            Native.idevice_free(_device);
            _device = IntPtr.Zero;
        }
    }
}
