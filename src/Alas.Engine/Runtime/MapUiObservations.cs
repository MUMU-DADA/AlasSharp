using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record StageEntrance(Rectangle Icon, Rectangle Name);
public sealed class StageEntranceDetector(IImageProfileVision vision, AssetFiles assets, GameServer server)
{
    public async ValueTask<IReadOnlyList<StageEntrance>> FindAsync(ScreenFrame frame, StageEntranceKind kinds, CancellationToken token)
    {
        var result = new List<StageEntrance>();
        foreach (var rule in StageEntranceRules.Patterns(kinds, server))
        {
            var points = await vision.TemplatePointsAsync(frame, new(StageEntranceRules.Area, await assets.ReadAsync(rule.Template.For(server), token),
                rule.Similarity, rule.Processing, rule.Letter), token);
            foreach (var point in Group(points.Points))
            {
                var icon = new Rectangle(point.X + StageEntranceRules.Area.X, point.Y + StageEntranceRules.Area.Y,
                    point.X + StageEntranceRules.Area.X + points.TemplateSize.X, point.Y + StageEntranceRules.Area.Y + points.TemplateSize.Y);
                var name = new PixelArea(icon.Left + rule.NameOffset.X, icon.Top + rule.NameOffset.Y, rule.NameSize.X, rule.NameSize.Y);
                var means = await vision.LetterColumnMeansAsync(frame, name, new(255, 255, 255), 128, token);
                int width = name.Width;
                for (int x = 10; x <= means.Count - 5; x++)
                    if ((means[x] + means[x + 1] + means[x + 2] + means[x + 3] + means[x + 4]) / 5 > 245)
                    { width = x + 1; break; }
                result.Add(new(icon, new(name.X - 3, name.Y - 7, name.X + width + 3, name.Y + name.Height + 7)));
            }
        }
        return result.AsReadOnly();
    }
    private static IEnumerable<PixelPoint> Group(IReadOnlyList<PixelPoint> points)
    {
        var remaining = points.ToList();
        while (remaining.Count > 0)
        {
            var first = remaining[0];
            var nearby = remaining.Where(p => Math.Abs((long)p.X - first.X) + Math.Abs((long)p.Y - first.Y) <= 3).ToArray();
            remaining.RemoveAll(p => Math.Abs((long)p.X - first.X) + Math.Abs((long)p.Y - first.Y) <= 3);
            yield return new((int)Math.Round(nearby.Average(p => (double)p.X)), (int)Math.Round(nearby.Average(p => (double)p.Y)));
        }
    }
}

public sealed class MapUiObservations(Func<ScreenFrame> current, IImageProfileVision vision, AssetFiles assets,
    GameServer server, StageEntranceKind entrances = StageEntranceKind.Normal) : IMapUiObservations
{
    private readonly StageEntranceDetector _stages = new(vision, assets, server);
    public async ValueTask<int> InfoBarCountAsync(CancellationToken token)
        => (await vision.ColorRowPeaksAsync(current(), UiAssets.Handler.INFO_BAR_AREA.For(server).Area!.Value.Area,
            new(107, 158, 255), 235, 50, 50, token)).Count;
    public async ValueTask<bool> HasStageEntranceAsync(CancellationToken token)
        => (await _stages.FindAsync(current(), entrances, token)).Count > 0;
}
