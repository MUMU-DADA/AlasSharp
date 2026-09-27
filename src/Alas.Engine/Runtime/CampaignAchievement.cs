using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignStopSnapshot(string Revision, string Folder, string Stage, bool Enabled);
public interface ICampaignStopStore
{
    ValueTask<CampaignStopSnapshot> ReadAsync(CancellationToken token);
    ValueTask SaveAsync(CampaignStopSnapshot expected, string? nextStage, CancellationToken token);
}
public sealed record CampaignStopEvidence(string RuleId, string Achievement, CampaignMapInfo? Info = null,
    int CancelClicks = 0, long? ReturnedFrame = null, string? NextStage = null, bool? Disabled = null, bool Persisted = false);
public interface ICampaignAchievementService
{
    ValueTask PrepareAchievementAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token);
    ValueTask<CampaignStopEvidence> StopForAchievementAsync(CampaignMapInfo info, TimeSpan timeout, CancellationToken token);
}

/// <summary>Cancel map entry before native handle_map_stop updates the bound task.</summary>
public sealed class CampaignAchievement(IUiDriver ui, Func<long> frameSequence,
    Func<CancellationToken, ValueTask<bool>> isInStage, ICampaignStopStore store,
    IEnumerable<string> stageFiles)
{
    public static readonly SourceFile Source = MapUiRecovery.PreparationSource;
    private CampaignStopSnapshot? _snapshot;
    private CampaignConfiguration? _configuration;
    public CampaignStopEvidence? Evidence { get; private set; }
    public async ValueTask PrepareAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token)
    {
        if (_snapshot is not null) throw new InvalidOperationException("Achievement controller belongs to one task");
        _ = configuration.MapAchievement.Name();
        var snapshot = await store.ReadAsync(token);
        int split = rule.Id.IndexOf('/');
        if (split < 1 || snapshot.Folder != rule.Id[..split] ||
            CampaignObjectives.InputName(snapshot.Stage) != CampaignObjectives.InputName(rule.Id[(split + 1)..]))
            throw new InvalidDataException("Achievement task configuration does not match the requested campaign");
        _configuration = configuration; _snapshot = snapshot;
        Evidence = new(rule.Id, configuration.MapAchievement.Name());
    }
    public async ValueTask<CampaignStopEvidence> StopAsync(CampaignMapInfo info, TimeSpan timeout, CancellationToken token)
    {
        if (_snapshot is null || _configuration is null || Evidence is null)
            throw new InvalidOperationException("Prepare the achievement binding before map actions");
        if (Evidence.Info is not null) throw new InvalidOperationException("Achievement stop cannot be repeated");
        if (info.FrameSequence <= 0 || !double.IsFinite(info.ClearPercentage) || info.ClearPercentage is < 0 or >= 1.4 ||
            !CampaignObjectives.Reached(_configuration.MapAchievement, info))
            throw new InvalidDataException("Map achievement has not been observed");
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        Evidence = Evidence with { Info = info };
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
        try
        {
            long sequence = frameSequence();
            if (sequence < info.FrameSequence) throw new InvalidDataException("Achievement cancellation predates the map observation");
            bool first = ui.HasFrame;
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Map achievement cancellation did not return to stage");
                if (!first)
                {
                    await ui.ScreenshotAsync(linked.Token);
                    long next = frameSequence();
                    if (next <= sequence) throw new InvalidDataException("Map cancellation reused a stale frame");
                    sequence = next;
                }
                first = false;
                if (await isInStage(linked.Token))
                {
                    // Native accepts the current frame; require a frame after preparation observation.
                    if (sequence <= info.FrameSequence) continue;
                    Evidence = Evidence with { ReturnedFrame = sequence };
                    break;
                }
                if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: linked.Token))
                    throw new InvalidDataException("Achievement cancellation unexpectedly entered the map");
                if (await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION, ButtonOffset.Expand(20, 20), interval: 2, token: linked.Token) ||
                    await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION_HARD, ButtonOffset.Expand(20, 20), interval: 2, token: linked.Token) ||
                    await ui.AppearsAsync(UiAssets.Map.FLEET_PREPARATION, ButtonOffset.Expand(20, 50), interval: 2, token: linked.Token))
                {
                    Evidence = Evidence with { CancelClicks = Evidence.CancelClicks + 1 };
                    await ui.ClickAsync(UiAssets.Map.MAP_PREPARATION_CANCEL, linked.Token);
                }
            }
            string current = CampaignObjectives.InputName(_snapshot.Stage);
            string nextStage = _configuration.StageIncrease
                ? CampaignObjectives.NextStage(current, _snapshot.Folder, _configuration, stageFiles) : current;
            bool disable = nextStage == current;
            Evidence = Evidence with { NextStage = disable ? null : nextStage, Disabled = disable };
            await store.SaveAsync(_snapshot, disable ? null : nextStage, linked.Token);
            Evidence = Evidence with { Persisted = true };
            return Evidence;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Achievement cancellation or persistence exceeded its time limit", error); }
    }
}
