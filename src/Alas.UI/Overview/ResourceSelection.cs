using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.UI.ViewModels;

namespace Alas.UI.Overview;

/// <summary>Client-local preference, matching azurpilot.resources.{instance}; never a game config write.</summary>
public interface IResourceSelectionStore
{
    string? Read(string instance);
    void Write(string instance, string json);
}

public sealed class MemoryResourceSelectionStore : IResourceSelectionStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? Read(string instance) => _values.GetValueOrDefault(instance);
    public void Write(string instance, string json) => _values[instance] = json;
}

public sealed record ResourceChoice(string Key, string Label, ResourceCardViewModel Card);

/// <summary>Selection/order are independent of the latest resource observation, as in Overview.tsx.</summary>
public sealed class ResourceSelection(IResourceSelectionStore store) : INotifyPropertyChanged
{
    private static readonly string[] Defaults = ["Oil", "Coin", "Gem", "Cube"];
    private string[] _keys = [.. Defaults];
    private JsonObject? _observation;
    private string _instance = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SelectionChanged;
    public IReadOnlyList<string> Keys => Array.AsReadOnly(_keys);
    public IReadOnlyList<ResourceChoice> Selected { get; private set; } = [];
    public IReadOnlyList<ResourceChoice> Available { get; private set; } = [];
    public string StorageError { get; private set; } = "";

    public void SetInstance(string instance)
    {
        if (_instance == instance) return;
        var previousKeys = _keys;
        var previousError = StorageError;
        _instance = instance;
        _observation = null;
        _keys = [.. Defaults];
        StorageError = "";
        try
        {
            // Preserve empty selections and unknown names; only malformed preferences use defaults.
            if (JsonNode.Parse(store.Read(instance) ?? "null") is JsonArray array &&
                array.All(item => item is JsonValue value && value.TryGetValue<string>(out _)))
                _keys = array.Select(item => item!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { StorageError = "无法读取资源卡片偏好，本次使用默认搭配。"; }
        Refresh();
        NotifyPreferenceChanges(previousKeys, previousError);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Observe(JsonObject? observation)
    {
        _observation = observation;
        Refresh();
    }

    public void Add(string key)
    {
        if (Available.Any(item => item.Key == key) && !_keys.Contains(key, StringComparer.Ordinal))
            Change([.. _keys, key]);
    }
    public void Remove(string key) => Change(_keys.Where(item => item != key).ToArray());
    public void RestoreDefault() => Change([.. Defaults]);

    /// <summary>Same splice semantics as ResourceCards.tsx for drag/drop or keyboard movement.</summary>
    public void Move(string source, string target)
    {
        var keys = _keys.ToList();
        int from = keys.IndexOf(source), to = keys.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        keys.RemoveAt(from);
        keys.Insert(to, source);
        Change(keys.ToArray());
    }

    private void Change(string[] keys)
    {
        if (_instance.Length == 0) return;
        var previousKeys = _keys;
        var previousError = StorageError;
        _keys = keys.Distinct(StringComparer.Ordinal).ToArray();
        StorageError = "";
        try { store.Write(_instance, new JsonArray(_keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()).ToJsonString()); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { StorageError = "无法保存资源卡片偏好，当前选择仅在本次页面内有效。"; }
        Refresh();
        NotifyPreferenceChanges(previousKeys, previousError);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyPreferenceChanges(string[] previousKeys, string previousError)
    {
        if (!previousKeys.SequenceEqual(_keys, StringComparer.Ordinal))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keys)));
        if (previousError != StorageError)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StorageError)));
    }

    private void Refresh()
    {
        var cards = SchedulerObservation.Resources(_observation, _keys);
        var selected = _keys.Select((key, index) => new ResourceChoice(key, cards[index].Name, cards[index])).ToArray();
        var availableKeys = (_observation?["resources"] as JsonArray)?.OfType<JsonObject>()
            .Select(item => item["name"] is JsonValue name && name.TryGetValue<string>(out var key) ? key : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).Where(key => !_keys.Contains(key, StringComparer.Ordinal)).ToArray() ?? [];
        var availableCards = SchedulerObservation.Resources(_observation, availableKeys);
        var available = availableKeys.Select((key, index) => new ResourceChoice(key, availableCards[index].Name, availableCards[index])).ToArray();
        // Compare the projected display values, not mutable JSON identity or the whole observation.
        // Log-only snapshots must retain card/list identities so bound templates are not rebuilt.
        var selectedChanged = !SameDisplay(Selected, selected);
        var availableChanged = !SameDisplay(Available, available);
        if (selectedChanged) Selected = selected;
        if (availableChanged) Available = available;
        if (selectedChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        if (availableChanged) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Available)));
    }

    private static bool SameDisplay(IReadOnlyList<ResourceChoice> current, IReadOnlyList<ResourceChoice> next)
    {
        if (current.Count != next.Count) return false;
        for (var index = 0; index < current.Count; index++)
        {
            var before = current[index];
            var after = next[index];
            if (before.Key != after.Key || before.Label != after.Label
                || before.Card.Name != after.Card.Name || before.Card.Value != after.Card.Value
                || before.Card.Limit != after.Card.Limit || before.Card.Foot != after.Card.Foot
                || before.Card.TintIndex != after.Card.TintIndex || !ReferenceEquals(before.Card.Image, after.Card.Image))
                return false;
        }
        return true;
    }
}
