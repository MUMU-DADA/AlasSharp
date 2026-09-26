using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>The upstream destination fallback is preserved, but never implies reachability.</summary>
public sealed record MapRoute(bool IsReachable, IReadOnlyList<Cell>? FullPath, IReadOnlyList<Cell> Waypoints);

/// <summary>Map topology, positive movement costs and route nodes over this sortie's authoritative state.</summary>
public sealed class MapPathfinder(CampaignState state)
{
    public static readonly SourceFile Source = new("module/map/map_base.py", "72e0d294061d70a9ba950dd0ef3749bf427fc0914e52ba85ff6fc474144d68ed");
    public const int Unreachable = 9999;
    private FrozenDictionary<Cell, ImmutableArray<Cell>>? _connections;
    public IReadOnlyDictionary<Cell, ImmutableArray<Cell>> Connections => _connections ?? throw new InvalidOperationException("Map connections are not initialized");

    public void InitializeConnections(bool walls = false, bool portals = false)
    {
        var links = state.Cells.ToDictionary(g => g.Location, g => state.Covered(g.Location,
            [(0, -1), (0, 1), (-1, 0), (1, 0)]).Select(n => n.Location).ToHashSet());
        if (walls)
            foreach (var edge in state.Map.Walls)
            {
                links[edge.From].Remove(edge.To);
                links[edge.To].Remove(edge.From);
            }
        foreach (var portal in state.Map.Portals)
        {
            if (portals) links[portal.From].Add(portal.To);
            // Native disabling also removes a portal's ordinary adjacent edge in that direction.
            else links[portal.From].Remove(portal.To);
            state[portal.From].IsPortal = portals;
            state[portal.From].PortalLink = portals ? portal.To : null;
        }
        _connections = links.ToFrozenDictionary(p => p.Key, p => p.Value.OrderBy(state.Map.IndexOf).ToImmutableArray());
    }

    public void ComputeCosts(Cell start, bool hasAmbush = true, bool hasEnemy = true)
    {
        _ = Connections;
        var origin = state[start];
        foreach (var grid in state.Cells) { grid.Cost = Unreachable; grid.Connection = null; }
        origin.Cost = 0;
        // Native stops when the visited set stops growing, before weighted relaxation can finish.
        // Dijkstra preserves entry costs/blockers while ensuring every stored cost matches its route.
        var frontier = new PriorityQueue<Cell, (int Cost, int Index)>();
        frontier.Enqueue(start, (0, state.Map.IndexOf(start)));
        while (frontier.TryDequeue(out var location, out var priority))
        {
            var grid = state[location];
            if (grid.Cost != priority.Cost) continue;
            foreach (var adjacent in Connections[location])
            {
                var next = state[adjacent];
                if (next.IsLand || next.IsMechanismBlock) continue;
                int cost = grid.Cost + (next.MayAmbush && hasAmbush ? 10 : 1);
                if (cost < next.Cost)
                {
                    next.Cost = cost;
                    next.Connection = location;
                    if (next.IsSea || !hasEnemy) frontier.Enqueue(adjacent, (cost, state.Map.IndexOf(adjacent)));
                }
                else if (cost == next.Cost && cost < Unreachable && Math.Abs(adjacent.Column - location.Column) == 1)
                    next.Connection = location;
            }
        }
    }

    public void ComputeFleetCosts(IReadOnlyList<KeyValuePair<int, Cell?>> fleets, Cell current, bool hasAmbush)
    {
        if (fleets.Any(p => p.Key is not (1 or 2)) || fleets.Select(p => p.Key).Distinct().Count() != fleets.Count)
            throw new ArgumentException("Fleet cost fields require unique fleet indices 1 or 2", nameof(fleets));
        _ = state[current];
        // Stable sort: non-current fleets first; leave the current fleet's general field active.
        foreach (var pair in fleets.OrderBy(p => p.Value == current))
        {
            if (pair.Value is not { } start) continue;
            ComputeCosts(start, hasAmbush);
            foreach (var grid in state.Cells)
            {
                if (pair.Key == 1) grid.Cost1 = grid.Cost;
                else grid.Cost2 = grid.Cost;
            }
        }
    }

