using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

internal static class EmotionInput
{
    public static CampaignEmotionMode Mode(JsonObject input, CampaignEmotionMode fallback = CampaignEmotionMode.Calculate)
        => !input.ContainsKey("emotionMode") ? fallback : EmotionRules.ParseMode(
            input["emotionMode"]?.GetValue<string>() ?? throw new ArgumentException("emotionMode cannot be null"));
}
