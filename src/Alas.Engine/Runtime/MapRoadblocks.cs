using System.Numerics;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record MapRoadblockPlan(bool Reachable, IReadOnlyList<Cell> Enemies);

/// <summary>Fleet.brute_find_roadblocks without exponential products or temporary mutation of observed enemies.</summary>
public static class MapRoadblocks
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;

    public static MapRoadblockPlan Find(CampaignState state, Cell destination, int fleet, CancellationToken token = default)
    {
        if (fleet is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(fleet));
        Cell start = (fleet == 1 ? state.Fleet1Location : state.Fleet2Location)
            ?? throw new InvalidOperationException("Roadblock search requires a known fleet location");
        _ = state[destination];
        var links = state.Paths.Connections;
        var enemies = state.Cells.Where(cell => cell.IsEnemy).ToArray();
        var ranks = enemies.Select((cell, index) => (cell.Location, index)).ToDictionary(pair => pair.Location, pair => pair.index);
        // Native product enumeration first minimizes removal count, then the ordered subset of map cells.
        // Every simple path has a reward below unit, so one additional removal always costs more.
        // Positive enemy costs rule out beneficial cycles; zero-cost sea ties need no predecessor update.
        BigInteger unit = BigInteger.One << enemies.Length;
        var distance = new Dictionary<Cell, BigInteger> { [start] = BigInteger.Zero };
        var previous = new Dictionary<Cell, Cell>();
        var pending = new PriorityQueue<Cell, (BigInteger Cost, int Index)>();
        pending.Enqueue(start, (BigInteger.Zero, state.Map.IndexOf(start)));
        while (pending.TryDequeue(out var current, out var priority))
        {
            token.ThrowIfCancellationRequested();
            if (distance[current] != priority.Cost) continue;
            if (current == destination)
            {
                var removed = new HashSet<Cell>();
                for (var cell = destination; previous.TryGetValue(cell, out var parent); cell = parent)
                    if (cell != destination && ranks.ContainsKey(cell)) removed.Add(cell);
                return new(true, enemies.Where(enemy => removed.Contains(enemy.Location)).Select(enemy => enemy.Location).ToArray());
            }
            foreach (var next in links[current])
            {
                var cell = state[next];
                if (cell.IsLand || cell.IsMechanismBlock) continue;
                // Native changes is_enemy only. A siren, fortress or boss cannot become transit sea.
                if (next != destination && (cell.IsSiren || cell.IsFortress || cell.IsBoss)) continue;
                BigInteger cost = priority.Cost;
                if (next != destination && next != start && ranks.TryGetValue(next, out int rank))
                    cost += unit - (BigInteger.One << (enemies.Length - rank - 1));
                if (distance.TryGetValue(next, out var known) && known <= cost) continue;
                distance[next] = cost; previous[next] = current;
                pending.Enqueue(next, (cost, state.Map.IndexOf(next)));
            }
        }
        return new(false, []);
    }
}
