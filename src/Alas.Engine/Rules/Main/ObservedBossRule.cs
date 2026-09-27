using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

/// <summary>Shared implementation of the identical main-chapter mystery/observed-boss hook bodies.</summary>
public abstract class ObservedBossRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py", "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    public override MapDefinition Map => CampaignMapCatalog.Get(Id).Map;
    protected async ValueTask<bool> MysteriesThenDefault(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await BattleDefaultAsync(context);
    }
    protected static bool ObservedBossBlocked(CampaignContext context)
        => context.State.Cells.FirstOrDefault(cell => cell.IsBoss) is { } boss &&
            !context.Operations.CheckAccessibility(boss.Location, FleetRoles.BossIndex(context.Config));
    protected async ValueTask<bool> MysteriesThenObservedBoss(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        if (ObservedBossBlocked(context)) return await BattleDefaultAsync(context);
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}
