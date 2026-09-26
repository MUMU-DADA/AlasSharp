using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CombatFlowChecks
{
    public static async Task RunAsync()
    {
        var ui = new Ui(UiAssets.Combat.BATTLE_PREPARATION, UiAssets.Combat.AUTOMATION_OFF);
        ui.Enqueue(UiAssets.Combat.BATTLE_PREPARATION, UiAssets.Combat.AUTOMATION_ON);
        ui.Enqueue(UiAssets.Combat.AUTOMATION_CONFIRM_CHECK, UiAssets.Combat.AUTOMATION_CONFIRM);
        ui.Enqueue(UiAssets.CombatUi.PAUSE);
        ui.Enqueue(UiAssets.Combat.BATTLE_STATUS_S);
        ui.Enqueue(UiAssets.Combat.EXP_INFO_S);
        ui.Enqueue(UiAssets.Combat.GET_ITEMS_1);
        for (int i = 0; i < 4; i++) ui.Enqueue(UiAssets.Ui.CAMPAIGN_CHECK);
        var result = await Flow(ui).RunAutoAsync(Options);
        Check(result is { Return: CombatReturn.InStage, Rank: { Rank: CombatRank.S, IsWinningRank: true } } &&
              result.Rank.Source == CombatRankSource.BattleStatus && result.CapturedFrames == 10,
            "Automatic combat did not preserve the rank and stable stage return");
        Check(ui.Clicks.SequenceEqual(new[] { "AUTOMATION_SWITCH", "BATTLE_PREPARATION", "AUTOMATION_CONFIRM",
                "BATTLE_STATUS_S", "EXP_INFO_S", "GET_ITEMS_1" }),
            "Automatic combat did not follow the preparation, result and reward sequence");

        var conflicting = new Ui(UiAssets.Combat.BATTLE_PREPARATION);
        conflicting.Enqueue(UiAssets.CombatUi.PAUSE);
        conflicting.Enqueue(UiAssets.Combat.BATTLE_STATUS_S);
        conflicting.Enqueue(UiAssets.Combat.EXP_INFO_C);
        await Rejects<InvalidDataException>(() => Flow(conflicting).RunAutoAsync(Options).AsTask());
        Check(conflicting.Clicks.Contains("BATTLE_STATUS_S") && !conflicting.Clicks.Contains("EXP_INFO_C"),
            "Contradictory experience rank was clicked or battle evidence was lost");

        var map = new Ui(UiAssets.Combat.BATTLE_PREPARATION);
        map.Enqueue(UiAssets.CombatUi.PAUSE);
        map.Enqueue(UiAssets.Combat.BATTLE_STATUS_B);
        for (int i = 0; i < 8; i++) map.Enqueue(UiAssets.Handler.IN_MAP);
        var mapResult = await Flow(map).RunAutoAsync(Options);
        Check(mapResult is { Return: CombatReturn.InMap, Rank: { Rank: CombatRank.B } } && !mapResult.EnemySearchingObserved,
            "Ordinary combat did not wait for stable map return");

        var unknown = new Ui(UiAssets.Combat.BATTLE_PREPARATION);
        unknown.Enqueue(UiAssets.CombatUi.PAUSE);
        unknown.Enqueue(UiAssets.Combat.GET_ITEMS_1);
        unknown.Enqueue(UiAssets.Combat.GET_SHIP, UiAssets.Combat.NEW_SHIP);
        for (int i = 0; i < 4; i++) unknown.Enqueue(UiAssets.Ui.CAMPAIGN_CHECK);
        var unknownResult = await Flow(unknown).RunAutoAsync(Options);
        Check(unknownResult is { Return: CombatReturn.InStage, Rank: null, NewShipObserved: true } &&
              unknown.Clicks.Contains("GET_SHIP"),
            "Item-first combat invented a rank or lost the new-ship popup");

        var searching = new Ui(UiAssets.Combat.BATTLE_PREPARATION);
        searching.Enqueue(UiAssets.CombatUi.PAUSE);
        searching.Enqueue(UiAssets.Combat.BATTLE_STATUS_A);
        searching.Enqueue(UiAssets.Handler.IN_MAP, UiAssets.Handler.MAP_ENEMY_SEARCHING);
        for (int i = 0; i < 8; i++) searching.Enqueue(UiAssets.Handler.IN_MAP);
        var searchingResult = await Flow(searching).RunAutoAsync(Options);
        Check(searchingResult is { Return: CombatReturn.InMap, EnemySearchingObserved: true, Rank: { Rank: CombatRank.A } },
            "Enemy-searching overlay was mistaken for a completed map return");

        var auto = new Ui(UiAssets.Combat.BATTLE_PREPARATION);
        auto.Enqueue(UiAssets.CombatUi.PAUSE);
        auto.Enqueue(UiAssets.Combat.COMBAT_AUTO);
        auto.Enqueue(UiAssets.Combat.COMBAT_AUTO);
        auto.Enqueue(UiAssets.Combat.BATTLE_STATUS_A);
        for (int i = 0; i < 8; i++) auto.Enqueue(UiAssets.Handler.IN_MAP);
        var autoResult = await Flow(auto).RunAutoAsync(Options);
        Check(autoResult is { Return: CombatReturn.InMap, Rank: { Rank: CombatRank.A } } &&
              auto.Clicks.Count(click => click == "COMBAT_AUTO_SWITCH") == 1,
            "Manual joystick was not switched to upstream automatic mode exactly once");

        var emptyStage = new Ui(UiAssets.Combat.BATTLE_PREPARATION) { StageEntranceVisible = false };
        emptyStage.Enqueue(UiAssets.CombatUi.PAUSE);
        emptyStage.Enqueue(UiAssets.Combat.BATTLE_STATUS_S);
        emptyStage.Enqueue(UiAssets.Ui.CAMPAIGN_CHECK);
        await Rejects<TimeoutException>(() => Flow(emptyStage).RunAutoAsync(Options with
        { StatusTimeout = TimeSpan.FromSeconds(4) }).AsTask());

        var stalled = new Ui();
        await Rejects<TimeoutException>(() => Flow(stalled).RunAutoAsync(Options with
        { PreparationTimeout = TimeSpan.FromSeconds(3) }).AsTask());
        Check(stalled.Clicks.Count == 0, "An unknown preparation page caused a device action");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Rejects<OperationCanceledException>(() => Flow(new Ui()).RunAutoAsync(Options, cancelled.Token).AsTask());
        Console.WriteLine("Independent combat flow: preparation, result, stage/map return, conflict, timeout and cancellation passed; synthetic frames only.");
    }

    private static CombatFlow Flow(Ui ui) => new(ui, new NoStory(), new NoPopup(), new Stage(ui));
    private static CombatFlowOptions Options => new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(15));
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class NoStory : IStoryHandler
    {
        public ValueTask<bool> StorySkipAsync(CancellationToken token = default) => ValueTask.FromResult(false);
        public ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class NoPopup : IPopupHandler
    { public ValueTask<bool> ConfirmAsync(CancellationToken token) => ValueTask.FromResult(false); }
    private sealed class Stage(Ui ui) : IMapUiObservations
    {
        public ValueTask<int> InfoBarCountAsync(CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token)
            => ValueTask.FromResult(ui.StageEntranceVisible && ui.Visible.Contains(UiAssets.Ui.CAMPAIGN_CHECK.Id));
    }
    private sealed class ClockSource : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
    private sealed class Ui : IUiDriver
    {
        private readonly Queue<HashSet<string>> _frames = new();
        private readonly ClockSource _clock = new();
        public Ui(params AssetRule[] initial) { Visible = initial.Select(a => a.Id).ToHashSet(StringComparer.Ordinal); }
        public GameServer Server => GameServer.Cn;
        public bool HasFrame => true;
        public TimeProvider Clock => _clock;
        public HashSet<string> Visible { get; private set; }
        public bool StageEntranceVisible { get; set; } = true;
        public List<string> Clicks { get; } = [];
        public void Enqueue(params AssetRule[] assets)
            => _frames.Enqueue(assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal));
        public ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_frames.Count > 0) Visible = _frames.Dequeue();
            _clock.Advance(TimeSpan.FromSeconds(1));
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Visible.Contains(asset.Id)); }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Clicks.Add(asset.Name); return ValueTask.CompletedTask; }
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public void ClearOffset(AssetRule asset) => throw new NotSupportedException();
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
        public void ResetInterval(AssetRule asset, double seconds = 3) => throw new NotSupportedException();
        public void ClearInterval(AssetRule asset) => throw new NotSupportedException();
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); _clock.Advance(time); return ValueTask.CompletedTask; }
    }
}
