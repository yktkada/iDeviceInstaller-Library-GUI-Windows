using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace IdeviceGui.Services.Parsing;

/// <summary>
/// 适配证据的存储结构（2026-11-04 M5-lite）：写入 `Packages.LaunchImageJson`。
/// 只存判定需要的像素尺寸，不存文件名/路径（体积小、JSON 稳定）。
/// </summary>
internal sealed class LaunchEvidenceDto
{
    public int V { get; set; } = 1;
    public List<int[]> L { get; set; } = new();   // 启动图实际像素尺寸（宽,高）
    public bool S { get; set; }                   // UILaunchStoryboardName 存在
    public List<int> I { get; set; } = new();     // 图标实际边长（方形才记）
}

internal static class LaunchEvidenceCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize(IEnumerable<(int Width, int Height)> launchSizes, bool storyboard,
        IEnumerable<int> iconSides)
    {
        var sizes = launchSizes.Distinct().Select(s => new[] { s.Width, s.Height }).ToList();
        var icons = iconSides.Distinct().OrderBy(x => x).ToList();
        if (sizes.Count == 0 && !storyboard && icons.Count == 0) return "";
        return JsonSerializer.Serialize(new LaunchEvidenceDto { L = sizes, S = storyboard, I = icons }, Options);
    }

    public static LaunchEvidenceDto? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<LaunchEvidenceDto>(json, Options);
            return dto is null ? null : new LaunchEvidenceDto
            {
                V = dto.V,
                L = dto.L ?? new List<int[]>(),
                S = dto.S,
                I = dto.I ?? new List<int>(),
            };
        }
        catch
        {
            return null;
        }
    }
}
