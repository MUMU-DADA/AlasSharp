using System.Collections.Concurrent;

namespace Alas.UI.DeploySettings;

/// <summary>Process-session draft storage; contents never leave memory.</summary>
public sealed class MemoryDeployDraftStore : IDeployDraftStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Read(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Write(string key, string content) => _values[key] = content;

    public void Remove(string key) => _values.TryRemove(key, out _);
}
