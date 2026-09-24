using Alas.Vision;

namespace Alas.Navigation;

/// <summary>导航需要的设备能力（截图、点击、返回键，便于用假设备做离线测试）。</summary>
public interface INavigationDevice
{
    /// <summary>让宿主拿到当前帧。像素是否跨语言边界由实现决定（见 DeviceController.CaptureForHost）。</summary>
    void Capture();
    void Click(int x, int y);
    /// <summary>返回键（KEYCODE_BACK = 4）：未建模画面的自救手段。</summary>
    void Back();
}

/// <summary>把真实设备接到导航器上。</summary>
public sealed class DeviceNavigationAdapter : INavigationDevice
{
    private readonly Device.DeviceController _device;

    public DeviceNavigationAdapter(Device.DeviceController device) => _device = device;

    public void Capture() => _device.CaptureForHost();

    public void Click(int x, int y) => _device.Click(x, y);

    public void Back() => _device.Back();
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

    /// <summary>
    /// 一次导航里允许几次"未建模画面自救"（按返回键退出浮层）。
    ///
    /// 为什么必须有：上游的页面图只覆盖 53 个 Page，而游戏里到处是**不在图里的浮层**
    /// （角色详情、个人信息、舰队编辑…）。实测：在船坞长按舰船卡片会进角色详情页，
    /// 此时没有任何页面规则命中，导航器若只会"报错退出"就卡死了 —— 上游靠
    /// `ui_ensure`/`ui_additional` 那套兜底，我们这里用最朴素也最可靠的一招：按返回。
    /// </summary>
    public int UnmodeledRecoveryBudget { get; init; } = 2;

    /// <summary>取一帧并交给识图宿主，返回当前命中的页面集合。</summary>
    public PageCurrentResult Perceive()
    {
        _device.Capture();
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
        int recoveries = 0, attempts = 0;
        PageCurrentResult current = Perceive();
        // 用 while 而不是 for：自救（按返回）不消耗跳数预算，但要单独限次，
        // 否则一个"退回又被弹回"的浮层能把预算烧光。
        while (true)
        {
            if (++attempts > MaxHops + UnmodeledRecoveryBudget)
                return new NavigationResult(false, target, current.Hit, hops,
                    $"尝试次数超过上限（{MaxHops} 跳 + {UnmodeledRecoveryBudget} 次自救）");
            if (current.Hit.Contains(target))
                return new NavigationResult(true, target, current.Hit, hops, null);
            if (hops.Count >= MaxHops)
            {
                // **诊断后缀**：把"点了没反应/被弹回"与"真的走错路"区分开 —— 前者多半是入口未解锁。
                // 由来（2026-09-23 真机）：`page_guild` / `page_os` / `page_meowfficer` 三个页面走不到，
                // 而入口都在屏、模板分 0.99~1.00 —— 真相是**账号未解锁**（点击要么无反应，要么弹回主界面）。
                // 当时只有一句"超过最大跳数"，我为此先后错判成"素材不匹配"与"坐标错了"。
                // 把当时的观察自动写进失败原因，下次一眼能分出是哪一类（详解见 `docs/tasks.md` 的识别与导航边界）。
                var last = hops[^1];
                bool unchanged = last.ArrivedPages is not { Count: > 0 }
                                 || last.ArrivedPages.All(p => last.OnPages?.Contains(p) == true);
                string hint = unchanged
                    ? "（末次点击后画面未变：入口可能未解锁/不可用，见 docs/tasks.md 的识别与导航边界）"
                    : "";
                return new NavigationResult(false, target, current.Hit, hops,
                    $"超过最大跳数 {MaxHops} 仍未到达{hint}");
            }

            var reachable = current.Hit.Where(distance.ContainsKey).ToList();
            if (reachable.Count == 0)
            {
                if (recoveries < UnmodeledRecoveryBudget)
                {
                    recoveries++;
                    _device.Back();
                    Thread.Sleep(SettleMs);
                    current = Perceive();
                    hops.Add(new NavigationHop(0, Array.Empty<string>(), target,
                                               "<BACK 自救>", 0, 0, 0, current.Hit, true));
                    continue;
                }
                return new NavigationResult(false, target, current.Hit, hops,
                    $"当前画面没有任何已建模页面（hit={string.Join(",", current.Hit)}），"
                    + $"且自救 {recoveries} 次仍无效");
            }
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
            hops.Add(new NavigationHop(hops.Count + 1, new[] { bestFrom! }, target, bestButton,
                                       bestScore, point.Value.X, point.Value.Y,
                                       current.Hit, lowConfidence));
        }
    }
}
