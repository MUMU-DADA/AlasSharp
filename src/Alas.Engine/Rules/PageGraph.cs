using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Alas.Engine.Rules;

public sealed record PageEdge(string Destination, AssetRule Button);
public sealed record PageRule(string Id, AssetRule? Check, ImmutableArray<PageEdge> Links)
{
    public bool IsIsland => Id == "page_island" || Id.StartsWith("page_island_", StringComparison.Ordinal);
}

public sealed class PageGraph
{
    public ImmutableArray<PageRule> Pages { get; }
    private readonly FrozenDictionary<string, PageRule> _byId;
    public PageGraph(IEnumerable<PageRule> pages)
    {
        Pages = pages.ToImmutableArray();
        _byId = Pages.ToFrozenDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var page in Pages)
        {
            if (page.Links.Select(edge => edge.Destination).Distinct(StringComparer.Ordinal).Count() != page.Links.Length)
                throw new ArgumentException("Duplicate page link");
            foreach (var edge in page.Links)
                if (!_byId.ContainsKey(edge.Destination)) throw new ArgumentException("Page link points outside the graph");
        }
    }
    public PageRule this[string id] => _byId.TryGetValue(id, out var page)
        ? page : throw new NotSupportedException($"Page rule is not implemented: {id}");

    /// <summary>Reverse unweighted search from Page.init_connection, without shared mutable parent fields.</summary>
    public IReadOnlyDictionary<string, PageEdge> RoutesTo(string destination)
    {
        _ = this[destination];
        var routes = new Dictionary<string, PageEdge>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { destination };
        var frontier = new Queue<string>();
        frontier.Enqueue(destination);
        while (frontier.TryDequeue(out string? next))
            foreach (var page in Pages)
            {
                if (visited.Contains(page.Id)) continue;
                var edge = page.Links.FirstOrDefault(link => link.Destination == next);
                if (edge is null) continue;
                routes.Add(page.Id, edge);
                visited.Add(page.Id);
                frontier.Enqueue(page.Id);
            }
        return routes.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

internal sealed class PageGraphBuilder
{
    private readonly List<(string Id, AssetRule? Check)> _pages = [];
    private readonly Dictionary<string, Dictionary<string, AssetRule>> _links = new(StringComparer.Ordinal);
    public string Page(string id, AssetRule? check)
    {
        _pages.Add((id, check));
        _links.Add(id, new(StringComparer.Ordinal));
        return id;
    }
    public void Link(string source, AssetRule button, string destination) => _links[source][destination] = button;
    public PageGraph Build() => new(_pages.Select(page => new PageRule(page.Id, page.Check,
        _links[page.Id].Select(link => new PageEdge(link.Key, link.Value)).ToImmutableArray())));
}
