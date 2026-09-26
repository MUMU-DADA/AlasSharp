using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Counts of completed actions in this sortie; never a settlement verdict.</summary>
public readonly record struct MapProgress(int Battle = 0, int Mystery = 0, int Siren = 0, int Carrier = 0)
{
    public void Validate()
    {
        if (Battle < 0 || Mystery < 0 || Siren < 0 || Carrier < 0)
            throw new ArgumentOutOfRangeException(nameof(MapProgress), "Map progress cannot be negative");
    }
}

public readonly record struct SpawnAmounts(int Enemy, int Mystery, int Siren, int Boss, int Carrier)
{
    public bool IsZero => Enemy == 0 && Mystery == 0 && Siren == 0 && Boss == 0 && Carrier == 0;
}
public sealed record SpawnCensus(int Battle, SpawnAmounts Possible, SpawnAmounts Missing);
public sealed record MapInitialization(bool ClearMode = false, bool PoorMapData = false, bool Walls = false,
    bool Portals = false, bool LandBased = false, bool Maze = false, bool Fortress = false, bool BouncingEnemy = false);

public sealed partial class CampaignState
{
    public static readonly SourceFile SpawnSource = new("module/map/map_base.py",
        "72e0d294061d70a9ba950dd0ef3749bf427fc0914e52ba85ff6fc474144d68ed");
    public static readonly SourceFile InitializationSource = new("module/map/fleet.py",
        "7dd79c92cebcd8f8ebd8b0f44c8d4f2fbae2cdd6753f96f74398898fa7c81f04");
    public bool PoorMapData { get; set; }
    public bool IsMapInitialized { get; private set; }
    public MapMechanisms Mechanisms { get; private set; }
    public ImmutableArray<SpawnWave> ActiveWaves { get; private set; }
    public ImmutableArray<SpawnWave> SpawnStack { get; private set; } = [];
    public int MysteryCount { get; set; }
    public int SirenCount { get; set; }
    public int CarrierCount { get; set; }
    public int AmmoCount { get; private set; } = 3;
    public Cell? Fleet1Location { get; set; }
    public Cell? Fleet2Location { get; set; }
    public Cell? SubmarineLocation { get; set; }
    public MapProgress Progress => new(BattleCount, MysteryCount, SirenCount, CarrierCount);
    // Upstream names this is_map_data_poor, although True means declarations exist.
    public bool HasCompleteSpawnDeclarations => Cells.Any(g => g.MayEnemy) && Cells.Any(g => g.MayBoss) &&
        Cells.Any(g => g.IsSpawnPoint) && !ActiveWaves.IsEmpty;

