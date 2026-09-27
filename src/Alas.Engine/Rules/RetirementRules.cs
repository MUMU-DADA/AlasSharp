using System.Collections.Immutable;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;

namespace Alas.Engine.Rules;

public enum RetirementMode { Disabled, OneClick, Old }
public enum ShipRarity { N, R, SR, SSR }
public sealed record RetirementOptions
{
    public RetirementMode Mode { get; init; } = RetirementMode.OneClick;
    public bool KeepLimitBreak { get; init; } = true;
    public ImmutableArray<ShipRarity> Rarities { get; init; } = [ShipRarity.N, ShipRarity.R];
    public int Amount { get; init; } = 3000;
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Amount is not (10 or 3000) || Rarities.IsDefaultOrEmpty ||
            Rarities.Any(r => !Enum.IsDefined(r)) || Rarities.Distinct().Count() != Rarities.Length)
            throw new ArgumentException("Invalid retirement options");
    }
}

public static class RetirementRules
{
    public static readonly SourceFile Source = new("module/retire/retirement.py",
        "99bee01891e5b7b0c105fef7b31b3898e2c296890784d54fa61e934f3736d2f6");
    public static readonly SourceFile DockSource = new("module/retire/dock.py",
        "72d2032f65f1cda7287d3c67a1e92adee8bd4a64971be695ccc7c141d5263b63");
    public static readonly SourceFile SettingsSource = new("module/retire/setting.py",
        "38a38d615ba3b131511acf769656ccc515305d288ecffc14230f0d94cf60315d");
    public static ImmutableArray<(ShipRarity Rarity, Rgb Color)> RarityColors { get; } =
        [(ShipRarity.N, new(174,176,187)), (ShipRarity.R, new(106,195,248)),
         (ShipRarity.SR, new(151,134,254)), (ShipRarity.SSR, new(248,223,107))];
    public static ImmutableArray<Rectangle> Cards { get; } = Grid(93, 76, 164 + 2.0 / 3, 227, 138, 204, 14);
    public static ImmutableArray<UiSettingGroup> DockSettings { get; } = [
        Group("sort", "level", 36, 1, ["rarity", "level", "total", "join", "intimacy", "mood", "stat"]),
        Group("index", "all", 109, 2, ["all", "vanguard", "main", "dd", "cl", "ca", "bb", "cv", "repair", "ss", "others"]),
        Group("faction", "all", 239, 3, ["all", "eagle", "royal", "sakura", "iron", "dragon", "sardegna",
            "northern", "iris", "vichya", "tulipa", "pedreria", "meta", "tempesta", "other"]),
        Group("rarity", "all", 427, 1, ["all", "common", "rare", "elite", "super_rare", "ultra"]),
        Group("extra", "no_limit", 499, 2, ["no_limit", "has_skin", "can_retrofit", "enhanceable", "can_limit_break",
            "not_level_max", "can_awaken", "can_awaken_plus", "special", "oath_skin", "unique_augment_module", "wear_skin", "oathed"])
    ];
    public static ImmutableArray<UiSettingGroup> QuickSettings(GameServer server)
    {
        UiSettingOption Option(string name, AssetRule asset) => new(name, asset.For(server).Area!.Value, asset);
        return [new("filter_1", "R", [Option("R", UiAssets.Retire.RETIRE_SETTING_1)]),
            new("filter_2", "E", [Option("E", UiAssets.Retire.RETIRE_SETTING_2)]),
            new("filter_3", "N", [Option("N", UiAssets.Retire.RETIRE_SETTING_3)]),
            new("filter_4", "all", [Option("all", UiAssets.Retire.RETIRE_SETTING_4)]),
            new("filter_5", "all", [Option("keep_limit_break", UiAssets.Retire.RETIRE_SETTING_5_PRESERVE),
                Option("all", UiAssets.Retire.RETIRE_SETTING_5_ALL)])];
    }
    public static string FilterRarity(ShipRarity rarity) => rarity switch
    {
        ShipRarity.N => "common",
        ShipRarity.R => "rare",
        ShipRarity.SR => "elite",
        ShipRarity.SSR => "super_rare",
        _ => throw new ArgumentOutOfRangeException(nameof(rarity))
    };
    private static UiSettingGroup Group(string name, string fallback, int y, int rows, string[] names)
        => new(name, fallback, Grid(218, y, 147 + 1.0 / 3, 57, 139, 42, rows * 7)
            .Take(names.Length).Select((area, i) => new UiSettingOption(names[i], area)).ToImmutableArray());
    private static ImmutableArray<Rectangle> Grid(int x, int y, double dx, int dy, int width, int height, int count)
        => Enumerable.Range(0, count).Select(i =>
        {
            int left = (int)Math.Round(x + i % 7 * dx, MidpointRounding.ToEven), top = y + i / 7 * dy;
            return new Rectangle(left, top, left + width, top + height);
        }).ToImmutableArray();
}
