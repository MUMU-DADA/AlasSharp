using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CombatRankChecks
{
    public static async Task RunAsync()
    {
        var ui = new Ui();
        var probe = new CombatRankProbe(ui);
        await Rejects<InvalidOperationException>(() => probe.ObserveBattleStatusAsync().AsTask());

        ui.HasFrame = true;
        Check(await probe.ObserveBattleStatusAsync() is null && probe.Evidence is null,
            "An empty battle frame produced rank evidence");
        ui.Visible.Add(UiAssets.Combat.BATTLE_STATUS_A.Id);
        ui.Visible.Add(UiAssets.Combat.BATTLE_STATUS_S.Id);
        var first = await probe.ObserveBattleStatusAsync();
        Check(first is { Rank: CombatRank.S, Source: CombatRankSource.BattleStatus, IsWinningRank: true } &&
              first.AssetId == UiAssets.Combat.BATTLE_STATUS_S.Id && probe.Evidence == first,
            "Battle-status priority or source evidence differs from upstream");

        ui.Visible.Clear();
        ui.Visible.Add(UiAssets.Combat.EXP_INFO_S.Id);
        var experience = await probe.ObserveExperienceAsync();
        Check(experience is { Rank: CombatRank.S, Source: CombatRankSource.Experience } && probe.Evidence == first,
            "Matching experience rank did not preserve the original battle-status evidence");
        ui.Visible.Clear();
        ui.Visible.Add(UiAssets.Combat.EXP_INFO_C.Id);
        await Rejects<InvalidDataException>(() => probe.ObserveExperienceAsync().AsTask());
        Check(probe.Evidence == first, "Conflicting experience screen changed stored evidence");

        foreach (var (asset, rank) in new[]
        {
            (UiAssets.Combat.BATTLE_STATUS_B, CombatRank.B),
            (UiAssets.Combat.BATTLE_STATUS_C, CombatRank.C),
            (UiAssets.Combat.BATTLE_STATUS_D, CombatRank.D),
            (UiAssets.Combat.EXP_INFO_A, CombatRank.A),
            (UiAssets.Combat.EXP_INFO_B, CombatRank.B),
            (UiAssets.Combat.EXP_INFO_D, CombatRank.D)
        })
        {
            ui.Visible.Clear();
            ui.Visible.Add(asset.Id);
            var isolated = new CombatRankProbe(ui);
            var result = asset.Name.StartsWith("EXP_INFO_", StringComparison.Ordinal)
                ? await isolated.ObserveExperienceAsync() : await isolated.ObserveBattleStatusAsync();
            Check(result?.Rank == rank && result.IsWinningRank == (rank is CombatRank.S or CombatRank.A or CombatRank.B),
                "Rank or victory classification is incorrect for " + asset.Name);
        }

        ui.Visible.Clear();
        ui.Visible.Add(UiAssets.Combat.BATTLE_STATUS_D.Id);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Rejects<OperationCanceledException>(() => new CombatRankProbe(ui).ObserveBattleStatusAsync(cancelled.Token).AsTask());
        Console.WriteLine("Independent combat rank: 13 checks passed; synthetic asset matches, no combat actions or settlement.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class Ui : IUiDriver
    {
        public GameServer Server => GameServer.Cn;
        public bool HasFrame { get; set; }
        public TimeProvider Clock => TimeProvider.System;
        public HashSet<string> Visible { get; } = new(StringComparer.Ordinal);
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Visible.Contains(asset.Id)); }
        public ValueTask ScreenshotAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token) => throw new NotSupportedException();
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public void ClearOffset(AssetRule asset) => throw new NotSupportedException();
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
        public void ResetInterval(AssetRule asset, double seconds = 3) => throw new NotSupportedException();
        public void ClearInterval(AssetRule asset) => throw new NotSupportedException();
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token) => throw new NotSupportedException();
    }
}
