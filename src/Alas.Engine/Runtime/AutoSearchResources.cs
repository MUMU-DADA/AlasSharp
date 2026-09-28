using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record AutoSearchResourceReading(long FrameSequence, string Resource, int Amount, bool LimitTriggered);

/// <summary>CampaignStatus OCR and AutoSearchCombat watchers. Limits are observed during a sortie,
/// never interpreted as permission to interrupt its current battle or to report a clear.</summary>
public sealed class AutoSearchResources(IUiDriver ui, Func<long> frameSequence, int oilLimit,
    bool balancerTask = false, int coinLimit = 10000)
{
    public static readonly SourceFile Source = new("module/campaign/campaign_status.py",
        "2bf0bef021c53782dbdfc28846206dd74f22e33973424f59ca1d4e26df705352");
    private bool _oilChecked, _coinChecked;
    public bool OilLimitTriggered { get; private set; }
    public bool CoinLimitTriggered { get; private set; }
    public List<AutoSearchResourceReading> Readings { get; } = [];
    public void BeginMove() { _oilChecked = false; _coinChecked = false; }
    public async ValueTask ObserveAsync(CancellationToken token)
    {
        if (oilLimit < 0 || coinLimit < 0) throw new ArgumentOutOfRangeException(nameof(oilLimit));
        if (!_oilChecked)
        {
            var check = UiAssets.Campaign.OCR_OIL_CHECK;
            _ = await ui.AppearsAsync(check, token: token);
            long sequence = frameSequence();
            var color = await ui.ColorAsync(ui.ButtonArea(check), token);
            if (color.FrameSequence != sequence || sequence != frameSequence()) throw new InvalidDataException("Oil color frame changed");
            int letter = AssetMatcher.ColorSimilar(color, check.For(ui.Server).Color!.Value, 10)
                ? ui.Server == GameServer.Jp ? 201 : 247
                : AssetMatcher.ColorSimilar(color, new(59, 59, 64), 10) ? 165 : 247;
            int amount = await ReadAsync(UiAssets.Campaign.OCR_OIL, letter, token);
            if (amount != 0) { OilLimitTriggered = amount < Math.Max(500, oilLimit); _oilChecked = true; }
            Readings.Add(new(frameSequence(), "oil", amount, OilLimitTriggered));
        }
        if (!_coinChecked)
        {
            var timeout = new IntervalTimer(ui.Clock, 1, 2); timeout.Reset();
            int amount = 0;
            while (!timeout.Reached())
            {
                amount = await ReadAsync(UiAssets.Campaign.OCR_COIN, ui.Server == GameServer.Jp ? 201 : 239, token);
                if (amount >= 100) break;
                long previous = frameSequence();
                await ui.ScreenshotAsync(token);
                if (frameSequence() <= previous) throw new InvalidDataException("Coin retry reused a stale frame");
            }
            if (amount != 0) { CoinLimitTriggered = balancerTask && amount < coinLimit; _coinChecked = true; }
            Readings.Add(new(frameSequence(), "coin", amount, CoinLimitTriggered));
        }
    }
    private async ValueTask<int> ReadAsync(AssetRule asset, int letter, CancellationToken token)
    {
        long sequence = frameSequence();
        var result = await ui.ReadTextAsync(new(asset.For(ui.Server).Area!.Value.Area, "azur_lane",
            OcrValues.DigitAlphabet, letter, letter, letter), token);
        if (sequence <= 0 || result.FrameSequence != sequence || sequence != frameSequence())
            throw new InvalidDataException("Resource OCR frame changed");
        return checked((int)OcrValues.Digit(result.Text));
    }
}
