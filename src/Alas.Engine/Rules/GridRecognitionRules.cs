using System.Collections.Immutable;

namespace Alas.Engine.Rules;

public sealed record EnemyTemplateRule(string Genre, AssetRule Template, ImmutableArray<double> Scales);

/// <summary>Compiled GridPredictor configuration. Chapter definitions can replace these upstream defaults.</summary>
public sealed record GridRecognitionRules
{
    public static readonly SourceFile Source = new("module/config/config_manual.py",
        "1aae224354dd8114839cbe362f9a8a8bfc14e23ef633b4560cd9410d46d9daf5");
    public double ImageScale { get; init; } = 1;
    public double GenreSimilarity { get; init; } = 0.85;
    public bool HasSiren { get; init; }
    public bool SirenHasBossIcon { get; init; }
    public bool SirenHasSmallBossIcon { get; init; }
    public bool HasMystery { get; init; } = true;
    public bool HasMissileAttack { get; init; }
    public ImmutableArray<EnemyTemplateRule> Enemies { get; init; } =
    [
        new("Light", UiAssets.Template.TEMPLATE_ENEMY_Light, [1]),
        new("Main", UiAssets.Template.TEMPLATE_ENEMY_Main, [1]),
        new("Carrier", UiAssets.Template.TEMPLATE_ENEMY_Carrier, [1]),
        new("Treasure", UiAssets.Template.TEMPLATE_ENEMY_Treasure, [1])
    ];
    public ImmutableArray<EnemyTemplateRule> Sirens { get; init; } =
    [
        new("Siren_DD", UiAssets.Template.TEMPLATE_SIREN_DD, [1]),
        new("Siren_CL", UiAssets.Template.TEMPLATE_SIREN_CL, [1]),
        new("Siren_CA", UiAssets.Template.TEMPLATE_SIREN_CA, [1]),
        new("Siren_BB", UiAssets.Template.TEMPLATE_SIREN_BB, [1]),
        new("Siren_CV", UiAssets.Template.TEMPLATE_SIREN_CV, [1])
    ];

    public void Validate()
    {
        if (!double.IsFinite(ImageScale) || ImageScale <= 0 || !double.IsFinite(GenreSimilarity) || GenreSimilarity is < -1 or > 1 ||
            Enemies.IsDefault || Sirens.IsDefault)
            throw new ArgumentException("Invalid grid recognition configuration");
        var entries = Enemies.Concat(HasSiren ? Sirens : []);
        var genres = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry is null || string.IsNullOrWhiteSpace(entry.Genre) || !genres.Add(entry.Genre) ||
                entry.Template?.Kind != AssetKind.Template || entry.Scales.IsDefaultOrEmpty ||
                entry.Scales.Any(s => !double.IsFinite(s) || Math.Round(60 * s) is < 1 or > 1024))
                throw new ArgumentException("Invalid or duplicate grid template configuration");
    }
}
