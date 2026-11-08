namespace IdeviceGui.Services.Parsing;

/// <summary>
/// IPA 来源（2026-11-05 用户定，只保留三类）：
/// AppStore 原始包 / 重签名包 / 解密包。
/// 判定证据：`embedded.mobileprovision` 存在 → 重签；否则 `SC_Info/` 存在 → App Store 原始包；
/// 否则 → 解密包。
/// </summary>
internal static class PackageOrigin
{
    public const string Original = "original";
    public const string Resigned = "resigned";
    public const string Decrypted = "decrypted";
    /// <summary>组行专用：成员来源不一致（不是文件来源分类）。</summary>
    public const string Mixed = "mixed";

    public static string FromEntries(bool hasEmbeddedProvision, bool hasScInfo)
        => hasEmbeddedProvision ? Resigned : hasScInfo ? Original : Decrypted;

    public static string DisplayText(string? kind) => kind switch
    {
        Original or "appstore" => "Original App Store",   // appstore 兼容 2026-11-05 旧库
        Resigned => "Re-signed",
        Decrypted => "Decrypted",
        Mixed => "Multiple",
        _ => "",
    };
}
