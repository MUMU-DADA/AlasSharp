using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum CombatRank { S, A, B, C, D }
public enum CombatRankSource { BattleStatus, Experience }

public sealed record CombatRankEvidence(CombatRank Rank, CombatRankSource Source, string AssetId)
{
    public bool IsWinningRank => Rank is CombatRank.S or CombatRank.A or CombatRank.B;
}

/// <summary>One combat's rank evidence. A rank alone never establishes a cleared sortie.</summary>
public sealed class CombatRankProbe(IUiDriver ui)
{
    public static readonly SourceFile Source = MapEncounterProbe.CombatSource;
    private static readonly (CombatRank Rank, AssetRule Asset)[] BattleStatus =
    [
        (CombatRank.S, UiAssets.Combat.BATTLE_STATUS_S),
        (CombatRank.A, UiAssets.Combat.BATTLE_STATUS_A),
        (CombatRank.B, UiAssets.Combat.BATTLE_STATUS_B),
        (CombatRank.C, UiAssets.Combat.BATTLE_STATUS_C),
        (CombatRank.D, UiAssets.Combat.BATTLE_STATUS_D)
    ];
    private static readonly (CombatRank Rank, AssetRule Asset)[] Experience =
    [
        (CombatRank.S, UiAssets.Combat.EXP_INFO_S),
        (CombatRank.A, UiAssets.Combat.EXP_INFO_A),
        (CombatRank.B, UiAssets.Combat.EXP_INFO_B),
        (CombatRank.C, UiAssets.Combat.EXP_INFO_C),
        (CombatRank.D, UiAssets.Combat.EXP_INFO_D)
    ];

    private CombatRankEvidence? _evidence;
    public CombatRankEvidence? Evidence => _evidence;

    public async ValueTask<CombatRankEvidence?> ObserveBattleStatusAsync(CancellationToken token = default)
        => await ObserveAsync(BattleStatus, CombatRankSource.BattleStatus, token);

    public async ValueTask<CombatRankEvidence?> ObserveExperienceAsync(CancellationToken token = default)
        => await ObserveAsync(Experience, CombatRankSource.Experience, token);

    private async ValueTask<CombatRankEvidence?> ObserveAsync(
        (CombatRank Rank, AssetRule Asset)[] candidates, CombatRankSource source, CancellationToken token)
    {
        if (!ui.HasFrame) throw new InvalidOperationException("Capture a combat frame before observing its rank");
        foreach (var (rank, asset) in candidates)
        {
            if (!await ui.AppearsAsync(asset, token: token)) continue;
            var observed = new CombatRankEvidence(rank, source, asset.Id);
            if (_evidence is not null && _evidence.Rank != observed.Rank)
                throw new InvalidDataException("Battle status and experience screens disagree on the combat rank");
            _evidence ??= observed;
            return observed;
        }
        return null;
    }
}
