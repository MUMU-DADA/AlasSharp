namespace Alas.Engine.Rules;

public static class SubmarineRules
{
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
        if (configuration.Submarine < 0) throw new ArgumentException("Invalid submarine fleet");
        if (configuration.Submarine != 0 && configuration.SubmarineMode is SubmarineMode.BossOnly or SubmarineMode.HuntAndBoss)
            throw new NotSupportedException("Boss submarine modes require relocation and fleet-page standby preparation, which are not yet ported");
    }
}
