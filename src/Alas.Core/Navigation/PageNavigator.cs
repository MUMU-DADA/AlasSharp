using Alas.Vision;

namespace Alas.Navigation;

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
