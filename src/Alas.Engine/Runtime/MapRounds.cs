using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Native Fleet.round_* state, isolated to one sortie. Spawn rows are not cumulative.</summary>
public sealed class MapRounds(CampaignState state)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    private CampaignConfiguration? _configuration;
    private readonly Dictionary<int, int> _enemies = [];
    public bool Initialized => _configuration is not null;
    public int Round { get; private set; }
    public IReadOnlyDictionary<int, int> EnemyRounds => _enemies.ToImmutableDictionary();
    private CampaignConfiguration Config => _configuration ?? throw new InvalidOperationException("Map rounds have not been initialized");
    private IEnumerable<int> Turns => (Config.HasMovableEnemy ? Config.MovableEnemyTurns : [])
        .Concat(Config.HasMovableNormalEnemy ? Config.MovableNormalEnemyTurns : []).Distinct();

    public static void Validate(CampaignConfiguration config)
    {
        if (config.MovableEnemyTurns.IsDefault || config.MovableNormalEnemyTurns.IsDefault ||
            config.MovableEnemyTurns.Concat(config.MovableNormalEnemyTurns).Any(turn => turn <= 0) ||
            config.MovableEnemyStep < 0 || !double.IsFinite(config.SirenMoveWait) || config.SirenMoveWait < 0 ||
            config.Fleet1Step <= 0 || config.Fleet2Step <= 0)
            throw new ArgumentException("Invalid map round or fleet step configuration", nameof(config));
    }

    public void Initialize(CampaignConfiguration config)
    {
        Validate(config);
        if (!state.IsMapInitialized || Initialized)
            throw new InvalidOperationException("Rounds require a fresh initialized map and its initial observations");
        _configuration = config;
        Round = 0;
        RecordBattle();
    }

    public void RecordBattle()
    {
        if (!Config.HasMovableEnemy) return;
        if (state.BattleCount < 0) throw new InvalidDataException("Negative battle count");
        bool reset = !state.Cells.Any(g => g.IsSiren) &&
            (!Config.HasMovableNormalEnemy || !state.Cells.Any(g => g.IsEnemy));
        var wave = state.BattleCount < state.ActiveWaves.Length ? state.ActiveWaves[state.BattleCount] : null;
        int count = checked((wave?.Siren ?? 0) + (Config.HasMovableNormalEnemy ? wave?.Enemy ?? 0 : 0));
        int next = checked((!reset && _enemies.TryGetValue(Round, out int previous) ? previous : 0) + count);
        if (reset) _enemies.Clear();
        if (count > 0) _enemies[Round] = next;
    }

    public void Advance()
    {
        // Normal-only mode does not advance native rounds either.
        if (Config.HasMovableEnemy || Config.HasMaze) Round = checked(Round + 1);
    }
    public void RequireConfiguration(CampaignConfiguration config)
    {
        var original = Config;
        Validate(config);
        if (original.HasMovableEnemy != config.HasMovableEnemy || original.HasMovableNormalEnemy != config.HasMovableNormalEnemy ||
            original.HasMaze != config.HasMaze || original.HasBouncingEnemy != config.HasBouncingEnemy ||
            original.SirenMoveWait != config.SirenMoveWait || !original.MovableEnemyTurns.SequenceEqual(config.MovableEnemyTurns) ||
            !original.MovableNormalEnemyTurns.SequenceEqual(config.MovableNormalEnemyTurns))
            throw new InvalidOperationException("Map round configuration changed after initialization");
    }
    private bool Due(int round, int born) => Turns.Any(turn => round > born && (round - born) % turn == 0);
    public bool EnemyMoved => Config.HasMovableEnemy && _enemies.Keys.Any(born => Due(Round, born));
    public bool MazeChanged => Config.HasMaze && Round != 0 && Round % 3 == 0;
    public bool MazeActive(Cell cell) => Config.HasMaze && state[cell].IsMaze &&
        state.MazeRound > 0 && state[cell].MazeRound.Contains(Round % state.MazeRound);
    public double WaitSeconds
    {
        get
        {
            int next = checked(Round + 1);
            long count = Config.HasMovableEnemy ? _enemies.Where(pair => Due(next, pair.Key)).Sum(pair => (long)pair.Value) : 0;
            if (Config.HasBouncingEnemy)
                count += state.Mechanisms.BouncingRoutes.Count(route => route.Any(cell => state[cell].MayBouncingEnemy));
            double seconds = count * Config.SirenMoveWait + (Config.HasMaze && next % 3 == 0 ? 1 : 0);
            if (!double.IsFinite(seconds)) throw new InvalidDataException("Map round wait overflowed");
            return seconds;
        }
    }
}
