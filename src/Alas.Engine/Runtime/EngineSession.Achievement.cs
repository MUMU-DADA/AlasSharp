using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class EngineSession : ICampaignAchievementService
{
    private CampaignAchievement? _achievement;
    public async ValueTask PrepareAchievementAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token)
    {
        if (configuration.MapAchievement == MapAchievement.NonStop) return;
        if (!_options.HasEmotionStore || string.IsNullOrWhiteSpace(_options.ApplicationPackage))
            throw new NotSupportedException("Map achievement requires a bound task configuration and game package");
        if (_achievement is not null) throw new InvalidOperationException("Achievement state is already prepared for this task");
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No achievement image"),
            _vision, _assets, Driver.Server);
        var guard = new MapUiRecovery(Driver, observations, _application, recovery, recovery);
        var workspace = new ConfigWorkspace(_options.ConfigRoot!);
        string folder = rule.Id[..rule.Id.IndexOf('/')];
        _achievement = new(Driver, () => Driver.Frame?.Sequence ?? 0, guard.IsInStageAsync,
            workspace.CampaignStopStore(_options.ConfigInstance!, configuration.ConfigTask,
                new(_options.Serial, _options.ApplicationPackage, _options.Server)),
            CampaignMapCatalog.Ids.Where(id => id.StartsWith(folder + "/", StringComparison.Ordinal)).Select(id => id[(folder.Length + 1)..]));
        await _achievement.PrepareAsync(rule, configuration, token);
    }
    public ValueTask<CampaignStopEvidence> StopForAchievementAsync(CampaignMapInfo info, TimeSpan timeout, CancellationToken token)
        => (_achievement ?? throw new InvalidOperationException("Prepare achievement configuration before cancelling entry"))
            .StopAsync(info, timeout, token);
}
