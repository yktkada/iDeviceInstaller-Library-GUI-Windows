using System.IO;
using System.Text;

namespace IdeviceGui.Services;

/// <summary>
/// 文件日志（docs/08 §3.1）：<c>&lt;程序目录&gt;\logs\app.log</c>，5 MB × 3 轮转，**只写程序目录**。
/// <para>
/// M2 简化：逐行写入（每行 Flush），不做"后台每 1 秒批量 Flush"——B4 日志面板会统一做批量，
/// 现在先保证"扫描 / 写库错误一定留得下来"，避免两套缓冲各写一遍。
/// </para>
/// <para>打开失败（目录不可写 / 被杀软锁）不抛异常：记录 <see cref="OpenError"/> 并自动关闭文件日志，不影响程序。</para>
/// </summary>
internal sealed class FileLog : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxArchives = 2;   // app.log + app.1.log + app.2.log

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly bool _enabled;
    private StreamWriter? _writer;
    private bool _disabled;

    public string FilePath => _filePath;
    public string? OpenError { get; private set; }

    public FileLog(string logDirectory, bool enabled)
    {
        _filePath = Path.Combine(logDirectory, "app.log");
        _enabled = enabled;
        if (!enabled) return;

        try
        {
            Directory.CreateDirectory(logDirectory);
            _writer = Open();
        }
        catch (Exception ex)
        {
            OpenError = ex.GetType().Name + ": " + ex.Message;
            _disabled = true;
        }
    }

    public void Write(string line)
    {
        if (!_enabled || _disabled) return;
        lock (_gate)
        {
            if (_disabled) return;
            try
            {
                _writer ??= Open();
                _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
                _writer.Flush();
                if (_writer.BaseStream.Length > MaxBytes) Rotate();
            }
            catch (Exception ex)
            {
                // 写入失败：关闭文件日志，不让它影响主流程（docs/08 §3.1）
                OpenError ??= ex.GetType().Name + ": " + ex.Message;
                _disabled = true;
                try { _writer?.Dispose(); } catch { /* 忽略 */ }
                _writer = null;
            }
        }
    }

    private StreamWriter Open()
    {
        var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
    }

    private string ArchivePath(int index) => index == 0
        ? _filePath
        : Path.Combine(Path.GetDirectoryName(_filePath)!,
            Path.GetFileNameWithoutExtension(_filePath) + "." + index + ".log");

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        // app.(n-1).log → app.n.log，最老的丢掉
        try { if (File.Exists(ArchivePath(MaxArchives))) File.Delete(ArchivePath(MaxArchives)); }
        catch { /* 忽略 */ }

        for (var index = MaxArchives - 1; index >= 1; index--)
        {
            try
            {
                if (File.Exists(ArchivePath(index))) File.Move(ArchivePath(index), ArchivePath(index + 1), overwrite: true);
            }
            catch { /* 忽略 */ }
        }

        try
        {
            if (File.Exists(ArchivePath(0))) File.Move(ArchivePath(0), ArchivePath(1), overwrite: true);
        }
        catch { /* 忽略 */ }

        _writer = Open();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Flush(); } catch { /* 忽略 */ }
            try { _writer?.Dispose(); } catch { /* 忽略 */ }
            _writer = null;
            _disabled = true;
        }
    }
}
