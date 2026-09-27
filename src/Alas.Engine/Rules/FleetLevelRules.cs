using System.Collections.Immutable;
using Alas.Engine.Imaging;

namespace Alas.Engine.Rules;

public sealed record FleetLevelOptions(int ReachLevel = 0, bool StopIfReachLevel32 = false)
{
    public bool Enabled => ReachLevel > 0 || StopIfReachLevel32;
    public void Validate()
    { if (ReachLevel < 0) throw new ArgumentOutOfRangeException(nameof(ReachLevel)); }
}

public static class FleetLevelRules
{
    public static readonly SourceFile Source = new("module/combat/level.py",
        "1140a478394b51d00758364e388c03d082a83e1439ac78df4d69375a1d9ad7db");
    public static ImmutableArray<OcrRequest> Requests(GameServer server)
    {
        var (x, y, width) = server switch { GameServer.En => (56, 113, 46), GameServer.Jp => (34, 128, 68),
            GameServer.Cn or GameServer.Tw => (58, 128, 46), _ => throw new ArgumentOutOfRangeException(nameof(server)) };
        bool jp = server == GameServer.Jp;
        var preprocessing = new OcrPrefixCrop(8, 107, 255.0 / ((107 + 105 + 107) / 3.0),
            new(70, 102, 152), .299, .587, .114,
            jp ? 5 : 9, jp ? 11 : 15, jp ? 63 : 127, jp ? 23 : 17, jp ? 70 : 46, 3, jp ? 2 : 0);
        return Enumerable.Range(0, 6).Select(i => new OcrRequest(new(x, y + i * 100, width, 19),
            OcrModels.LanguageFor("azur_lane", server), OcrValues.DigitAlphabet,
            Preprocessing: OcrPreprocessing.PrefixCrop, PrefixCrop: preprocessing)).ToImmutableArray();
    }
}
