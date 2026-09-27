namespace Alas.Engine.Rules;

/// <summary>Campaign class attributes, separate from Config. Native enemy-search detection now uses luma matching;
/// its retained transparency declaration must not become an extra detection gate.</summary>
public sealed record MapOverlayRules(double Ambush = .40, double AirRaid = .35, double EnemySearching = .5)
{
    public MapOverlayRules Validate()
    {
        if (new[] { Ambush, AirRaid, EnemySearching }.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("Invalid map overlay thresholds");
        return this;
    }
}
