using Alas.Vision;

namespace Alas.Navigation;

/// <summary>导航需要的设备能力（只用截图与点击，便于用假设备做离线测试）。</summary>
public interface INavigationDevice
{
    /// <summary>取一帧 PNG 字节（像素不跨语言边界，见 DeviceController）。</summary>
    byte[] Screenshot();
    void Click(int x, int y);
}

/// <summary>把真实设备接到导航器上。</summary>
public sealed class DeviceNavigationAdapter : INavigationDevice
{
    private readonly Device.DeviceController _device;

    public DeviceNavigationAdapter(Device.DeviceController device) => _device = device;

    public byte[] Screenshot() => _device.ScreenshotBytes();

    public void Click(int x, int y) => _device.Click(x, y);
}

/// <summary>
/// 上游页面导航图。**运行时向上游要**（`module.ui.page.Page.links`），不导出、不改写：
/// 上游改了连线，这里立刻跟着变，不存在产物漂移。
/// </summary>
public sealed class PageGraph
{
    private readonly Dictionary<string, List<PageLinkInfo>> _links = new(StringComparer.Ordinal);

    public int NodeCount { get; }
    public int EdgeCount { get; }
    public IReadOnlyList<string> Unmapped { get; }
    public IReadOnlyList<string> RoundtripBad { get; }

    private PageGraph(PageGraphResult raw)
    {
        NodeCount = raw.NodeCount;
        EdgeCount = raw.EdgeCount;
        Unmapped = raw.Unmapped;
        RoundtripBad = raw.RoundtripBad;
        foreach (var node in raw.Nodes)
            _links[node.Name] = node.Links;
    }

    /// <summary>加载并做健全性检查：资产 id 往返不一致的图不能用来点击（会点到别的按钮）。</summary>
    public static PageGraph Load(IVisionEngine vision)
    {
        var raw = vision.PageGraph();
        var graph = new PageGraph(raw);
        if (raw.RoundtripBad.Count > 0)
            throw new InvalidOperationException(
                $"页面图的资产 id 往返校验失败（{raw.RoundtripBad.Count} 个）：" +
                string.Join(", ", raw.RoundtripBad.Take(5)));
        return graph;
    }

    public IReadOnlyList<PageLinkInfo> LinksFrom(string page)
        => _links.TryGetValue(page, out var list) ? list : Array.Empty<PageLinkInfo>();

    public bool HasPage(string page) => _links.ContainsKey(page);

    /// <summary>
    /// 反向 BFS：每个页面到 <paramref name="destination"/> 的最少跳数。
    /// 上游用 A*（`Page.init_connection`），但边权全为 1，BFS 结果等价且不需要启发函数。
    /// 不可达的页面不出现在结果里 —— 调用方必须把"查不到距离"当成"走不到"，不能当 0。
    /// </summary>
    public Dictionary<string, int> DistancesTo(string destination)
    {
        var reverse = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (from, links) in _links)
        {
            foreach (var link in links)
            {
                if (!reverse.TryGetValue(link.To, out var list))
                    reverse[link.To] = list = new List<string>();
                list.Add(from);
            }
        }

        var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [destination] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(destination);
        while (queue.Count > 0)
        {
            string page = queue.Dequeue();
            if (!reverse.TryGetValue(page, out var parents)) continue;
            foreach (string parent in parents)
            {
                if (distance.ContainsKey(parent)) continue;
                distance[parent] = distance[page] + 1;
                queue.Enqueue(parent);
            }
        }
        return distance;
    }

    /// <summary>求一条最短路径（页面名序列），不含则返回 null。</summary>
    public List<string>? Path(string from, string to)
    {
        if (!HasPage(from) || !HasPage(to)) return null;
        if (from == to) return new List<string> { from };
        var previous = new Dictionary<string, string>(StringComparer.Ordinal) { [from] = from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            string page = queue.Dequeue();
            foreach (var link in LinksFrom(page))
            {
                if (previous.ContainsKey(link.To)) continue;
                previous[link.To] = page;
                if (link.To == to)
                {
                    var path = new List<string> { to };
                    for (string p = to; p != from; p = previous[p]) path.Insert(0, previous[p]);
                    return path;
                }
                queue.Enqueue(link.To);
            }
        }
        return null;
    }
}

/// <summary>一次导航的落点记录，用于事后核对"到底点了哪个资产、分数多少"。</summary>
public sealed record NavigationHop(
    int Hop, IReadOnlyList<string> OnPages, string Target, string? Button, double Score,
    int ClickX, int ClickY, IReadOnlyList<string> ArrivedPages, bool LowConfidence);

public sealed record NavigationResult(
    bool Success, string Target, IReadOnlyList<string> FinalPages,
    IReadOnlyList<NavigationHop> Hops, string? Failure);

/// <summary>
/// 页面导航器：沿上游页面图逐跳点击，直到目标页真的在屏幕上出现。
///
/// 与上游 `UI.ui_goto` 的关系：同样用"perceive → click → 再 perceive"的循环，但
/// 选边规则针对本机实测做了两处必要的收紧：
///
///  1. **按实测分择优，而不是拿边的顺序碰运气。** 新版主界面下 `page_main` 与
///     `page_main_white` 会同时命中，两套出边的资产分差距极大（旧版 ≤0.25、白版 ≥0.94）。
///     点旧坐标只会点到空气，所以候选边取"当前命中页的全部出边"再按分数选最高。
///  2. **只走能缩短 BFS 距离的边。** 页面图里存在同层的横跳（如 shop 与 munitions
///     互为子页），盲目横跳可能在两页之间来回。距离变小的边才允许点。
///
/// 阈值 <see cref="MinScore"/> 是必需的：宁可判定"这一跳没有可用按钮"并失败，
/// 也不能在低分位置盲点（历史上误点出过别的页面）。
/// </summary>
public sealed class PageNavigator
{
    private readonly IVisionEngine _vision;
    private readonly INavigationDevice _device;
    private readonly PageGraph _graph;

