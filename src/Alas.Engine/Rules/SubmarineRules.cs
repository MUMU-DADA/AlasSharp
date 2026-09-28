namespace Alas.Engine.Rules;

public static class SubmarineRules
{
    public static bool RequiresBossRelocation(this CampaignConfiguration configuration)
        => configuration.Submarine != 0 && configuration.SubmarineMode is SubmarineMode.BossOnly or SubmarineMode.HuntAndBoss;

    public static int BossDistance(this CampaignConfiguration configuration) => configuration.SubmarineDistanceToBoss switch
    {
        "to_boss_position" => 0, "1_grid_to_boss" => 1, "2_grid_to_boss" => 2,
        "use_open_ocean_support" => -1,
        _ => throw new ArgumentException("Unknown submarine distance: " + configuration.SubmarineDistanceToBoss)
    };

    public static SubmarineMode CombatMode(this CampaignConfiguration configuration, bool expectedBoss)
        => configuration.Submarine == 0 ? SubmarineMode.DoNotUse : configuration.RequiresBossRelocation()
            ? expectedBoss ? SubmarineMode.EveryCombat : SubmarineMode.DoNotUse : configuration.SubmarineMode;

    public static string Name(this SubmarineMode mode) => mode switch
    {
        SubmarineMode.DoNotUse => "do_not_use", SubmarineMode.HuntOnly => "hunt_only",
        SubmarineMode.BossOnly => "boss_only", SubmarineMode.HuntAndBoss => "hunt_and_boss",
        SubmarineMode.EveryCombat => "every_combat", _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static SubmarineMode Parse(string value) => value switch
    {
        "do_not_use" => SubmarineMode.DoNotUse, "hunt_only" => SubmarineMode.HuntOnly,
        "boss_only" => SubmarineMode.BossOnly, "hunt_and_boss" => SubmarineMode.HuntAndBoss,
        "every_combat" => SubmarineMode.EveryCombat, _ => throw new ArgumentException("Unknown submarine mode: " + value)
    };
    public static void RequireSupported(CampaignConfiguration configuration)
    {
        _ = configuration.SubmarineMode.Name();
        _ = configuration.BossDistance();
        if (configuration.Submarine < 0) throw new ArgumentException("Invalid submarine fleet");
    }
}
