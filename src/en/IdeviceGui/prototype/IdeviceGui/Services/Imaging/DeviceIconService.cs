using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace IdeviceGui.Services.Imaging;

/// <summary>
/// 设备应用图标：调用独立 <c>IdeviceIconHelper.exe</c> 取 SpringBoard PNG。
/// 主程序不直接 P/Invoke libimobiledevice：设备卡死时只影响 helper 子进程（与 AfcHelper 同一模式）。
/// 缓存目录：<c>&lt;data&gt;\deviceicons\&lt;udid&gt;\&lt;BundleId&gt;.png</c>。
/// </summary>
internal static class DeviceIconService
{
    private const int DecodePixelWidth = 64;

    /// <summary>
    /// 已解码图标的进程内缓存：按 PNG 路径 + 文件最后写入时间命中。
    /// 设备应用视图来回切换时不再重新读盘/解码（2026-11-08 用户要求）。
    /// </summary>
    private sealed class CachedImage
    {
        public required DateTime LastWriteUtc { get; init; }
        public object? Image { get; init; }
    }

    private static readonly Dictionary<string, CachedImage> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ImageCacheGate = new();
    private const int ImageCacheLimit = 1024;

    public static string CacheDirectory(string udid)
        => Path.Combine(AppPaths.DataDirectory, "deviceicons", udid);

    /// <summary>
    /// 确保指定 BundleId 的图标已落盘，返回 BundleId → PNG 路径。
    /// helper 不存在/超时/失败时返回已有缓存（没有则空），不抛、不影响列表。
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> EnsureAsync(
        string udid, IReadOnlyList<string> bundleIds, CancellationToken ct)
    {
        var dir = CacheDirectory(udid);
        Directory.CreateDirectory(dir);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var bundleId in bundleIds)
        {
            var path = Path.Combine(dir, SafeName(bundleId) + ".png");
            if (File.Exists(path)) result[bundleId] = path;
            else missing.Add(bundleId);
        }

        if (missing.Count == 0) return result;

        var helper = Path.Combine(AppPaths.ProgramDirectory, "IdeviceIconHelper.exe");
        if (!File.Exists(helper)) return result;

        var listPath = Path.Combine(AppPaths.TempDirectory, "deviceicons-" + ShortId(udid) + ".txt");
        var jsonPath = Path.Combine(AppPaths.TempDirectory, "deviceicons-" + ShortId(udid) + ".json");
        try
        {
            Directory.CreateDirectory(AppPaths.TempDirectory);
            await File.WriteAllLinesAsync(listPath, missing, ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--udid");
            psi.ArgumentList.Add(udid);
            psi.ArgumentList.Add("--bundles-file");
            psi.ArgumentList.Add(listPath);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add(jsonPath);

            using var process = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                if (ct.IsCancellationRequested) throw;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // helper 失败：保留已有缓存，不阻塞列表
        }

        foreach (var bundleId in bundleIds)
        {
            var path = Path.Combine(dir, SafeName(bundleId) + ".png");
            if (File.Exists(path)) result[bundleId] = path;
        }

        return result;
    }

    /// <summary>解码并 Freeze；在后台线程调用，结果可跨线程绑定。相同文件+时间直接返回缓存。</summary>
    public static object? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lastWrite = File.GetLastWriteTimeUtc(path);
            lock (ImageCacheGate)
            {
                if (ImageCache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWrite)
                    return cached.Image;
            }

            object? image = null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = DecodePixelWidth;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }

            lock (ImageCacheGate)
            {
                if (ImageCache.Count >= ImageCacheLimit) ImageCache.Clear();
                ImageCache[path] = new CachedImage { LastWriteUtc = lastWrite, Image = image };
            }
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeName(string bundleId)
        => bundleId.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    private static string ShortId(string udid)
        => udid.Length <= 12 ? udid : udid[..12];
}
