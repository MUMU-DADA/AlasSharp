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
        if (!_options.HasProfileStore || string.IsNullOrWhiteSpace(_options.ApplicationPackage))
            throw new NotSupportedException("Calculated emotion requires a bound configuration instance and game package");
        if (_emotion is null)
        {
            var workspace = new EngineProfileStore(_options.ProfileRoot!);
            _emotion = new(workspace.EmotionStore(_options.ProfileInstance!,
                new(_options.Serial, _options.ApplicationPackage, _options.Server)), Driver.Clock);
            _emotionConfiguration = configuration;
        }
        var emotion = RequireEmotion(configuration);
        // Resume starts inside a map; it must not apply the pre-entry task delay a second time.
        if (!alreadyInMap) return await emotion.CheckEntryAsync(battles, configuration.FleetOrder, token,
            configuration.IsDoubleBook, configuration.UseDoubleBook);
        await emotion.ValidateAsync(token);
        return null;
    }

    private CampaignEmotion RequireEmotion(CampaignConfiguration configuration)
    {
        if (_emotion is null || _emotionConfiguration is null ||
            _emotionConfiguration.EmotionMode != configuration.EmotionMode ||
            _emotionConfiguration.FleetOrder != configuration.FleetOrder)
            throw new InvalidOperationException("Prepare the matching campaign emotion configuration before device actions");
        return _emotion;
    }

    ValueTask ICampaignInMapHost.EnsureEmotionAsync(CampaignConfiguration configuration, CancellationToken token)
        => configuration.EmotionMode.Calculates() ? RequireEmotion(configuration).ValidateAsync(token) : ValueTask.CompletedTask;
}
