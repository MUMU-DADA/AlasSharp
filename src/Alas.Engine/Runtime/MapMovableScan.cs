using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record MovableEnemySnapshot(ImmutableArray<Cell> Sirens, ImmutableArray<Cell> Enemies)
{
    public static MovableEnemySnapshot Capture(CampaignState state) => new(
        state.Cells.Where(g => g.IsSiren).Select(g => g.Location).ToImmutableArray(),
        state.Cells.Where(g => g.IsEnemy).Select(g => g.Location).ToImmutableArray());
}
public sealed record MovableTrackingEvidence(bool Siren, MovableEnemyMatch Match,
    ImmutableArray<Cell> Dropped, ImmutableArray<Cell> Predicted);
public sealed record MovableScanEvidence(int Round, bool AfterBattle, MovableEnemySnapshot Before,
    MapScanResult Scan, ImmutableArray<MovableTrackingEvidence> Tracking);

/// <summary>Native full_scan_movable / track_movable over the C# scanner and authoritative map.</summary>
public sealed class MapMovableScan(CampaignState state, CampaignConfiguration configuration, MapScanner scanner,
    bool hasEnemyTemplates = true)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    public async ValueTask<MovableScanEvidence> ScanAsync(MovableEnemySnapshot before, bool afterBattle,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (state.SpawnStack.IsEmpty) throw new InvalidOperationException("Movable tracking requires spawn declarations");
        foreach (var cell in (configuration.HasMovableEnemy ? before.Sirens : [])
            .Concat(configuration.HasMovableNormalEnemy ? before.Enemies : [])) state[cell].WipeOut();
        bool sirenOnly = !configuration.HasMovableNormalEnemy && configuration.HasMovableEnemy;
        var scan = await scanner.ScanAsync(state.Progress, TimeSpan.FromMinutes(2),
            queue: sirenOnly && !afterBattle ? before.Sirens : null,
            mustScan: sirenOnly ? before.Sirens : null, mode: MapScanMode.Movable,
            fleet: new FleetScanOptions(Fleet2Enabled: configuration.Fleet2 != 0), token: token);
        var tracking = ImmutableArray.CreateBuilder<MovableTrackingEvidence>();
        if (configuration.HasMovableEnemy) tracking.Add(Track(before.Sirens, afterBattle, siren: true, token));
        if (configuration.HasMovableNormalEnemy) tracking.Add(Track(before.Enemies, afterBattle, siren: false, token));
        state.RefreshFleetPaths(configuration);
        var evidence = new MovableScanEvidence(state.Rounds.Round, afterBattle, before, scan, tracking.ToImmutable());
        state.RecordMovableScan(evidence);
        return evidence;
    }

    public MovableTrackingEvidence Track(IReadOnlyList<Cell> before, bool afterBattle, bool siren,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Cell current = (state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location)
            ?? throw new InvalidOperationException("Movable tracking requires the current fleet location");
        var after = state.Cells.Where(g => siren ? g.IsSiren : g.IsEnemy).ToArray();
        var spawn = state.Cells.Where(g => siren ? g.MaySiren : g.MayEnemy).Select(g => g.Location).ToArray();
        var match = MovableEnemyMatcher.Match(before, spawn, after.Select(g => g.Location).ToArray(),
            afterBattle ? [current] : [], siren ? configuration.MovableEnemyStep : 1, token);
        var matchedAfter = match.After.ToHashSet();
        var dropped = ImmutableArray.CreateBuilder<Cell>();
        if (!configuration.HasMovableNormalEnemy)
            foreach (var grid in after.Where(g => !matchedAfter.Contains(g.Location) && !g.MaySiren))
            { grid.WipeOut(); dropped.Add(grid.Location); }
        var diff = before.Except(match.Before).ToArray();
        var missing = state.GetMissing(state.Progress).Missing;
        var predicted = ImmutableArray.CreateBuilder<Cell>();
        if (diff.Length > 0 && (siren ? missing.Siren : missing.Enemy) != 0)
        {
            var covered = state.Covered(current, [(0, -2)]).Select(g => g.Location).ToHashSet();
            foreach (var fleet in new[] { state.Fleet1Location, state.Fleet2Location })
                if (fleet is { } location)
                {
                    covered.UnionWith(state.Covered(location, [(0, -1)]).Select(g => g.Location));
                    if (configuration.HasMovableNormalEnemy && !hasEnemyTemplates)
                        covered.UnionWith(state.Covered(location, [(1, 0)]).Select(g => g.Location));
                }
            covered.UnionWith(state.Map.Covered);
            foreach (var grid in siren ? after : state.Cells.Where(g => g.IsSiren))
                covered.UnionWith(state.Covered(grid.Location).Select(g => g.Location));
            var accessible = diff.SelectMany(cell => state.Paths.EnemyReachable(cell, siren ? 2 : 1,
                configuration.HasWall, token)).ToHashSet();
            foreach (var grid in state.Cells.Where(g => covered.Contains(g.Location) && accessible.Contains(g.Location) && g.IsSea && !g.IsFleet))
            {
                grid.IsEnemy = true;
                if (siren) grid.IsSiren = true;
                matchedAfter.Add(grid.Location);
                predicted.Add(grid.Location);
            }
        }
        foreach (var location in matchedAfter) if (location != current) state[location].IsMovable = true;
        return new(siren, match, dropped.ToImmutable(), predicted.ToImmutable());
    }
}
