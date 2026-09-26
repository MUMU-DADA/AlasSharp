using Alas.Engine.Imaging;

namespace Alas.Engine.Rules;

[Flags]
public enum StageEntranceKind { Normal = 1, Half = 2, Blue = 4, Green = 8, Event20240725 = 16 }
public sealed record StageEntrancePattern(AssetRule Template, PixelPoint NameOffset, PixelPoint NameSize,
    double Similarity = .85, ProfileProcessing Processing = ProfileProcessing.Gray, PixelColor Letter = default);
/// <summary>Direct CampaignOcr extraction declarations, including the upstream EN layout override.</summary>
public static class StageEntranceRules
{
    public static readonly SourceFile Source = new("module/campaign/campaign_ocr.py", "feacc978a19d9227b0b7138846989a7cb06013d6f349014492de6e9ee05b1866");
    public static readonly PixelArea Area = new(87, 117, 1064, 519);
    public static IEnumerable<StageEntrancePattern> Patterns(StageEntranceKind kinds, GameServer server)
    {
        if ((kinds & ~(StageEntranceKind.Normal | StageEntranceKind.Half | StageEntranceKind.Blue | StageEntranceKind.Green | StageEntranceKind.Event20240725)) != 0)
            throw new ArgumentException("Unknown upstream stage entrance kind");
        bool en = server == GameServer.En;
        if (kinds.HasFlag(StageEntranceKind.Normal))
        {
            yield return new(UiAssets.Template.TEMPLATE_STAGE_CLEAR, en ? new(70, 12) : new(75, 9), en ? new(60, 14) : new(60, 16));
            yield return new(UiAssets.Template.TEMPLATE_STAGE_PERCENT, en ? new(45, 3) : new(48, 0), en ? new(60, 14) : new(60, 16));
        }
        if (kinds.HasFlag(StageEntranceKind.Half)) yield return new(UiAssets.Template.TEMPLATE_STAGE_HALF_PERCENT, new(48, 0), new(60, 16));
        if (kinds.HasFlag(StageEntranceKind.Blue))
        {
            yield return new(UiAssets.Template.TEMPLATE_STAGE_BLUE_PERCENT, new(55, 0), new(60, 16), Processing: ProfileProcessing.Letters, Letter: new(255, 255, 255));
            yield return new(UiAssets.Template.TEMPLATE_STAGE_BLUE_CLEAR, new(60, 12), new(60, 16), Processing: ProfileProcessing.Letters, Letter: new(99, 223, 239));
        }
        if (kinds.HasFlag(StageEntranceKind.Green))
        {
            yield return new(UiAssets.Template.TEMPLATE_STAGE_GREEN_CLEAR, new(60, 0), new(60, 22));
            yield return new(UiAssets.Template.TEMPLATE_STAGE_PERCENT, new(52, 0), new(60, 22), .6);
        }
        if (kinds.HasFlag(StageEntranceKind.Event20240725)) yield return new(UiAssets.Template.TEMPLATE_STAGE_CLEAR_20240725, new(73, -4), new(60, 22));
    }
}
