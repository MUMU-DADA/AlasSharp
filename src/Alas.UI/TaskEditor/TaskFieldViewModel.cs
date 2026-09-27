using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alas.UI.TaskEditor;

public abstract class EditorObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected internal void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Editable JSON input for one Engine runner queue entry.</summary>
public sealed class TaskFieldViewModel : EditorObservable
{
    private JsonNode? _original;
    private JsonNode? _value;
    private string _text;
    private readonly Action _changed;

    internal TaskFieldViewModel(string task, string group, string argument, JsonObject definition,
        JsonNode? value, Func<string, string, string> translate, Action changed)
    {
        _original = value?.DeepClone();
        _value = value?.DeepClone();
        _text = Display(_value);
        _changed = changed;
        Group = group;
        Argument = argument;
        Path = $"{task}.{group}.{argument}";
        Label = translate($"{group}.{argument}.name", argument);
        Help = translate($"{group}.{argument}.help", "");
        Kind = TaskFieldKind.Json;
    }

    public string Path { get; }
    public string Group { get; }
    public string Argument { get; }
    public string Label { get; }
    public string Help { get; }
    public TaskFieldKind Kind { get; }
    public bool ReadOnly => false;
    public bool HiddenBySchema => false;
    public bool IsVisible => true;
    public bool IsDirty => !JsonNode.DeepEquals(_value, _original) || Error.Length > 0;
    public bool IsMultiline => true;
    public string Error { get; private set; } = "";
    public string Text => _text;
    public JsonNode? Value => _value?.DeepClone();
    public bool BoolValue => _value is JsonValue j && j.TryGetValue<bool>(out var b) && b;
    public List<TaskFieldOption> Options { get; } = [];

    public void SetText(string? text)
    {
        text ??= "";
        if (_text == text && Error.Length == 0) return;
        _text = text;
        try
        {
            _value = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
            Error = "";
        }
        catch (JsonException error)
        {
            Error = $"JSON 格式错误（行 {error.LineNumber + 1}，列 {error.BytePositionInLine + 1}）";
        }
        Notify(string.Empty);
        _changed();
    }

    public void SetBoolean(bool value) => SetText(value ? "true" : "false");
    public void SelectOption(TaskFieldOption option) { }
    public bool IsSelected(TaskFieldOption option) => false;
    public void ToggleOption(TaskFieldOption option, bool selected) { }

    public void Restore()
    {
        _value = _original?.DeepClone();
        _text = Display(_value);
        Error = "";
        Notify(string.Empty);
        _changed();
    }

    internal void AcceptSaved(JsonNode? saved)
    {
        _original = saved?.DeepClone();
        _value = saved?.DeepClone();
        _text = Display(_value);
        Error = "";
        Notify(string.Empty);
        _changed();
    }

    private static string Display(JsonNode? value) => value is null ? "{}" :
        value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
