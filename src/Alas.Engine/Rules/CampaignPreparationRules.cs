using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

/// <summary>Native fast-forward overrides applied after chapter Config, before map initialization.</summary>
public static class CampaignPreparationRules
{
    public static readonly SourceFile Source = CampaignFleetLock.Source;

    public static CampaignConfiguration Apply(CampaignConfiguration configuration, CampaignState state)
    {
        if (!configuration.IsClearMode) return configuration;
        return configuration with
        {
            HasAmbush = false, HasFleetStep = false, HasMovableEnemy = false,
            HasMovableNormalEnemy = false, HasPortal = false, HasLandBased = false,
            HasMaze = false, HasFortress = false, HasBouncingEnemy = false, HasDecoyEnemy = false,
            // Native map_data_init inspects base declarations before loading loop data.
            PoorMapData = configuration.PoorMapData && !state.HasCompleteSpawnDeclarations
        };
    }
}