    public IReadOnlyList<Cell>? FullPath(Cell destination)
    {
        var grid = state[destination];
        if (grid.Cost == 0) return [destination];
        if (grid.Connection is null) return null;
        var path = new List<Cell> { destination };
        var seen = new HashSet<Cell> { destination };
        while (grid.Connection is { } previous)
        {
            if (!seen.Add(previous)) throw new InvalidDataException("Map predecessor chain contains a cycle");
            grid = state[previous];
            path.Add(previous);
        }
        path.Reverse();
        return path;
    }

    public MapRoute FindRoute(Cell destination, int step = 0, bool turningOptimize = false)
    {
        if (step < 0) throw new ArgumentOutOfRangeException(nameof(step));
        var path = FullPath(destination);
        if (path is null || path.Count == 0) return new(false, path, [destination]);
        var indexes = new List<int> { 0 };
        for (int i = 0; i < path.Count - 1; i++)
        {
            var grid = state[path[i]];
            if (grid.IsPortal && grid.PortalLink == path[i + 1]) { indexes.Add(i); indexes.Add(i + 1); }
            if (grid.IsMaze && i != 0) indexes.Add(i);
        }
        if (!indexes.Contains(path.Count)) indexes.Add(path.Count);
        var nodes = new List<Cell>();
        for (int i = 0; i < indexes.Count - 1; i++)
        {
            int start = indexes[i], end = indexes[i + 1];
            if (end - start == 1 && state[path[start]].IsPortal && state[path[start]].PortalLink == path[end]) continue;
            var local = path.Skip(start).Take(end - start + 1).ToArray();
            nodes.AddRange(RouteNodes(local, step, turningOptimize));
        }
        return new(state[destination].IsAccessible, path, nodes);
    }

    public IReadOnlyList<Cell> RouteNodes(IReadOnlyList<Cell> route, int step = 0, bool turningOptimize = false)
    {
        if (step < 0) throw new ArgumentOutOfRangeException(nameof(step));
        if (route.Count == 0) throw new ArgumentException("Route nodes require a nonempty path", nameof(route));
        foreach (var cell in route) _ = state[cell];
        var nodes = new List<int>();
        if (turningOptimize)
        {
            var turns = new List<int>();
            for (int i = 1; i < route.Count - 1; i++)
                if (Math.Abs(route[i + 1].Column - route[i].Column) - Math.Abs(route[i].Column - route[i - 1].Column) == -1)
                    turns.Add(i);
            foreach (int index in turns)
            {
                if (!state[route[index]].IsFleet) nodes.Add(index);
                else
                {
                    if (index > 1 && !turns.Contains(index - 1)) nodes.Add(index - 1);
                    if (index < route.Count - 2 && !turns.Contains(index + 1)) nodes.Add(index + 1);
                }
            }
            nodes.Add(route.Count - 1);
            if (step == 0) return nodes.Select(i => route[i]).ToArray();
        }
        else
        {
            if (step == 0) return [route[^1]];
            nodes.Add(route.Count - 1);
        }
        nodes.Insert(0, 0);
        var inserted = new List<int>();
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            int left = nodes[i], right = nodes[i + 1];
            for (long position = (long)left + step; position < right; position += step)
            {
                int index = (int)position;
                var grid = state[route[index]];
                if (grid.IsFleet || grid.IsPortal || grid.IsFlare)
                {
                    if (index > 1 && !nodes.Contains(index - 1)) inserted.Add(index - 1);
                    if (index < route.Count - 2 && !nodes.Contains(index + 1)) inserted.Add(index + 1);
                }
                else inserted.Add(index);
            }
            inserted.Add(right);
        }
        return inserted.Select(i => route[i]).ToArray();
    }
}
