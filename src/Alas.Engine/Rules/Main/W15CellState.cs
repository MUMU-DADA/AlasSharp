using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

/// <summary>Direct W15GridInfo override; applies before all base observation mutations.</summary>
public sealed class W15CellState(Cell location, MapTile declaration) : CellState(location, declaration)
{
    public new static readonly SourceFile Source = new("campaign/campaign_main/campaign_15_base.py",
        "60f28ffa4c3c6a8255428d99e974f8d7ef7f1c4ea8f3921d4ae5fc48bb2e4ed6");

    public override bool Merge(CellObservation info, MapScanMode mode = MapScanMode.Normal)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (info.IsBoss && !IsLand && MaySiren)
        {
            IsSiren = true;
            EnemyScale = 0;
            EnemyGenre = string.Empty;
            return true;
        }
        return base.Merge(info, mode);
    }
}
