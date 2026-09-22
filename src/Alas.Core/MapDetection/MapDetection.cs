using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Vision;

namespace Alas.MapDetection;

/// <summary>
/// S2（地图识别）的 C# 侧模型。
///
/// **架构铁律：图像算法一行都不在 C# 里。** 单应性、Hough 直线、瓦片匹配、颜色统计
/// 全部由上游 <c>module/map_detection</c>（以及大世界的 <c>module/os/globe_detection</c>）
/// 在 CPython 宿主里执行，这里只承载结果：
///   - <see cref="MapDetectionAssets"/>：素材链的形状（UI 遮罩/瓦片/检测区域）
///   - <see cref="MapDetectResult"/>：一次战役地图检测的结果（可能"未检测到"）
///   - <see cref="GlobeDetectResult"/>：大世界单应性与坐标往返
/// </summary>
public sealed class MapDetectionAssets
{
    [JsonPropertyName("detecting_area")] public List<int> DetectingArea { get; set; } = new();
    [JsonPropertyName("ui_mask")] public List<int>? UiMask { get; set; }
    [JsonPropertyName("ui_mask_os")] public List<int>? UiMaskOs { get; set; }
    [JsonPropertyName("ui_mask_stroke")] public List<int>? UiMaskStroke { get; set; }
    [JsonPropertyName("ui_mask_in_map")] public List<int>? UiMaskInMap { get; set; }
    [JsonPropertyName("ui_mask_os_in_map")] public List<int>? UiMaskOsInMap { get; set; }
    [JsonPropertyName("tile_center_image")] public List<int>? TileCenter { get; set; }
    [JsonPropertyName("tile_corner_image")] public List<int>? TileCorner { get; set; }
}

/// <summary>
/// 一次地图检测的结果。<see cref="Detected"/> 为 false 是**正常结果**（当前画面不是地图），
/// <see cref="Reason"/> 保留上游给的原始原因，便于区分"画面不对"与"接线不对"。
/// </summary>
public sealed class MapDetectResult
{
    [JsonPropertyName("backend")] public string? Backend { get; set; }
    [JsonPropertyName("load")] public string? Load { get; set; }
    [JsonPropertyName("predict")] public string? Predict { get; set; }
    [JsonPropertyName("detected")] public bool Detected { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("grid_count")] public int? GridCount { get; set; }
    [JsonPropertyName("shape")] public List<int>? Shape { get; set; }
    [JsonPropertyName("center_loca")] public List<int>? CenterLoca { get; set; }
    [JsonPropertyName("left_edge")] public bool? LeftEdge { get; set; }
    [JsonPropertyName("right_edge")] public bool? RightEdge { get; set; }
    [JsonPropertyName("upper_edge")] public bool? UpperEdge { get; set; }
    [JsonPropertyName("lower_edge")] public bool? LowerEdge { get; set; }
}

/// <summary>
/// 大世界（OS）地图识别结果：单应矩阵 + "屏幕点 → 大世界坐标 → 回屏幕"的往返。
/// 往返误差应当≈0（同一变换的逆），这是不依赖真机地图的数值自检。
/// </summary>
public sealed class GlobeDetectResult
{
    [JsonPropertyName("load")] public string? Load { get; set; }
    [JsonPropertyName("homo_size")] public List<int>? HomoSize { get; set; }
    [JsonPropertyName("homo_data")] public List<List<double>>? HomoData { get; set; }
    [JsonPropertyName("screen2globe")] public List<List<double>>? ScreenToGlobe { get; set; }
    [JsonPropertyName("globe2screen")] public List<List<double>>? GlobeToScreen { get; set; }
    [JsonPropertyName("roundtrip_error")] public string? RoundtripError { get; set; }
}

/// <summary>S2 客户端：把请求发给宿主，拿回上游的识别结果。</summary>
public sealed class MapDetectionClient
{
    private readonly IVisionEngine _vision;

    public MapDetectionClient(IVisionEngine vision) => _vision = vision;

    /// <summary>素材链自检：素材读不出来时立刻暴露，而不是等真机跑地图才炸。</summary>
    public MapDetectionAssets Assets()
        => _vision.CallTyped<MapDetectionAssets>("map_detection_assets");

    /// <summary>战役地图识别（当前画面）。未检测到属于正常结果，看 <see cref="MapDetectResult.Reason"/>。</summary>
    public MapDetectResult DetectMap() => _vision.CallTyped<MapDetectResult>("map_detect");

    /// <summary>大世界地图识别（当前画面），返回单应性与坐标往返。</summary>
    public GlobeDetectResult DetectGlobe(IEnumerable<(int X, int Y)>? points = null)
    {
        var args = new JsonObject();
        if (points is not null)
        {
            var arr = new JsonArray();
            foreach (var (x, y) in points)
                arr.Add(new JsonArray { x, y });
            args["points"] = arr;
        }
        return _vision.CallTyped<GlobeDetectResult>("globe_detect",
            args.Count == 0 ? null : args);
    }
}
