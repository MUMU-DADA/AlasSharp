using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class EngineSession
{
    private readonly EngineSessionOptions _options;
    private CampaignEmotion? _emotion;
    private CampaignConfiguration? _emotionConfiguration;

    public async ValueTask<EmotionEntryEvidence?> PrepareAsync(CampaignConfiguration configuration, int battles,
        bool alreadyInMap, CancellationToken token)
    {
        if (!configuration.EmotionMode.Calculates()) return null;
        if (!_options.HasEmotionStore || string.IsNullOrWhiteSpace(_options.ApplicationPackage))
            throw new NotSupportedException("Calculated emotion requires a bound configuration instance and game package");
        if (_emotion is null)
        {
            var workspace = new ConfigWorkspace(_options.ConfigRoot!);
            _emotion = new(workspace.EmotionStore(_options.ConfigInstance!, configuration.ConfigTask,
                new(_options.Serial, _options.ApplicationPackage, _options.Server)), Driver.Clock);
            _emotionConfiguration = configuration;
        }
        var emotion = RequireEmotion(configuration);
        // Resume starts inside a map; it must not apply the pre-entry task delay a second time.
        if (!alreadyInMap) return await emotion.CheckEntryAsync(battles, configuration.FleetOrder, token);
        await emotion.ValidateAsync(token);
        return null;
    }

    private CampaignEmotion RequireEmotion(CampaignConfiguration configuration)
    {
        if (_emotion is null || _emotionConfiguration is null ||
            _emotionConfiguration.EmotionMode != configuration.EmotionMode ||
            _emotionConfiguration.ConfigTask != configuration.ConfigTask ||
            _emotionConfiguration.FleetOrder != configuration.FleetOrder)
            throw new InvalidOperationException("Prepare the matching campaign emotion configuration before device actions");
        return _emotion;
    }

    ValueTask ICampaignInMapHost.EnsureEmotionAsync(CampaignConfiguration configuration, CancellationToken token)
        => configuration.EmotionMode.Calculates() ? RequireEmotion(configuration).ValidateAsync(token) : ValueTask.CompletedTask;
}