    public PageNavigator(IVisionEngine vision, INavigationDevice device, PageGraph graph)
    {
        _vision = vision;
        _device = device;
        _graph = graph;
    }

    /// <summary>
    /// 视为"按钮确实在屏上"的分数下限。达到它才按上游 appear+click 的语义点
    /// **匹配到的位置**（`Button.button` 匹配后即 `_button_offset`）。
    /// </summary>
    public double ConfirmedScore { get; init; } = 0.85;

    /// <summary>
    /// 连资产坐标都不点的最低分（默认不限，与上游一致）。
    ///
    /// 上游 `UI.ui_goto` 的语义是：**只校验"当前页的 check 按钮出现"，然后直接点
    /// `page.links[page.parent]`，不校验那个按钮自身是否出现**；而且它每轮都
    /// `GOTO_MAIN.clear_offset()`，正是为了清掉别处 appear 留下的偏移、点回标称坐标。
    /// 本机实测印证了这一点：`ui/RESHMENU_GOTO_META` 只有 0.0941 分（新版界面换了皮），
    /// 但点它的标称坐标确实能进 Meta 页。早期版本在这里设 0.85 的硬闸门，
    /// 反而把上游本来能走通的一跳判成失败 —— 闸门只能用来决定"点匹配点还是点标称点"。
    /// </summary>
    public double MinClickScore { get; init; } = 0.0;

    /// <summary>每跳点击后等待画面稳定的时间（毫秒）。真机实测页面切换约 1~2 秒。</summary>
    public int SettleMs { get; init; } = 2500;

    public int MaxHops { get; init; } = 8;

    /// <summary>取一帧并交给识图宿主，返回当前命中的页面集合。</summary>
    public PageCurrentResult Perceive()
    {
        byte[] frame = _device.Screenshot();
        _vision.SetScreenshot(frame, "navigation");
        return _vision.PageCurrent();
    }

    public NavigationResult Goto(string target)
    {
        if (!_graph.HasPage(target))
            return new NavigationResult(false, target, Array.Empty<string>(),
                Array.Empty<NavigationHop>(), $"页面图里没有 {target}");
        var distance = _graph.DistancesTo(target);
        // 反向 BFS 只包含"能走到 target"的页面，所以能走到这里的页面集合本身就是判据：
        // 当前画面若全都不在 distance 里，就是真的走不到（而不是距离 0）。

        var hops = new List<NavigationHop>();
        PageCurrentResult current = Perceive();
        for (int hop = 1; hop <= MaxHops; hop++)
        {
            if (current.Hit.Contains(target))
                return new NavigationResult(true, target, current.Hit, hops, null);

            var reachable = current.Hit.Where(distance.ContainsKey).ToList();
            if (reachable.Count == 0)
                return new NavigationResult(false, target, current.Hit, hops,
                    $"当前画面没有任何已建模页面（hit={string.Join(",", current.Hit)}）");
            int here = reachable.Min(p => distance[p]);

            // 候选边：从当前所有命中页出发、且严格缩短到目标距离的边。
            // 每条边还要评估它的各个界面版本候选（旧版/白版），按实测分择优 ——
            // 只按边的原始资产打分会在新版主界面上选到 0.2 分的空气按钮。
            string? bestButton = null;
            double bestScore = -1;
            string? bestFrom = null, bestTo = null;
            foreach (string page in reachable)
            {
                foreach (var link in _graph.LinksFrom(page))
                {
                    if (!distance.TryGetValue(link.To, out int d) || d >= here) continue;
                    var variants = link.Variants.Count > 0 ? link.Variants : new List<string> { link.Button };
                    foreach (string candidate in variants)
                    {
                        var match = _vision.ButtonMatch(candidate, probeScore: true);
                        double score = match.Score ?? -1;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestButton = candidate;
                            bestFrom = page;
                            bestTo = link.To;
                        }
                    }
                }
            }
            bool lowConfidence = false;
            if (bestButton is null || bestScore < MinClickScore)
                return new NavigationResult(false, target, current.Hit, hops,
                    $"无可用出边：最高分 {(bestButton is null ? 0 : bestScore):F4}" +
                    (bestButton is null ? "（没有指向目标的边）" : $"（{bestButton}）"));
            lowConfidence = bestScore < ConfirmedScore;

            // 点击位置：分数够高说明按钮确实在屏上，点匹配到的实际位置；
            // 否则按上游 ui_goto 的语义点资产标称坐标（不要用低分下的匹配点 ——
            // 分数低时 minMaxLoc 的峰值是随机的，拿它当点击目标等于乱点）。
            var chosen = _vision.ButtonMatch(bestButton!, probeScore: true);
            (int X, int Y)? point = lowConfidence ? null : chosen.ClickPoint();
            if (point is null)
            {
                var center = _vision.AssetButtonCenter(bestButton!).Center;
                if (center.Count >= 2) point = (center[0], center[1]);
            }
            if (point is null)
                return new NavigationResult(false, target, current.Hit, hops,
                    $"{bestButton} 既没有匹配区域也没有标称坐标，无法点击");
            _device.Click(point.Value.X, point.Value.Y);
            Thread.Sleep(SettleMs);
            current = Perceive();
            hops.Add(new NavigationHop(hop, new[] { bestFrom! }, target, bestButton,
                                       bestScore, point.Value.X, point.Value.Y,
                                       current.Hit, lowConfidence));
        }
        return new NavigationResult(false, target, current.Hit, hops,
            $"超过最大跳数 {MaxHops} 仍未到达");
    }
}
