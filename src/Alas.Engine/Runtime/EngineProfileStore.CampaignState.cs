using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class EngineProfileStore
{
    public ICampaignStopStore CampaignStopStore(string instance, EmotionDeviceIdentity identity)
        => new ProfileCampaignStopStore(this, ValidateName(instance), identity);

    private sealed class ProfileCampaignStopStore(EngineProfileStore workspace, string instance,
        EmotionDeviceIdentity identity) : ICampaignStopStore
    {
        private static JsonObject ReadAchievement(JsonObject profile)
            => profile["campaign"]?["achievement"] as JsonObject
               ?? throw new InvalidDataException("Engine profile 缺少 campaign.achievement");

        private void CheckIdentity(JsonObject profile)
        {
            var device = profile["device"] as JsonObject;
            string serial = device?["serial"]?.GetValue<string>() ?? "";
            string package = device?["package"]?.GetValue<string>() ?? "";
            GameServer server = EngineProfileStore.ParseServer(device?["server"]?.GetValue<string>() ?? "");
            if (string.IsNullOrWhiteSpace(identity.Serial) || string.IsNullOrWhiteSpace(identity.Package) ||
                serial != identity.Serial || package != identity.Package || server != identity.Server)
                throw new EngineProfileException("CONFLICT", "Achievement profile 与当前设备不匹配");
        }

        private static CampaignStopSnapshot Snapshot(JsonObject achievement)
        {
            string folder = Required(achievement, "event");
            string stage = Required(achievement, "stage");
            if (achievement["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out bool isEnabled))
                throw new InvalidDataException("campaign.achievement.enabled 必须是布尔值");
            string revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(achievement.ToJsonString())));
            return new(revision, folder, stage, isEnabled);
        }

        public ValueTask<CampaignStopSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var profile = workspace.ReadRaw(instance, out _);
                CheckIdentity(profile);
                return ValueTask.FromResult(Snapshot(ReadAchievement(profile)));
            }
        }

        public ValueTask SaveAsync(CampaignStopSnapshot expected, string? nextStage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (nextStage is not null && string.IsNullOrWhiteSpace(nextStage)) throw new ArgumentException("Next stage is empty");
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var profile = workspace.ReadRaw(instance, out _);
                CheckIdentity(profile);
                var achievement = ReadAchievement(profile);
                var current = Snapshot(achievement);
                if (current != expected)
                    throw new EngineProfileException("CONFLICT", "Achievement profile 在观察期间发生变化");
                if (nextStage is null) achievement["enabled"] = false;
                else achievement["stage"] = nextStage;
                token.ThrowIfCancellationRequested();
                workspace.WriteRaw(instance, profile);
                return ValueTask.CompletedTask;
            }
        }

        private static string Required(JsonObject value, string key)
            => value[key] is JsonValue node && node.TryGetValue<string>(out string? result) && !string.IsNullOrWhiteSpace(result)
                ? result : throw new InvalidDataException("campaign.achievement 缺少 " + key);
    }
}
