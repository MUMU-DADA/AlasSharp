using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using static Alas.Engine.Rules.UiAssets.Retire;

namespace Alas.Engine.Tests;

/// <summary>Actual dock/setting/retirement composition with pure CV pixels and synthetic device boundaries.</summary>
internal static class RetirementWorkflowChecks
{
    internal static async Task RunAsync(PureVisionWorker vision)
    {
        foreach (GameServer server in Enum.GetValues<GameServer>())
        {
            var ui = new DockUi(server);
            await ui.PrimeAsync();
            var dock = Dock(ui, vision);
            await dock.FavouriteAsync(false, default);
            await dock.DescendingAsync(true, default);
            await dock.FilterAsync(["common", "rare"], default);
            Check(!ui.Favourite && ui.Descending && ui.Scene == "dock" &&
                ui.Selected.SetEquals(["sort/level", "index/all", "faction/all", "rarity/common", "rarity/rare", "extra/no_limit"]),
                "Actual dock settings failed to confirm switches, defaults and multi-selection");
            await dock.QuickSettingsAsync(null, default);
            Check(ui.Quick.SetEquals(["filter_1/R", "filter_2/E", "filter_3/N", "filter_4/all", "filter_5/keep_limit_break"]),
                "Quick settings changed the preserved fifth group");
            await dock.QuickSettingsAsync("all", default);
            Check(ui.Quick.Contains("filter_5/all") && !ui.Quick.Contains("filter_5/keep_limit_break"),
                "Quick settings did not select all");
            await dock.QuickSettingsAsync("keep_limit_break", default);
            Check(ui.Scene == "dock" && ui.Quick.Contains("filter_5/keep_limit_break") && !ui.Quick.Contains("filter_5/all"),
                "Quick settings did not restore the keep option and leave the settings page");

            // Use the real dock in the longest allowed fallback route for each native server.
            ui = new DockUi(server) { SuccessAt = server == GameServer.Tw ? 2 : 5 };
            await ui.PrimeAsync();
            var handler = Handler(ui, vision);
            var result = await handler.RunAsync(new() { KeepLimitBreak = false }, default);
            Check(result.ReturnedSequence > result.StartedSequence && result.Confirmations is
                [{ ShipConfirmClicks: 1, EquipmentConfirmClicks: 1, ReturnedSequence: not null }] &&
                ui.Attempts == ui.SuccessAt && ui.Scene == "returned" && result.NativeSelectionEstimate == 10,
                "Full one-click retirement lost native fallback, confirmed return or selection estimate");
        }

        foreach (int amount in new[] { 10, 3000 })
        {
            var ui = new DockUi(GameServer.Cn) { Old = true };
            await ui.PrimeAsync();
            var result = await Handler(ui, vision).RunAsync(new() { Mode = RetirementMode.Old, Amount = amount }, default);
            Check(result.NativeSelectionEstimate == (amount == 10 ? 10 : 14) &&
                result.Confirmations.Count == (amount == 10 ? 1 : 2) &&
                ui.Selected.SetEquals(["sort/level", "index/all", "faction/all", "rarity/all", "extra/no_limit"]) &&
                ui.Descending && ui.Scene == "returned", "Old retirement lost batches, default restoration or exit");
        }
        var failed = new DockUi(GameServer.Cn) { Old = true, FailClick = "EQUIP_CONFIRM_2" };
        await failed.PrimeAsync();
        var partial = Handler(failed, vision);
        await Rejects<IOException>(async () => await partial.RunAsync(new() { Mode = RetirementMode.Old }, default));
        Check(partial.Evidence is [
            {
                ReturnedSequence: null, NativeSelectionEstimate: 0,
                Confirmations: [{ ShipConfirmClicks: 1, EquipmentConfirmClicks: 0, ReturnedSequence: null }]
            }],
            "Old retirement promoted selected cards to completed retirements after a failed confirmation");

        var stalled = new DockUi(GameServer.Cn) { IgnoreSettingClicks = true };
        await stalled.PrimeAsync();
        await Rejects<TimeoutException>(async () => await Dock(stalled, vision).QuickSettingsAsync("all", default));
        Check(stalled.Attempts == 0 && stalled.Scene == "quick", "Unconfirmed settings authorized a retirement");
        await TipsAsync(vision);
        Console.WriteLine("Retirement composition: four-server real dock + quick settings + fallback, old-retire batches, partial failure and tips passed; CV pixels and synthetic I/O only.");
    }