    /// <summary>The data-only map_data_init path; every sortie owns a fresh state.</summary>
    public void InitializeMapData(MapInitialization options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsMapInitialized || !SpawnStack.IsEmpty)
            throw new InvalidOperationException("Map initialization requires a fresh sortie state");
        BattleCount = MysteryCount = SirenCount = CarrierCount = 0;
        FleetIndex = 1;
        Fleet1Location = Fleet2Location = SubmarineLocation = null;
        AmmoCount = 3;
        ResetMap();
        // Native clear-mode override runs before loading loop tile/spawn declarations.
        PoorMapData = options.PoorMapData && !(options.ClearMode && HasCompleteSpawnDeclarations);
        if (options.ClearMode)
            Mechanisms = new MapMechanisms(Map.Mechanisms.LandBased, Map.Mechanisms.Mazes.Select(g => g.AsEnumerable()));
        LoadMapData(options.ClearMode);
        LoadSpawnData(options.ClearMode);
        Paths.InitializeConnections(options.Walls, options.Portals);
        LoadMechanisms(options.LandBased, options.Maze, options.Fortress, options.BouncingEnemy);
        IsMapInitialized = true;
    }

    /// <summary>Native load_spawn_data appends cumulative rows; initialization calls it once per sortie.</summary>
    public void LoadSpawnData(bool useLoop = false)
    {
        var waves = useLoop && !Map.LoopWaves.IsEmpty ? Map.LoopWaves : Map.Waves;
        var next = ImmutableArray.CreateBuilder<SpawnWave>(SpawnStack.Length + waves.Length);
        next.AddRange(SpawnStack);
        int enemy = 0, mystery = 0, siren = 0, boss = 0;
        foreach (var wave in waves)
        {
            checked { enemy += wave.Enemy; mystery += wave.Mystery; siren += wave.Siren; boss += wave.Boss; }
            next.Add(new(wave.Battle, enemy, mystery, boss, siren));
        }
        ActiveWaves = waves;
        SpawnStack = next.MoveToImmutable();
    }

    /// <summary>Union of dynamic icon occlusion and the upstream static covered declaration.</summary>
    public IReadOnlyList<CellState> CoveredCells()
    {
        var locations = Cells.SelectMany(g => Covered(g.Location)).Select(g => g.Location).Concat(Map.Covered).ToHashSet();
        // Native SelectedGrids.add is a set union. Stable map order is used here;
        // census and predictions do not depend on the set's iteration order.
        return Cells.Where(g => locations.Contains(g.Location)).ToArray();
    }

    public SpawnCensus GetMissing(MapProgress progress, MapScanMode mode = MapScanMode.Normal)
    {
        progress.Validate();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (SpawnStack.IsEmpty) throw new InvalidOperationException("Spawn census requires a nonempty loaded spawn table");
        // Upstream indexes by completed battle count, not by the row's Battle label.
        var wave = SpawnStack[Math.Min(progress.Battle, SpawnStack.Length - 1)];
        int enemy, mystery, siren, boss, carrier;
        checked
        {
            enemy = wave.Enemy - (progress.Battle - progress.Siren) - Cells.Count(g => g.IsEnemy);
            mystery = wave.Mystery - progress.Mystery - Cells.Count(g => g.IsMystery);
            siren = wave.Siren - progress.Siren - Cells.Count(g => g.IsSiren);
            boss = wave.Boss - Cells.Count(g => g.IsBoss);
            carrier = mode == MapScanMode.Carrier ? progress.Carrier - Cells.Count(g => g.IsEnemy && !g.MayEnemy) : 0;
            enemy += Mechanisms.FortressEnemies.Length - Cells.Count(g => g.IsFortress);
            enemy += Mechanisms.BouncingRoutes.Count(route => !route.Any(cell => this[cell].MayBouncingEnemy));
        }
        var covered = CoveredCells();
        var possible = new SpawnAmounts(
            covered.Count(g => (g.MayEnemy || mode == MapScanMode.Movable) && !g.IsEnemy),
            covered.Count(g => g.MayMystery && !g.IsMystery),
            covered.Count(g => (g.MaySiren || mode == MapScanMode.Movable) && !g.IsSiren),
            covered.Count(g => g.MayBoss && !g.IsBoss), covered.Count(g => g.MayCarrier));
        return new(wave.Battle, possible, new(enemy, mystery, siren, boss, carrier));
    }

    public bool MissingIsNone(MapProgress progress, MapScanMode mode = MapScanMode.Normal)
    {
        progress.Validate();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return !PoorMapData && GetMissing(progress, mode).Missing.IsZero;
    }

    /// <summary>Native missing_predict changes inferred flags, never proves combat or completion.</summary>
    public IReadOnlyList<Cell> PredictMissing(MapProgress progress, MapScanMode mode = MapScanMode.Normal)
    {
        progress.Validate();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (PoorMapData) return [];
        var census = GetMissing(progress, mode);
        var may = census.Possible;
        var missing = census.Missing;
        var changed = new List<Cell>();
        foreach (var grid in CoveredCells())
        {
            var previous = (grid.IsEnemy, grid.IsMystery, grid.IsSiren, grid.IsBoss);
            if (grid.MayEnemy && missing.Enemy > 0 && missing.Enemy == may.Enemy) grid.IsEnemy = true;
            if (grid.MayMystery && missing.Mystery > 0 && missing.Mystery == may.Mystery) grid.IsMystery = true;
            if (grid.MaySiren && missing.Siren > 0 && missing.Siren == may.Siren) grid.IsSiren = true;
            if (grid.MayBoss && missing.Boss > 0 && missing.Boss == may.Boss) grid.IsBoss = true;
            // MayCarrier is intentionally evaluated after enemy/siren/boss mutations.
            if (progress.Carrier != 0 && grid.MayCarrier && missing.Carrier > 0 && missing.Carrier == may.Carrier)
                grid.IsEnemy = true;
            if (previous != (grid.IsEnemy, grid.IsMystery, grid.IsSiren, grid.IsBoss)) changed.Add(grid.Location);
        }
        return changed.AsReadOnly();
    }
}
