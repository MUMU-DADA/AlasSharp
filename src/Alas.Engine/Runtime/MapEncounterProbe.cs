using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapEncounterKind { None, Combat, AirRaid, Ambush, ItemPopup, UnknownPage }

public interface IMapEncounterProbe
{
    ValueTask InitializeAsync(long frameSequence, CancellationToken token);
    ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token);
}

/// <summary>Read-only upstream interaction priority; CV returns colors and template matches, not decisions.</summary>
public sealed class MapEncounterProbe(IUiDriver ui, bool hasAmbush) : IMapEncounterProbe
{
    public static readonly SourceFile CombatSource = new("module/combat/combat.py",
        "abbe4e2f1017cbdc5b6ca8595bd5411f9a9616b47105066be16b6e7f3910d4b7");
    public static readonly SourceFile AmbushSource = new("module/handler/ambush.py",
        "c6ead02b8c3e54a82ff45f350fe3ab7116a41418bd4fdc7645cd33397123c6c5");
    private double? _airRaidRed;
    private double? _ambushRed;

    public async ValueTask InitializeAsync(long frameSequence, CancellationToken token)
    {
        _airRaidRed = _ambushRed = null;
        if (!hasAmbush) return;
        _airRaidRed = await RedAsync(UiAssets.Handler.MAP_AIR_RAID, frameSequence, token);
        _ambushRed = await RedAsync(UiAssets.Handler.MAP_AMBUSH, frameSequence, token);
    }

    public async ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
    {
        if (await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION, ButtonOffset.Expand(30, 20), token: token) ||
            await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION_WITH_OVERLAY, threshold: 30, token: token))
            return MapEncounterKind.Combat;
        if (hasAmbush)
        {
            if (_airRaidRed is null || _ambushRed is null)
                throw new InvalidOperationException("Initialize the map encounter probe before clicking a grid");
            if (Overlay(_airRaidRed.Value, await RedAsync(UiAssets.Handler.MAP_AIR_RAID, frameSequence, token)) > 0.35)
                return MapEncounterKind.AirRaid;
            if (Overlay(_ambushRed.Value, await RedAsync(UiAssets.Handler.MAP_AMBUSH, frameSequence, token)) > 0.40 ||
                await ui.AppearsAsync(UiAssets.Handler.MAP_AMBUSH_EVADE, ButtonOffset.Expand(30, 30), token: token))
                return MapEncounterKind.Ambush;
        }
        if (await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Vertical(5), token: token))
            return MapEncounterKind.ItemPopup;
        return MapEncounterKind.None;
    }

    internal async ValueTask<bool> IsAirRaidAsync(long frameSequence, CancellationToken token)
    {
        if (_airRaidRed is null) throw new InvalidOperationException("Air raid color baseline was not initialized");
        return Overlay(_airRaidRed.Value, await RedAsync(UiAssets.Handler.MAP_AIR_RAID, frameSequence, token)) > 0.35;
    }

    internal static double Overlay(double initialRed, double currentRed)
    {
        if (!double.IsFinite(initialRed) || !double.IsFinite(currentRed) ||
            initialRed is < 0 or > 255 || currentRed is < 0 or > 255 || initialRed == 247)
            throw new InvalidDataException("Invalid red overlay measurement");
        return (currentRed - initialRed) / (247 - initialRed);
    }

    private async ValueTask<double> RedAsync(AssetRule asset, long frameSequence, CancellationToken token)
    {
        var area = asset.For(ui.Server).Area ?? throw new InvalidDataException("Overlay asset has no area");
        MeanColorObservation measured = await ui.ColorAsync(area, token);
        if (measured.FrameSequence != frameSequence || !double.IsFinite(measured.R))
            throw new InvalidDataException("Overlay measurement belongs to another frame");
        return measured.R;
    }
}

/// <summary>Native air-raid wait; other interactions remain with the explicitly supplied C# handler.</summary>
public sealed class MapAirRaidHandler(IUiDriver ui, MapEncounterProbe probe, Func<long> frameSequence,
    IMapEncounterHandler? next = null)
    : IMapEncounterHandler
{
    public async ValueTask<bool> HandleAsync(MapEncounterKind encounter, CancellationToken token)
    {
        if (encounter != MapEncounterKind.AirRaid)
            return next is not null && await next.HandleAsync(encounter, token);
        var disappear = new IntervalTimer(ui.Clock, 0.5);
        var timeout = new IntervalTimer(ui.Clock, 2.5, count: 2);
        disappear.Reset();
        timeout.Reset();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await ui.ScreenshotAsync(token);
            if (timeout.Reached()) return true;
            if (await probe.IsAirRaidAsync(frameSequence(), token)) disappear.Reset();
            else if (disappear.Reached()) return true;
        }
    }
}