    private static async Task TipsAsync(PureVisionWorker vision)
    {
        foreach (var asset in new[] { UiAssets.Handler.GAME_TIPS, UiAssets.Handler.GAME_TIPS3, UiAssets.Handler.GAME_TIPS4 })
            foreach (int count in new[] { 50, 51 })
            {
                var ui = new TipsUi(asset, count);
                await ui.PrimeAsync();
                var handler = new RetirementHandler(ui, Dock(new DockUi(GameServer.Cn), vision), () => 1,
                    _ => ValueTask.FromResult(0));
                bool handled = await new CampaignInterruptions(ui, new UiVisuals(vision, () => ui.Current), handler)
                    .RetirementAsync(default);
                Check(handled == (count > 50) && ui.Clicks.SequenceEqual(count > 50 ? ["GAME_TIPS"] : Array.Empty<string>()),
                    "Game tips lost translated pixel threshold or native shared click target");
            }
    }

    private static RetirementDock Dock(DockUi ui, PureVisionWorker vision) => new(ui, new(vision, () => ui.Current));
    private static RetirementHandler Handler(DockUi ui, PureVisionWorker vision)
        => new(ui, Dock(ui, vision), () => ui.Current.Sequence, ui.InfoAsync);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class DockUi(GameServer server) : AppearanceProbe(server, null)
    {
        private int _frames, _available = 14, _selected;
        private string _seen = "dock";
        private bool _seenFavourite = true, _seenDescending, _infoSeen;
        private AssetRule? _matched;
        public int SuccessAt { get; init; } = 1;
        public int Attempts { get; private set; }
        public bool Old { get; init; }
        public bool Favourite { get; private set; } = true;
        public bool Descending { get; private set; }
        public string Scene { get; private set; } = "dock";
        public string? FailClick { get; init; }
        public bool IgnoreSettingClicks { get; init; }
        public HashSet<string> Selected { get; } = ["sort/rarity", "index/cv", "faction/eagle", "rarity/elite", "extra/enhanceable"];
        public HashSet<string> Quick { get; } = ["filter_5/keep_limit_break"];
        public ScreenFrame Current { get; private set; } = new(0, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);

        public async Task PrimeAsync() { await base.ScreenshotAsync(default); Capture(); }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++_frames > 160) throw new TimeoutException("Synthetic dock stalled");
            Time.Advance(.5); Capture(); return ValueTask.CompletedTask;
        }
        private void Capture()
        {
            if (Scene == "info" && _infoSeen) Scene = "dock";
            _infoSeen = Scene == "info";
            _seen = Scene; _seenFavourite = Favourite; _seenDescending = Descending;
            byte[] rgb = new byte[1280 * 720 * 3];
            if (Scene == "filter")
                foreach (var group in RetirementRules.DockSettings)
                    foreach (var option in group.Options)
                        if (Selected.Contains(group.Name + "/" + option.Name)) Paint(rgb, option.Area, new(181, 142, 90));
            if (Scene == "quick")
                foreach (var group in RetirementRules.QuickSettings(Server))
                    foreach (var option in group.Options)
                        if (Quick.Contains(group.Name + "/" + option.Name)) Paint(rgb, option.Area, new(255, 255, 255));
            if (Scene == "dock" && Old)
                foreach (var card in RetirementRules.Cards.Take(_available))
                    Paint(rgb, new(card.Left, card.Top, card.Right, card.Top + 5), new(174, 176, 187));
            Current = new(Current.Sequence + 1, DateTimeOffset.UnixEpoch, VisionChecks.Png(1280, 720, rgb));
        }
        private bool Visible(string name) => _seen switch
        {
            "dock" or "info" => name switch
            {
                "IN_RETIREMENT_CHECK" or "DOCK_CHECK" or "ONE_CLICK_RETIREMENT" or "RETIRE_SETTING_ENTER" or "BACK_ARROW" => true,
                "COMMON_SHIP_FILTER_ENABLE" => _seenFavourite,
                "COMMON_SHIP_FILTER_DISABLE" => !_seenFavourite,
                "SORT_ASC" => !_seenDescending,
                "SORT_DESC" => _seenDescending,
                "SHIP_CONFIRM" => Old && _selected > 0,
                _ => false
            },
            "filter" => name == "DOCK_FILTER_CONFIRM",
            "quick" => name == "RETIRE_SETTING_QUIT",
            "ship" => name == (Old ? "SHIP_CONFIRM" : "SHIP_CONFIRM_2"),
            "equipment" => name == "EQUIP_CONFIRM_2",
            _ => false
        };
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); _matched = asset;
            if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
            bool result = Visible(asset.Name);
            if (result && interval > 0) Timer(asset).Reset();
            return ValueTask.FromResult(result);
        }
        public ValueTask<int> InfoAsync(CancellationToken token) => ValueTask.FromResult(_seen == "info" ? 1 : 0);
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (asset.Name == FailClick) throw new IOException("Synthetic equipment confirmation failed");
            switch (asset.Name)
            {
                case "COMMON_SHIP_FILTER_ENABLE": case "COMMON_SHIP_FILTER_DISABLE": Favourite = !Favourite; break;
                case "SORTING_CLICK": Descending = !Descending; break;
                case "DOCK_FILTER": Scene = "filter"; break;
                case "DOCK_FILTER_CONFIRM": Scene = "dock"; break;
                case "RETIRE_SETTING_ENTER": Scene = "quick"; break;
                case "RETIRE_SETTING_QUIT": Scene = "dock"; break;
                case "ONE_CLICK_RETIREMENT": Scene = ++Attempts == SuccessAt ? "ship" : "info"; break;
                case "SHIP_CONFIRM": case "SHIP_CONFIRM_2": Scene = "equipment"; break;
                case "EQUIP_CONFIRM_2": Scene = "dock"; _available -= _selected; _selected = 0; break;
                case "BACK_ARROW": Scene = "returned"; break;
                default:
                    var pair = RetirementRules.QuickSettings(Server).SelectMany(g => g.Options.Select(o => (Group: g, Option: o)))
                        .Single(p => p.Option.Asset == asset);
                    if (!IgnoreSettingClicks) Set(Quick, pair.Group.Name, pair.Option.Name, true);
                    break;
            }
            return ValueTask.CompletedTask;
        }
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Scene == "dock" && Old) _selected++;
            else
            {
                var pair = RetirementRules.DockSettings.SelectMany(g => g.Options.Select(o => (Group: g, Option: o)))
                    .Single(p => p.Option.Area == area);
                if (!IgnoreSettingClicks) Set(Selected, pair.Group.Name, pair.Option.Name, pair.Group.Name == "sort");
            }
            return ValueTask.CompletedTask;
        }
        private static void Set(HashSet<string> active, string group, string option, bool exclusive)
        {
            if (exclusive || option is "all" or "no_limit") active.RemoveWhere(v => v.StartsWith(group + "/", StringComparison.Ordinal));
            else { active.Remove(group + "/all"); active.Remove(group + "/no_limit"); }
            active.Add(group + "/" + option);
        }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value;
        public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Rgb color;
            if (area.Height == 5)
            {
                int index = Array.FindIndex(RetirementRules.Cards.ToArray(), c => c.Left == area.Left && c.Top == area.Top);
                color = index >= 0 && index < _available ? new(174, 176, 187) : new(0, 0, 0);
            }
            else color = _matched!.For(Server).Color!.Value;
            return ValueTask.FromResult(new MeanColorObservation(Current.Sequence, color.R, color.G, color.B));
        }
    }

    private sealed class TipsUi(AssetRule visible, int count) : AppearanceProbe(GameServer.Cn, null)
    {
        public ScreenFrame Current { get; private set; } = new(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public List<string> Clicks { get; } = [];
        public async Task PrimeAsync()
        {
            await base.ScreenshotAsync(default);
            byte[] rgb = new byte[1280 * 720 * 3];
            var area = ButtonArea(visible);
            for (int i = 0; i < count; i++)
                rgb.AsSpan(((area.Top + i / area.Width) * 1280 + area.Left + i % area.Width) * 3, 3).Fill(40);
            Current = Current with { Png = VisionChecks.Png(1280, 720, rgb) };
        }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value.Offset(7, -3);
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default) => ValueTask.FromResult(asset == visible);
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { Clicks.Add(asset.Name); return ValueTask.CompletedTask; }
    }

    private static void Paint(byte[] rgb, Rectangle area, Rgb color)
    {
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                int i = (y * 1280 + x) * 3;
                rgb[i] = (byte)color.R; rgb[i + 1] = (byte)color.G; rgb[i + 2] = (byte)color.B;
            }
    }
}
