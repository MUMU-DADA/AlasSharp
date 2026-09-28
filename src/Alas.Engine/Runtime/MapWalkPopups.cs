using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Native map cat animation and delayed guild popup, sharing timers across grid taps.</summary>
public sealed class MapWalkPopups(IUiDriver ui, UiVisuals visuals, Func<long> frameSequence,
    bool isClearMode, IntervalTimer? catTimer = null)
{
    public static readonly SourceFile Source = new("module/map/map_operation.py",
        "58c3368ef5b18d3c2307915893735d489f5f38ba7c88ef0bdffc7b154b67bff0");
    private readonly IntervalTimer _catTimer = catTimer ?? new(ui.Clock, 2);
    private (MapEncounterKind Kind, long Frame)? _pending;

    public async ValueTask<MapEncounterKind> ObserveAsync(long sequence, CancellationToken token)
    {
        _pending = null;
        RequireFrame(sequence, token);
        var kind = MapEncounterKind.None;
        if (_catTimer.Reached() &&
            (await CatAsync(UiAssets.Map.MAP_CAT_ATTACK, 100, token) ||
             !isClearMode && await CatAsync(UiAssets.Map.MAP_CAT_ATTACK_MIRROR, 200, token)))
            kind = MapEncounterKind.CatAttack;
        else if (await UiRecovery.GuildPopupAppearsAsync(ui, token)) kind = MapEncounterKind.GuildPopup;
        RequireFrame(sequence, token);
        if (kind != MapEncounterKind.None) _pending = (kind, sequence);
        return kind;
    }

    public async ValueTask HandleAsync(MapEncounterKind kind, long sequence, CancellationToken token)
    {
        RequireFrame(sequence, token);
        if (_pending != (kind, sequence) || kind is not (MapEncounterKind.CatAttack or MapEncounterKind.GuildPopup))
            throw new InvalidOperationException("Map popup has no matching observation");
        _pending = null;
        await ui.ClickAsync(kind == MapEncounterKind.CatAttack ? UiAssets.Map.MAP_CAT_ATTACK : UiAssets.Handler.GUILD_POPUP_CANCEL, token);
        if (kind == MapEncounterKind.CatAttack) _catTimer.Reset();
    }

    private ValueTask<bool> CatAsync(AssetRule asset, int count, CancellationToken token)
        => visuals.ColorCountAsync(asset.For(ui.Server).Area ?? throw new InvalidDataException("Map animation has no area"),
            new(255, 231, 123), 30, count, token);

    private void RequireFrame(long sequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (sequence <= 0 || sequence != frameSequence()) throw new InvalidDataException("Map popup belongs to a different frame");
    }
}
