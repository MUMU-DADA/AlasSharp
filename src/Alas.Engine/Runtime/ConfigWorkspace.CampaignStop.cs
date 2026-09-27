using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class ConfigWorkspace
{
    private sealed record BoundGroup(JsonObject Values, Dictionary<string, string> Owners);
    private static BoundGroup BindTaskGroup(JsonObject merged, string task, string groupName)
    {
        if (merged[task] is not JsonObject) throw new ArgumentException("Configuration task is absent");
        var tasks = new List<string> { "General", "Alas" };
        if (task.StartsWith("Opsi", StringComparison.Ordinal)) tasks.Add("OpsiGeneral");
        if (task.StartsWith("Event", StringComparison.Ordinal) || task.StartsWith("Raid", StringComparison.Ordinal) ||
            task.StartsWith("Coalition", StringComparison.Ordinal) || task is "MaritimeEscort" or "GemsFarming")
            tasks.AddRange(["TaskBalancer", "EventGeneral"]);
        tasks.Add(task);
        var values = new JsonObject(); var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in tasks)
            if (merged[name]?[groupName] is JsonObject group)
                foreach (var field in group)
                    if (owners.TryAdd(field.Key, name)) values[field.Key] = field.Value?.DeepClone();
        return new(values, owners);
    }
    public ICampaignStopStore CampaignStopStore(string instance, string task, EmotionDeviceIdentity identity)
        => new ConfigCampaignStopStore(this, ValidateName(instance), ValidateTask(task), identity);
    private sealed record BoundStop(CampaignStopSnapshot Snapshot, string NameOwner, string EnableOwner);
    private static BoundStop ReadStop(JsonObject merged, string task)
    {
        var campaign = BindTaskGroup(merged, task, "Campaign");
        var scheduler = BindTaskGroup(merged, task, "Scheduler");
        string Required(string field) => campaign.Values[field] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidDataException("Campaign stop requires " + field);
        string folder = Required("Event"), name = Required("Name");
        if (scheduler.Values["Enable"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var enable))
            throw new InvalidDataException("Campaign stop requires the bound Scheduler.Enable flag");
        var identity = new JsonObject { ["folder"] = folder, ["stage"] = name, ["enabled"] = enable,
            ["folderOwner"] = campaign.Owners["Event"], ["stageOwner"] = campaign.Owners["Name"], ["enableOwner"] = scheduler.Owners["Enable"] };
        string revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToJsonString())));
        return new(new(revision, folder, name, enable), campaign.Owners["Name"], scheduler.Owners["Enable"]);
    }
    private sealed class ConfigCampaignStopStore(ConfigWorkspace workspace, string instance, string task,
        EmotionDeviceIdentity identity) : ICampaignStopStore
    {
        private void CheckIdentity(JsonObject merged)
        {
            var emulator = merged["Alas"]?["Emulator"];
            if (string.IsNullOrWhiteSpace(identity.Serial) || string.IsNullOrWhiteSpace(identity.Package) ||
                emulator?["Serial"]?.GetValue<string>() != identity.Serial || emulator?["PackageName"]?.GetValue<string>() != identity.Package ||
                GameServerRules.FromPackage(identity.Package) != identity.Server)
                throw new ConfigWorkspaceException("CONFLICT", "Achievement instance does not match the session device");
        }
        public ValueTask<CampaignStopSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var merged = workspace.MergeTemplate(workspace.ReadRaw(instance, out _));
                CheckIdentity(merged);
                return ValueTask.FromResult(ReadStop(merged, task).Snapshot);
            }
        }
        public ValueTask SaveAsync(CampaignStopSnapshot expected, string? nextStage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (nextStage is not null && string.IsNullOrWhiteSpace(nextStage)) throw new ArgumentException("Next stage is empty");
            lock (workspace._gate)
            {
                using var transaction = workspace.AcquireTransaction(instance);
                var raw = workspace.ReadRaw(instance, out _); var merged = workspace.MergeTemplate(raw);
                CheckIdentity(merged);
                var bound = ReadStop(merged, task);
                if (bound.Snapshot != expected) throw new ConfigWorkspaceException("CONFLICT", "Campaign configuration changed during achievement observation");
                if (nextStage is null) SetPath(raw, bound.EnableOwner + ".Scheduler.Enable", JsonValue.Create(false));
                else SetPath(raw, bound.NameOwner + ".Campaign.Name", JsonValue.Create(nextStage));
                token.ThrowIfCancellationRequested();
                workspace.WriteRaw(instance, raw);
                return ValueTask.CompletedTask;
            }
        }
    }
}
