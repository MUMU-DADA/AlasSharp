using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record EmotionDeviceIdentity(string Serial, string Package, GameServer Server);

public sealed partial class ConfigWorkspace
{
    public static readonly SourceFile EmotionBindingSource = new("module/config/config.py",
        "fdaba7e77c5ffdca9e71a2ded80095a9ce061335d44a30861df9f9cd1963b854");
    public IEmotionStore EmotionStore(string instance, string task, EmotionDeviceIdentity? identity = null)
        => new ConfigEmotionStore(this, ValidateName(instance), ValidateTask(task), identity);

    internal static string ValidateTask(string task)
        => Regex.IsMatch(task, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z") ? task : throw new ArgumentException("Invalid configuration task");

    private sealed record BoundEmotion(EmotionSnapshot Snapshot, Dictionary<string, string> Owners);
    private BoundEmotion ReadEmotion(JsonObject merged, string task)
    {
        var (values, owners) = BindTaskGroup(merged, task, "Emotion");
        T Required<T>(string key) => values[key] is JsonValue value && value.TryGetValue<T>(out var result)
            ? result : throw new InvalidDataException("Missing or invalid emotion field: " + key);
        FleetEmotionRecord Read(int fleet)
        {
            string key = "Fleet" + fleet;
            int value = Required<int>(key + "Value");
            if (!DateTime.TryParseExact(Required<string>(key + "Record"), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var recorded))
                throw new InvalidDataException("Invalid emotion record time");
            var settings = new FleetEmotionSettings(Required<string>(key + "Control") switch
            {
                "keep_exp_bonus" => EmotionControl.KeepExpBonus, "prevent_green_face" => EmotionControl.PreventGreenFace,
                "prevent_yellow_face" => EmotionControl.PreventYellowFace, "prevent_red_face" => EmotionControl.PreventRedFace,
                _ => throw new InvalidDataException("Unknown emotion control")
            }, Required<string>(key + "Recover") switch
            {
                "not_in_dormitory" => EmotionRecovery.NotInDormitory, "dormitory_floor_1" => EmotionRecovery.DormitoryFloor1,
                "dormitory_floor_2" => EmotionRecovery.DormitoryFloor2, _ => throw new InvalidDataException("Unknown emotion recovery")
            }, Required<bool>(key + "Oath"));
            settings.Validate();
            return new(value, new DateTimeOffset(DateTime.SpecifyKind(recorded, DateTimeKind.Local)), settings);
        }
        string revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            values.ToJsonString() + string.Join('|', owners.Select(p => p.Key + ":" + p.Value)))));
        return new(new(revision, [Read(1), Read(2)]), owners);
    }
    private sealed class ConfigEmotionStore(ConfigWorkspace workspace, string instance, string task,
        EmotionDeviceIdentity? identity) : IEmotionStore
    {
        private void CheckIdentity(JsonObject merged)
        {
            if (identity is null) return;
            var emulator = merged["Alas"]?["Emulator"];
            if (string.IsNullOrWhiteSpace(identity.Serial) || string.IsNullOrWhiteSpace(identity.Package) ||
                emulator?["Serial"]?.GetValue<string>() != identity.Serial ||
                emulator?["PackageName"]?.GetValue<string>() != identity.Package ||
                GameServerRules.FromPackage(identity.Package) != identity.Server)
                throw new ConfigWorkspaceException("CONFLICT", "Emotion instance does not match the session device");
        }
        public ValueTask<EmotionSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var merged = workspace.MergeTemplate(workspace.ReadRaw(instance, out _));
                CheckIdentity(merged);
                return ValueTask.FromResult(workspace.ReadEmotion(merged, task).Snapshot);
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
                var raw = workspace.ReadRaw(instance, out _);
                var merged = workspace.MergeTemplate(raw);
                CheckIdentity(merged);
                var bound = workspace.ReadEmotion(merged, task);
                if (bound.Snapshot.Revision != expected.Revision)
                    throw new ConfigWorkspaceException("CONFLICT", "Emotion configuration changed during calculation");
                string time = recordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                for (int i = 0; i < 2; i++)
                {
                    string key = "Fleet" + (i + 1);
                    SetPath(raw, bound.Owners[key + "Value"] + ".Emotion." + key + "Value", JsonValue.Create(values[i]));
                    SetPath(raw, bound.Owners[key + "Record"] + ".Emotion." + key + "Record", JsonValue.Create(time));
                }
                if (nextRun is { } scheduled)
                {
                    if (merged[task]?["Scheduler"]?["NextRun"] is not JsonValue)
                        throw new InvalidDataException("Deferred emotion requires the task scheduler record");
                    SetPath(raw, task + ".Scheduler.NextRun", JsonValue.Create(scheduled.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                }
                token.ThrowIfCancellationRequested();
                workspace.WriteRaw(instance, raw);
                return ValueTask.FromResult(workspace.ReadEmotion(workspace.MergeTemplate(raw), task).Snapshot);
            }
        }
    }
}
