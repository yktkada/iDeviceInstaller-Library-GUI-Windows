using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace IdeviceIconHelper;

/// <summary>
/// 设备 App 图标 helper 原型（只读设备）：
/// 一个进程连一台设备，按 BundleId 取 SpringBoard 图标 PNG。
/// 实测约束：同一个 sbservices client 连续取约 19 个后服务会被断开（-3），
/// 因此每 10 个图标重建一次 client，遇到 -3 立即重连并重试当前 BundleId。
/// </summary>
internal static class Program
{
    private const int Success = 0;
    private const int ConnFailed = -3;
    private const int BatchSize = 10;

    private static int Main(string[] args)
    {
        if (!TryParseArgs(args, out var udid, out var bundles, out var outDir, out var jsonPath, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("用法: IdeviceIconHelper --udid <udid> --bundles-file <txt> --out <dir> [--json <path>]");
            return 2;
        }

        if (bundles.Count == 0)
        {
            Console.Error.WriteLine("BundleId 列表为空");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        var results = new List<IconResult>(bundles.Count);

        var rc = IdeviceNative.idevice_new(out var device, udid);
        if (rc != 0 || device == IntPtr.Zero)
        {
            Console.Error.WriteLine($"idevice_new 失败: rc={rc}");
            return 1;
        }

        var client = IntPtr.Zero;
        var used = 0;
        try
        {
            foreach (var bundleId in bundles)
            {
                results.Add(FetchOne(device, ref client, ref used, bundleId, outDir));
            }
        }
        finally
        {
            if (client != IntPtr.Zero) IdeviceNative.sbservices_client_free(client);
            IdeviceNative.idevice_free(device);
        }

        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath, json);
        }
        else
        {
            Console.WriteLine(json);
        }

        var ok = results.Count(x => x.Rc == Success);
        Console.Error.WriteLine($"完成: {ok}/{results.Count} 成功");
        return 0;
    }

    private static IconResult FetchOne(IntPtr device, ref IntPtr client, ref int used, string bundleId, string outDir)
    {
        var total = Stopwatch.StartNew();
        var attempts = 0;
        var lastRc = -1;

        while (attempts < 3)
        {
            attempts++;
            if (client == IntPtr.Zero)
            {
                var startRc = IdeviceNative.sbservices_client_start_service(device, out client, "idevice-icon-helper");
                if (startRc != 0 || client == IntPtr.Zero)
                {
                    lastRc = startRc;
                    total.Stop();
                    return new IconResult(bundleId, lastRc, 0, null, null, 0, (long)total.Elapsed.TotalMilliseconds, attempts, null);
                }
                used = 0;
            }

            var call = Stopwatch.StartNew();
            var rc = IdeviceNative.sbservices_get_icon_pngdata(client, bundleId, out var pngData, out var pngSize);
            call.Stop();
            lastRc = rc;

            if (rc == Success && pngData != IntPtr.Zero && pngSize > 0)
            {
                var bytes = new byte[checked((int)Math.Min(pngSize, int.MaxValue))];
                Marshal.Copy(pngData, bytes, 0, bytes.Length);
                IdeviceNative.free(pngData);
                var (width, height) = ReadPngSize(bytes);
                var path = Path.Combine(outDir, SafeName(bundleId) + ".png");
                File.WriteAllBytes(path, bytes);
                used++;
                if (used >= BatchSize)
                {
                    IdeviceNative.sbservices_client_free(client);
                    client = IntPtr.Zero;
                }
                total.Stop();
                return new IconResult(bundleId, rc, bytes.Length, width, height, (long)call.Elapsed.TotalMilliseconds, (long)total.Elapsed.TotalMilliseconds, attempts, path);
            }

            if (rc == ConnFailed)
            {
                IdeviceNative.sbservices_client_free(client);
                client = IntPtr.Zero;
                continue;   // 重建 client 并重试当前 BundleId
            }

            // -256 等：该 App 没有可返回的图标，不重试
            used++;
            if (used >= BatchSize)
            {
                IdeviceNative.sbservices_client_free(client);
                client = IntPtr.Zero;
            }
            break;
        }

        total.Stop();
        return new IconResult(bundleId, lastRc, 0, null, null, 0, (long)total.Elapsed.TotalMilliseconds, attempts, null);
    }

    private static string SafeName(string bundleId)
        => bundleId.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    private static (int? Width, int? Height) ReadPngSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24) return (null, null);
        if (!data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return (null, null);
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return (null, null);
        var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        return width > int.MaxValue || height > int.MaxValue ? (null, null) : ((int)width, (int)height);
    }

    private static bool TryParseArgs(
        string[] args,
        out string udid,
        out List<string> bundles,
        out string outDir,
        out string? jsonPath,
        out string error)
    {
        udid = "";
        bundles = [];
        outDir = "";
        jsonPath = null;
        error = "";

        string? bundlesFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (i + 1 >= args.Length) { error = $"缺少参数值: {key}"; return false; }
            var value = args[++i];
            switch (key)
            {
                case "--udid": udid = value; break;
                case "--bundles-file": bundlesFile = value; break;
                case "--out": outDir = value; break;
                case "--json": jsonPath = value; break;
                default: error = $"未知参数: {key}"; return false;
            }
        }

        if (string.IsNullOrWhiteSpace(udid) || string.IsNullOrWhiteSpace(outDir) || bundlesFile is null)
        {
            error = "必须提供 --udid / --bundles-file / --out";
            return false;
        }

        if (!File.Exists(bundlesFile))
        {
            error = $"BundleId 文件不存在: {bundlesFile}";
            return false;
        }

        foreach (var line in File.ReadLines(bundlesFile))
        {
            var id = line.Trim();
            if (id.Length > 0 && !id.StartsWith('#')) bundles.Add(id);
        }

        return true;
    }
}

internal sealed record IconResult(
    string BundleId,
    int Rc,
    int Bytes,
    int? Width,
    int? Height,
    long ElapsedMs,
    long TotalMs,
    int Attempts,
    string? Path);

internal static class IdeviceNative
{
    [DllImport("libimobiledevice-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int idevice_new(out IntPtr device, string? udid);

    [DllImport("libimobiledevice-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int idevice_free(IntPtr device);

    [DllImport("libimobiledevice-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sbservices_client_start_service(IntPtr device, out IntPtr client, string label);

    [DllImport("libimobiledevice-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sbservices_client_free(IntPtr client);

    [DllImport("libimobiledevice-1.0.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sbservices_get_icon_pngdata(IntPtr client, string bundleId, out IntPtr pngData, out ulong pngSize);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void free(IntPtr ptr);
}
