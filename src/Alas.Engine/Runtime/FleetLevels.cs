using System.Collections.Immutable;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetLevelReading(int FleetIndex, long FrameSequence, bool AfterBattle,
    ImmutableArray<int> Levels, ImmutableArray<int> BeforeBattle, bool ReachLevelTriggered, bool Level32Triggered);
public sealed record FleetLevelEvidence(int ReachLevel, bool StopIfReachLevel32, bool ReachLevelTriggered,
    bool Level32Triggered, IReadOnlyList<FleetLevelReading> Readings);

/// <summary>Level's current displayed fleet baseline. A fleet switch must read again with afterBattle=false.
/// Trigger flags are sticky; reaching a level does not interrupt an ongoing sortie.</summary>
public sealed class FleetLevelState
{
    private static ImmutableArray<int> Unknown => [-1, -1, -1, -1, -1, -1];
    public ImmutableArray<int> Levels { get; private set; } = Unknown;
    public ImmutableArray<int> BeforeBattle { get; private set; } = Unknown;
    public bool ReachLevelTriggered { get; private set; }
    public bool Level32Triggered { get; private set; }
    private readonly List<FleetLevelReading> _readings = [];
    public void Reset() { Levels = BeforeBattle = Unknown; _readings.Clear(); }
    public FleetLevelEvidence Evidence(FleetLevelOptions options) => new(options.ReachLevel, options.StopIfReachLevel32,
        ReachLevelTriggered, Level32Triggered, _readings.ToArray());
    public FleetLevelReading Commit(int fleet, long sequence, IReadOnlyList<int> levels, bool afterBattle, FleetLevelOptions options)
    {
        options.Validate();
        if (!options.Enabled) throw new InvalidOperationException("Level observation is disabled");
        if (fleet is not (1 or 2) || sequence <= 0) throw new ArgumentException("Invalid level observation identity");
        var values = levels.ToImmutableArray();
        if (values.Length != 6 || values.Any(value => value < 0)) throw new InvalidDataException("Six nonnegative level observations are required");
        if (_readings.LastOrDefault() is { } last)
        {
            if (sequence <= last.FrameSequence) throw new InvalidDataException("Level observation reused a stale frame");
            if (afterBattle && fleet != last.FleetIndex)
                throw new InvalidDataException("Read the new fleet's level baseline before combat");
        }
        var previous = afterBattle ? Levels : Unknown;
        bool reached = afterBattle && options.ReachLevel > 0 && values.Where((value, i) =>
            value >= options.ReachLevel && options.ReachLevel > previous[i] && previous[i] > 0 &&
            (value - previous[i] == 1 || value < 35)).Any();
        Levels = values; BeforeBattle = previous;
        ReachLevelTriggered |= reached;
        Level32Triggered |= afterBattle && options.StopIfReachLevel32 && values[0] >= 32;
        var reading = new FleetLevelReading(fleet, sequence, afterBattle, values, previous, ReachLevelTriggered, Level32Triggered);
        _readings.Add(reading);
        return reading;
    }
}

public sealed class FleetLevelReader(IUiDriver ui, Func<long> sequence)
{
    public async ValueTask<FleetLevelReading?> ReadAsync(FleetLevelState state, int fleet, bool afterBattle,
        FleetLevelOptions options, CancellationToken token)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        if (!options.Enabled) return null; // Native lv_get performs no OCR and leaves its state unchanged.
        long frame = sequence();
        if (frame <= 0 || !await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
            throw new InvalidDataException("Level reading requires an in-map frame");
        var values = new List<int>();
        foreach (var request in FleetLevelRules.Requests(ui.Server))
        {
            var observation = await ui.ReadTextAsync(request, token);
            token.ThrowIfCancellationRequested();
            if (observation.FrameSequence != frame || sequence() != frame)
                throw new InvalidDataException("Level readings belong to different frames");
            // Same I/D/S/B correction and empty=0 semantics as LevelOcr.after_process.
            values.Add(checked((int)OcrValues.Digit(observation.Text)));
        }
        return state.Commit(fleet, frame, values, afterBattle, options);
    }
}
