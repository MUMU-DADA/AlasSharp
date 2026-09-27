using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record EmotionDeviceIdentity(string Serial, string Package, GameServer Server);

public sealed partial class EngineProfileStore
{
    // Kept as provenance for the offline oracle; runtime state is Engine-native.
    public static readonly SourceFile EmotionBindingSource = new("module/config/config.py",
        "fdaba7e77c5ffdca9e71a2ded80095a9ce061335d44a30861df9f9cd1963b854");

    public IEmotionStore EmotionStore(string instance, EmotionDeviceIdentity? identity = null)
        => new ProfileEmotionStore(this, ValidateName(instance), identity);

    private sealed record BoundEmotion(EmotionSnapshot Snapshot, JsonObject Emotion);

    private BoundEmotion ReadEmotion(JsonObject profile)
    {
        var emotion = profile["campaign"]?["emotion"] as JsonObject
            ?? throw new InvalidDataException("Engine profile 缺少 campaign.emotion");
        var fleets = emotion["fleets"] as JsonArray;
        if (fleets is null || fleets.Count != 2) throw new InvalidDataException("Emotion state requires two fleets");

        FleetEmotionRecord Read(int index)
        {
            if (fleets[index] is not JsonObject fleet) throw new InvalidDataException("Invalid emotion fleet state");
            int value = RequiredInt(fleet, "value");
            string recordedText = RequiredString(fleet, "recordedAt");
            if (!DateTime.TryParseExact(recordedText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var recorded)) throw new InvalidDataException("Invalid emotion record time");
            var settings = new FleetEmotionSettings(ParseControl(RequiredString(fleet, "control")),
                ParseRecovery(RequiredString(fleet, "recovery")), RequiredBool(fleet, "oath"));
            settings.Validate();
            return new(value, new DateTimeOffset(DateTime.SpecifyKind(recorded, DateTimeKind.Local)), settings);
        }

        string revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(emotion.ToJsonString())));
        return new(new(revision, [Read(0), Read(1)]), emotion);
    }

    private sealed class ProfileEmotionStore(EngineProfileStore workspace, string instance,
        EmotionDeviceIdentity? identity) : IEmotionStore
    {
        private void CheckIdentity(JsonObject profile)
        {
            if (identity is null) return;
            var device = profile["device"] as JsonObject;
            string serial = device?["serial"]?.GetValue<string>() ?? "";
            string package = device?["package"]?.GetValue<string>() ?? "";
            GameServer server = EngineProfileStore.ParseServer(device?["server"]?.GetValue<string>() ?? "");
            if (string.IsNullOrWhiteSpace(identity.Serial) || string.IsNullOrWhiteSpace(identity.Package) ||
                serial != identity.Serial || package != identity.Package || server != identity.Server)
                throw new EngineProfileException("CONFLICT", "Emotion profile 与当前设备不匹配");
        }

        public ValueTask<EmotionSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var profile = workspace.ReadRaw(instance, out _);
                CheckIdentity(profile);
                return ValueTask.FromResult(workspace.ReadEmotion(profile).Snapshot);
            }
        }

        public ValueTask<EmotionSnapshot> SaveAsync(EmotionSnapshot expected, ImmutableArray<int> values,
            DateTimeOffset recordedAt, DateTimeOffset? nextRun, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (values.IsDefault || values.Length != 2 || values.Any(v => v is < -150 or > 150))
                throw new ArgumentException("Emotion save requires two valid values");
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var profile = workspace.ReadRaw(instance, out _);
                CheckIdentity(profile);
                var bound = workspace.ReadEmotion(profile);
                if (bound.Snapshot.Revision != expected.Revision)
                    throw new EngineProfileException("CONFLICT", "Emotion profile 在计算期间发生变化");
                var fleets = bound.Emotion["fleets"]!.AsArray();
                string time = recordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                for (int i = 0; i < 2; i++)
                {
                    var fleet = fleets[i]!.AsObject();
                    fleet["value"] = values[i];
                    fleet["recordedAt"] = time;
                }
                bound.Emotion["nextRun"] = nextRun is { } scheduled
                    ? scheduled.ToLocalTime().ToString("O", CultureInfo.InvariantCulture) : null;
                token.ThrowIfCancellationRequested();
                workspace.WriteRaw(instance, profile);
                return ValueTask.FromResult(workspace.ReadEmotion(profile).Snapshot);
            }
        }
    }

    private static EmotionControl ParseControl(string value) => value switch
    {
        "keep_exp_bonus" => EmotionControl.KeepExpBonus,
        "prevent_green_face" => EmotionControl.PreventGreenFace,
        "prevent_yellow_face" => EmotionControl.PreventYellowFace,
        "prevent_red_face" => EmotionControl.PreventRedFace,
        _ => throw new InvalidDataException("Unknown emotion control")
    };

    private static EmotionRecovery ParseRecovery(string value) => value switch
    {
        "not_in_dormitory" => EmotionRecovery.NotInDormitory,
        "dormitory_floor_1" => EmotionRecovery.DormitoryFloor1,
        "dormitory_floor_2" => EmotionRecovery.DormitoryFloor2,
        _ => throw new InvalidDataException("Unknown emotion recovery")
    };

    private static int RequiredInt(JsonObject value, string key)
        => value[key] is JsonValue node && node.TryGetValue<int>(out int result)
            ? result : throw new InvalidDataException("Missing or invalid emotion field: " + key);
    private static bool RequiredBool(JsonObject value, string key)
        => value[key] is JsonValue node && node.TryGetValue<bool>(out bool result)
            ? result : throw new InvalidDataException("Missing or invalid emotion field: " + key);
    private static string RequiredString(JsonObject value, string key)
        => value[key] is JsonValue node && node.TryGetValue<string>(out string? result) && !string.IsNullOrWhiteSpace(result)
            ? result : throw new InvalidDataException("Missing or invalid emotion field: " + key);
}
