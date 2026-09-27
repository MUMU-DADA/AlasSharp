using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Native _goto's post-combat boss spawn recovery, before the destination marker is confirmed.</summary>
public sealed class MapCombatRecovery(CampaignState state, IMapArrivalCamera camera,
    Func<CancellationToken, ValueTask> refocusBoss, Func<CancellationToken, ValueTask>? readHealth = null)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;

    public async ValueTask RecoverAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Authoritative map accounting still waits for arrival. Native checks this
        // after incrementing battle_count; inspect the next raw spawn row here.
        int completed = checked(state.BattleCount + 1);
        if (state.ActiveWaves.Any(wave => wave.Battle == completed && wave.Boss > 0))
        {
            await refocusBoss(token);
            if (state.Health.Get(state.FleetIndex) is { } hp && hp.Weighted.Sum() < .01)
            {
                if (readHealth is null) throw new NotSupportedException("Boss camera recovery requires empty-HP rereading");
                await readHealth(token);
            }
        }
        else await camera.RelocalizeAsync(token);
    }
}
