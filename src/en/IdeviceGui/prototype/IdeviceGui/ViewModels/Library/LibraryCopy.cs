namespace IdeviceGui.ViewModels.Library;

/// <summary>
/// 资料库右键"Copy…"的取值逻辑（2026-11-01 用户要求）。
/// <para>纯函数，方便单测；组行走 <see cref="LibraryRow.GroupBundleId"/>，文件行取各自字段。
/// 空白值统一返回 null（界面提示"There is nothing to copy…"，不把空串写进剪贴板）。</para>
/// </summary>
internal static class LibraryCopy
{
    public static string? BundleId(LibraryRow? row) =>
        row is null ? null : NullIfBlank(row.IsGroup ? row.GroupBundleId : row.BundleId);

    public static string? Name(LibraryRow? row) =>
        row is null ? null : NullIfBlank(row.IsGroup ? row.NameText : row.Name);

    public static string? Version(LibraryRow? row) =>
        row is null ? null : NullIfBlank(row.VersionText);

    public static string? MinOs(LibraryRow? row) =>
        row is null ? null : NullIfBlank(row.MinOsText);

    public static string? FileName(LibraryRow? row) =>
        row is null || row.IsGroup ? null : NullIfBlank(row.FileName);

    public static string? FilePath(LibraryRow? row) =>
        row is null || row.IsGroup ? null : NullIfBlank(row.FilePath);

    /// <summary>整行（Tab 分隔）：名称 / BundleId / 版本 / 最低 iOS / 解压后 / 原始大小 / 架构 / 设备族 / 状态 / 路径（组行路径为空）。</summary>
    public static string Row(LibraryRow? row)
    {
        if (row is null) return "";

        return string.Join("\t",
            Name(row) ?? "",
            BundleId(row) ?? "",
            Version(row) ?? "",
            MinOs(row) ?? "",
            row.AppSizeText,
            row.FileSizeText,
            row.Arch,
            row.DeviceFamilyText,
            row.StateText,
            FilePath(row) ?? "");
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
