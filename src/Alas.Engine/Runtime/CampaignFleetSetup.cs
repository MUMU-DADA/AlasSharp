using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetPlan(int First, int Second, int Submarine,
    SubmarineMode SubmarineMode = SubmarineMode.DoNotUse, bool IsClearMode = false)
{
    public bool AutoSearch { get; init; }
    public FleetOrder FleetOrder { get; init; } = FleetOrder.Fleet1MobFleet2Boss;
    public bool SubmarineAutoCall { get; init; }

    public void Validate()
    {
        if (First is < 1 or > 6 || Second is < 0 or > 6 || Submarine is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(FleetPlan), "Fleet indices exceed upstream dropdown ranges");
        _ = SubmarineMode.Name();
        _ = FleetRoles.Name(FleetOrder);
        if (AutoSearch && !IsClearMode)
            throw new ArgumentException("Auto search requires confirmed clear mode");
    }
}

public sealed record FleetSetupResult(bool HardMode, bool Changed, bool SubmarineAvailable, int EffectiveSubmarine)
{
    public string? SubmarineStandby { get; init; }
    public string? AutoSearchFleetOrder { get; init; }
    public string? AutoSearchSubmarine { get; init; }
}

public interface ICampaignFleetPreparationService
{
    ValueTask<FleetSetupResult> ConfigureFleetAsync(FleetPlan plan, IPopupHandler popups, CancellationToken token);
}

public sealed class CampaignHardFleetUnsatisfiedException(string slot)
    : Exception($"Hard-mode fleet restriction is not satisfied: {slot}");

/// <summary>Native FleetPreparation order over C# fleet operators and one current frame.</summary>
public sealed class CampaignFleetSetup(IUiDriver ui, IImagePatchVision vision,
    Func<ScreenFrame> currentFrame, IPopupHandler popups)
{
    public async ValueTask<FleetSetupResult> ApplyAsync(FleetPlan plan, CancellationToken token = default)
    {
        plan.Validate();
        var first = new CampaignFleetOperator(ui, vision, currentFrame, FleetSlot.First, popups);
        await first.InitializeAsync(token);
        var second = new CampaignFleetOperator(ui, vision, currentFrame, FleetSlot.SecondFor(ui), popups);
        var submarine = new CampaignFleetOperator(ui, vision, currentFrame, FleetSlot.Submarine, popups);
        await second.InitializeAsync(token);
        await submarine.InitializeAsync(token);
        if (await first.AllowedAsync(token))
            foreach (var setting in CampaignAutoSearchSettings.Settings.Take(4)) ui.LoadOffset(setting, UiAssets.Map.FLEET_1_CLEAR);
        if (await submarine.AllowedAsync(token))
            foreach (var setting in CampaignAutoSearchSettings.Settings.Skip(4)) ui.LoadOffset(setting, UiAssets.Map.SUBMARINE_CLEAR);

        bool? h1 = await first.HardSatisfiedAsync(token);
        bool? h2 = await second.HardSatisfiedAsync(token);
        bool? h3 = await submarine.HardSatisfiedAsync(token);
        if (ui.Server is GameServer.Cn or GameServer.En or GameServer.Jp)
        {
            if (plan.First > 0 && h1 == false) throw new CampaignHardFleetUnsatisfiedException("first");
            if (plan.Second > 0 && h2 == false) throw new CampaignHardFleetUnsatisfiedException("second");
            if (plan.Submarine > 0 && h3 == false) throw new CampaignHardFleetUnsatisfiedException("submarine");
        }
        bool hard = h1 == true || h2 == true || h3 == true;
        if (hard)
        {
            bool available = await submarine.AllowedAsync(token);
            if (available && plan.Submarine == 0) await submarine.ClearAsync(token);
            return await FinishAsync(new(true, false, available, available ? plan.Submarine : 0));
        }

        // Cache availability before the second fleet dropdown can cover the submarine controls.
        bool submarineAvailable = await submarine.AllowedAsync(token);
        if (submarineAvailable)
        {
            if (plan.Submarine > 0)
            {
                if (await second.AllowedAsync(token)) await second.ClickClearAsync(token);
                await submarine.EnsureAsync(plan.Submarine, token);
            }
            else
            {
                bool clicked = false;
                if (await second.AllowedAsync(token)) { await second.ClickClearAsync(token); clicked = true; }
                if (await submarine.AllowedAsync(token)) { await submarine.ClickClearAsync(token); clicked = true; }
                if (clicked) await ui.ScreenshotAsync(token);
            }
        }
        if (plan.Second > 0)
        {
            await second.ClearAsync(token);
            await first.EnsureAsync(plan.First, token);
            await second.EnsureAsync(plan.Second, token);
        }
        else
        {
            if (await second.AllowedAsync(token)) await second.ClearAsync(token);
            await first.EnsureAsync(plan.First, token);
        }
        if (submarineAvailable && plan.Submarine == 0) await submarine.ClearAsync(token);
        return await FinishAsync(new(false, true, submarineAvailable, submarineAvailable ? plan.Submarine : 0));

        async ValueTask<FleetSetupResult> FinishAsync(FleetSetupResult result)
        {
            var settings = new CampaignAutoSearchSettings(ui, vision, currentFrame);
            if (result.EffectiveSubmarine > 0 && plan.SubmarineMode is (SubmarineMode.BossOnly or SubmarineMode.HuntAndBoss))
            {
                // Native disables the manual boss-call role first, then applies automatic roles.
                if (plan.IsClearMode)
                {
                    await settings.EnsureSubmarineStandbyAsync(token);
                    result = result with { SubmarineStandby = "confirmed" };
                }
                else result = result with { SubmarineStandby = "unavailable_clear_mode" };
            }
            if (plan.AutoSearch)
            {
                await settings.EnsureFleetOrderAsync(plan.FleetOrder, token);
                result = result with { AutoSearchFleetOrder = FleetRoles.Name(plan.FleetOrder) };
                if (result.EffectiveSubmarine > 0)
                {
                    await settings.EnsureSubmarineAsync(plan.SubmarineAutoCall, token);
                    result = result with { AutoSearchSubmarine = plan.SubmarineAutoCall ? "sub_auto_call" : "sub_standby" };
                }
            }
            return result;
        }
    }
}
