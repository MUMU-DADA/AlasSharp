using System.Collections.Immutable;
using System.Globalization;
using Alas.Engine.Imaging;

namespace Alas.Engine.Rules;

/// <summary>HPBalancer's campaign bars and HpControl defaults, independent of exported plans.</summary>
public sealed record FleetHealthOptions
{
    public bool UseLowHpRetreat { get; init; }
    public double LowHpRetreatThreshold { get; init; } = .3;
    public string BalanceWeight { get; init; } = "1000, 1000, 1000";
    public bool UseHpBalance { get; init; }
    public double HpBalanceThreshold { get; init; } = .2;
    public bool UseEmergencyRepair { get; init; }
    public double RepairUseSingleThreshold { get; init; } = .3;
    public double RepairUseMultiThreshold { get; init; } = .6;

    public ImmutableArray<double> Weights()
    {
        foreach (double threshold in new[] { LowHpRetreatThreshold, HpBalanceThreshold,
                     RepairUseSingleThreshold, RepairUseMultiThreshold })
            if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
                throw new ArgumentException("HP control thresholds must be between zero and one");
        var parts = BalanceWeight?.Replace('，', ',').Split(',') ?? [];
        if (parts.Length == 1) parts = [parts[0], parts[0], parts[0]]; // Native numpy broadcasts a single weight.
        var weights = parts.Select(part => int.TryParse(part.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? (double)value : double.NaN).ToImmutableArray();
        if (weights.Length != 3 || weights.Any(value => !double.IsFinite(value) || value < 0) || weights.Max() <= 0)
            throw new ArgumentException("HP balance requires three nonnegative weights with a positive maximum");
        return weights;
    }
}

public static class FleetHealthRules
{
    public static readonly SourceFile Source = new("module/combat/hp_balancer.py",
        "ed35b6a567fc314e65ae680ec706fc04ebce5045176415ede72cc488494cc570");
    public static ImmutableArray<ColorBarRequest> Bars(GameServer server)
    {
        int y = server switch { GameServer.En => 190, GameServer.Jp => 205,
            GameServer.Cn or GameServer.Tw => 206, _ => throw new ArgumentOutOfRangeException(nameof(server)) };
        return Enumerable.Range(0, 6).SelectMany(i => new[] {
            new ColorBarRequest(new(35, y + i * 100, 66, 4), new(99, 44, 24)),
            new ColorBarRequest(new(35, y + i * 100, 66, 4), new(156, 235, 57)) }).ToImmutableArray();
    }
}
