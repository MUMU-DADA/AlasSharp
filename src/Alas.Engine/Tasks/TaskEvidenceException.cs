using System.Text.Json.Nodes;

namespace Alas.Engine.Tasks;

/// <summary>Preserves completed phase observations when a later device action fails.</summary>
public sealed class TaskEvidenceException(string phase, JsonObject evidence, Exception cause)
    : Exception($"Task failed during {phase}", cause)
{
    public string Phase { get; } = phase;
    public JsonObject Evidence { get; } = evidence.DeepClone().AsObject();
}
