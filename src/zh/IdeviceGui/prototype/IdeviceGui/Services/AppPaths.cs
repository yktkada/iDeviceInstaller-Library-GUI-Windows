using System.IO;

namespace IdeviceGui.Services;

/// <summary>
/// 程序目录 / 日志 / 数据目录的唯一出口（硬约束 #7：只写程序目录，严禁写用户目录）。
/// <para>
/// 发布版：`data` 与 `logs` 都在 exe 同级。开发期可用环境变量 <c>IDEVICEGUI_DATA</c>
/// 把**数据**目录指到项目内（避免 `dotnet clean` 删库，见 docs/10 §3.3）；
/// <b>日志目录始终跟着程序目录</b>，不跟随该变量——崩溃日志要能在"刚部署到新机器"时也找到。
/// </para>
/// </summary>
internal static class AppPaths
{
    public const string DataDirectoryEnvVar = "IDEVICEGUI_DATA";

    /// <summary>exe 所在目录（带结尾分隔符，AppContext.BaseDirectory 的语义）。</summary>
    public static string ProgramDirectory => AppContext.BaseDirectory;

    /// <summary><c>&lt;程序目录&gt;\logs</c></summary>
    public static string LogsDirectory => Path.Combine(ProgramDirectory, "logs");

    /// <summary>崩溃日志：<c>&lt;程序目录&gt;\logs\crash.log</c>（覆盖写，只留最后一次，见 08 §3.2）。</summary>
    public static string CrashLogPath => Path.Combine(LogsDirectory, "crash.log");

    /// <summary>
    /// 数据目录：开发期读 <c>IDEVICEGUI_DATA</c>，否则 <c>&lt;程序目录&gt;\data</c>。
    /// <b>不回退用户目录</b>（硬约束 #7）。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvVar);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(ProgramDirectory, "data")
                : overridden;
        }
    }

    /// <summary>
    /// SQLite 的临时文件目录：<c>&lt;data&gt;\tmp</c>。
    /// <para>
    /// **不能留给系统 TEMP**（那是用户目录，违反硬约束 #7）：SQLite 在大事务 / 大结果集时会建临时文件，
    /// 默认落在 <c>%TEMP%</c>；实测 10 000 行扫描时在严格沙箱下直接抛
    /// <c>SQLITE_CANTOPEN(14) unable to open database file</c>（踩坑 C27）。
    /// 启动时把它指到程序目录，并设环境变量 <c>SQLITE_TMPDIR</c>（SQLite 会优先读它）。
    /// </para>
    /// </summary>
    public static string TempDirectory => Path.Combine(DataDirectory, "tmp");

    /// <summary>
    /// 把**本进程**的临时目录指到程序目录（<c>&lt;data&gt;\tmp</c>），供 SQLite 建临时文件用。
    /// <para>
    /// 为什么改 <c>TEMP</c>/<c>TMP</c> 而不是只设 <c>SQLITE_TMPDIR</c>：SQLite 的 Windows 后端用
    /// <c>GetTempPathW</c>，它只认 TMP/TEMP（<c>SQLITE_TMPDIR</c> 是 unix 侧的口径）；
    /// 实测只设 SQLITE_TMPDIR **无效**（10k 行扫描仍 SQLITE_CANTOPEN）。三个一起设最稳。
    /// 调用时机：**打开数据库之前**（启动自检之后）。
    /// </para>
    /// </summary>
    public static bool UseProgramTempDirectory(out string problem)
    {
        problem = "";
        try
        {
            Directory.CreateDirectory(TempDirectory);
        }
        catch (Exception ex)
        {
            problem =
                "未能创建临时目录：" + TempDirectory + Environment.NewLine +
                "原因：" + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                "请将程序复制到可写位置后重试。";
            return false;
        }

        if (!TryWriteProbe(TempDirectory, out var tempError))
        {
            problem =
                "未能写入临时目录：" + TempDirectory + Environment.NewLine +
                "原因：" + tempError + Environment.NewLine + Environment.NewLine +
                "请将程序复制到可写位置后重试。";
            return false;
        }

        Environment.SetEnvironmentVariable("SQLITE_TMPDIR", TempDirectory);
        Environment.SetEnvironmentVariable("TMP", TempDirectory);
        Environment.SetEnvironmentVariable("TEMP", TempDirectory);
        return true;
    }

    /// <summary>
    /// 启动自检：程序目录必须可写，否则提示用户并把程序放到可写目录，**不静默回退**（docs/10 §3.3）。
    /// 返回 false 时 <paramref name="problem"/> 是给用户看的中文说明。
    /// </summary>
    public static bool EnsureWritable(out string problem)
    {
        problem = "";

        if (!TryWriteProbe(ProgramDirectory, out var programError))
        {
            problem =
                "由于不能写入程序目录，数据与日志未能保存。" + Environment.NewLine + Environment.NewLine +
                "目录：" + ProgramDirectory + Environment.NewLine +
                "原因：" + programError + Environment.NewLine + Environment.NewLine +
                "请将程序复制到可写位置后重试。" +
                Environment.NewLine + "仅读写程序目录。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(LogsDirectory);
        }
        catch (Exception ex)
        {
            problem =
                "未能创建日志目录：" + LogsDirectory + Environment.NewLine +
                "原因：" + ex.Message + Environment.NewLine + Environment.NewLine +
                "请将程序复制到可写位置后重试。";
            return false;
        }

        if (!TryWriteProbe(LogsDirectory, out var logError))
        {
            problem =
                "日志目录不可写：" + LogsDirectory + Environment.NewLine +
                "原因：" + logError + Environment.NewLine + Environment.NewLine +
                "请将程序复制到可写位置后重试。";
            return false;
        }

        return true;
    }

    /// <summary>在目标目录里创建再删除一个探针文件，用真实 IO 判断可写（比读 ACL 可靠）。</summary>
    private static bool TryWriteProbe(string directory, out string error)
    {
        error = "";
        var probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0x42);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch
            {
                // 探针清理失败不重要
            }

            return false;
        }
    }
}
