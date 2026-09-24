using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Alas.UI.TaskEditor;

public abstract class EditorObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected internal void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Draft text is separate from the JSON payload so intermediate numeric/JSON edits survive.</summary>
public sealed class TaskFieldViewModel : EditorObservable
{
    private readonly JsonObject _definition;
    private readonly Action _changed;
    private JsonNode? _original;
    private JsonNode? _value;
    private string _text;
    private string? _checkedScript;
    private ScriptValidation? _validation;
    private int _version;

    internal TaskFieldViewModel(string task, string group, string argument, JsonObject definition,
        JsonNode? value, Func<string, string, string> translate, Action changed)
    {
        _definition = (JsonObject)definition.DeepClone();
        _original = value?.DeepClone();
        _value = value?.DeepClone();
        _changed = changed;
        Group = group;
        Argument = argument;
        Path = $"{task}.{group}.{argument}";
        Label = translate($"{group}.{argument}.name", argument);
        Help = Regex.Replace(translate($"{group}.{argument}.help", ""), "<[^>]*>", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        var type = String(definition["type"]);
        var mode = String(definition["mode"]);
        HiddenBySchema = argument == "_info" || String(definition["display"]) == "hide";
        ReadOnly = String(definition["display"]) is "disabled" or "readonly" || type is "storage" or "stored" or "state" or "lock";
        // Matches FieldInput.tsx precedence; a boolean value still uses a switch when options exist.
        Kind = type == "storage" ? TaskFieldKind.Storage
            : mode == "restricted_lua" ? TaskFieldKind.Lua
            : mode == "yaml" || type == "yaml" ? TaskFieldKind.Yaml
            : type == "multiselect" ? TaskFieldKind.MultiSelect
            : type is "checkbox" or "bool" || value is JsonValue j && j.TryGetValue<bool>(out _) ? TaskFieldKind.Boolean
            : definition["option"] is JsonArray { Count: > 0 } ? TaskFieldKind.Select
            : type is "textarea" or "task_priority" ? TaskFieldKind.Multiline
            : type == "datetime" ? TaskFieldKind.DateTime
            : type == "password" ? TaskFieldKind.Password
            : IsNumeric(definition["value"]) || type is "number" or "int" or "float" ? TaskFieldKind.Number
            : value is JsonArray or JsonObject ? TaskFieldKind.Json
            : TaskFieldKind.Text;
        _text = Display(value);
        if (definition["option"] is JsonArray options)
            foreach (var option in options)
                Options.Add(new TaskFieldOption(option?.DeepClone(), translate($"{group}.{argument}.{String(option)}", String(option))));
    }

    public string Path { get; }
    public string Group { get; }
    public string Argument { get; }
    public string Label { get; }
    public string Help { get; }
    public TaskFieldKind Kind { get; }
    public bool ReadOnly { get; }
    public bool HiddenBySchema { get; }
    public bool IsVisible => !HiddenBySchema && !(Kind == TaskFieldKind.Storage && _value is JsonObject { Count: 0 });
    public bool IsDirty => !JsonNode.DeepEquals(_value, _original) || Error.Length > 0;
    public bool IsMultiline => Kind is TaskFieldKind.Multiline or TaskFieldKind.Json or TaskFieldKind.Yaml or TaskFieldKind.Lua or TaskFieldKind.Storage;
    public bool CanResetSchedule => Group == "Scheduler" && Argument == "NextRun" && !ReadOnly;
    // The upstream StorageField deliberately keeps its clear action enabled even when the
    // stored value itself is read-only: clearing is a supported server-side mutation.
    public bool CanClearStorage => Kind == TaskFieldKind.Storage;
    public string Error { get; private set; } = "";
    public string Text => _text;
    public JsonNode? Value => _value?.DeepClone();
    public bool BoolValue => _value is JsonValue j && j.TryGetValue<bool>(out var b) && b;
    public List<TaskFieldOption> Options { get; } = [];
    public bool IsChecking { get; private set; }
    public bool ScriptValidated => Kind != TaskFieldKind.Lua || !IsDirty || _checkedScript == Text && _validation?.Valid == true;
    public IReadOnlyList<ScriptDiagnostic> Diagnostics => _validation?.Diagnostics ?? [];
    public string ScriptStatus => IsChecking ? "正在检查…" : _validation is null ? "脚本草稿需检查后才能保存"
        : string.IsNullOrEmpty(_validation.Summary) ? (_validation.Valid ? "脚本检查通过" : "脚本检查未通过") : _validation.Summary;
    public bool HasConflict { get; private set; }
    public string RemoteText { get; private set; } = "";
    private JsonNode? _remote;

    public void SetText(string? text)
    {
        if (ReadOnly) return;
        text ??= "";
        if (_text == text) return;
        _text = text;
        Error = "";
        JsonNode? payload = JsonValue.Create(text);
        if ((Kind is TaskFieldKind.Number or TaskFieldKind.DateTime) &&
            _definition["preserve_empty"]?.GetValue<bool>() != true && string.IsNullOrWhiteSpace(text) &&
            _definition["value"] is { } fallback && String(fallback).Length > 0)
        {
            payload = fallback.DeepClone();
            _text = String(fallback);
            text = _text;
        }
        if (Kind == TaskFieldKind.Number)
        {
            var valid = Regex.IsMatch(text.Trim(), @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!valid || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                Error = "请输入有效数字";
            else if ((String(_definition["type"]) == "int" && Math.Truncate(number) != number) ||
                Math.Truncate(number) == number && Math.Abs(number) > 9007199254740991d)
                Error = "请输入安全范围内的整数";
            else if (_definition["validate"] is JsonArray { Count: >= 2 } range &&
                (number < Number(range[0]) || number > Number(range[1])))
                Error = $"数值应介于 {range[0]} 和 {range[1]} 之间";
            else payload = JsonValue.Create(number);
        }
        else if (Kind == TaskFieldKind.Json)
        {
            try { payload = JsonNode.Parse(text); }
            catch (JsonException error) { Error = $"JSON 格式错误（行 {error.LineNumber + 1}，列 {error.BytePositionInLine + 1}）"; }
        }
        // YAML and restricted Lua remain exact text. Validation/execution belongs to the backend.
        _value = payload;
        Changed();
    }

    public void SetBoolean(bool value) => SetValue(JsonValue.Create(value));
    public void SelectOption(TaskFieldOption option)
    {
        if (Options.Contains(option)) SetValue(option.Value);
    }
    public bool IsSelected(TaskFieldOption option) => _value is JsonArray array && array.Any(x => JsonNode.DeepEquals(x, option.Value));
    public void ToggleOption(TaskFieldOption option, bool selected)
    {
        if (ReadOnly || !Options.Contains(option)) return;
        var values = _value is JsonArray array ? (JsonArray)array.DeepClone() : [];
        for (var i = values.Count - 1; i >= 0; i--)
            if (JsonNode.DeepEquals(values[i], option.Value)) values.RemoveAt(i);
        if (selected) values.Add(option.Value?.DeepClone());
        SetValue(values);
    }
    public void ClearStorage()
    {
        if (!CanClearStorage) return;
        _value = new JsonObject(); _text = "{}"; Error = ""; Changed();
    }
    public void ResetSchedule() { if (CanResetSchedule) SetText(""); }
    public void Restore()
    {
        _value = _original?.DeepClone(); _text = Display(_value); Error = "";
        HasConflict = false; Changed();
    }

    internal async Task CheckScriptAsync(Func<string, CancellationToken, Task<ScriptValidation>> validate, CancellationToken token)
    {
        if (ReadOnly || Kind != TaskFieldKind.Lua || IsChecking) return;
        var version = _version;
        var script = Text;
        IsChecking = true; _validation = null; _checkedScript = null; NotifyAll();
        try
        {
            var result = await validate(script, token);
            if (_version != version) return;
            _checkedScript = script; _validation = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_version == version) _validation = new ScriptValidation(false, [new(error.Message)], "脚本检查失败");
        }
        finally { IsChecking = false; NotifyAll(); _changed(); }
    }

    internal void AcceptScript(JsonNode? saved)
    {
        _original = saved?.DeepClone();
        _value = saved?.DeepClone();
        _text = Display(saved);
        Error = "";
        _checkedScript = null;
        _validation = null;
        HasConflict = false;
        NotifyAll();
        _changed();
    }

    internal void AcceptSaved(JsonNode? saved, JsonNode? submitted)
    {
        // Edits made while a request was in flight are not overwritten by its response.
        var stillSubmitted = JsonNode.DeepEquals(_value, submitted) && Error.Length == 0;
        _original = saved?.DeepClone();
        if (stillSubmitted) { _value = saved?.DeepClone(); _text = Display(saved); }
        HasConflict = false; NotifyAll();
    }

    internal void ReconcileRemote(JsonNode? remote)
    {
        _remote = remote?.DeepClone(); RemoteText = Display(remote);
        HasConflict = IsDirty && !JsonNode.DeepEquals(remote, _original) && !JsonNode.DeepEquals(remote, _value);
        if (!IsDirty) { _value = remote?.DeepClone(); _text = Display(remote); }
        _original = remote?.DeepClone(); NotifyAll();
    }
    public void ResolveConflict(bool keepMine)
    {
        if (!HasConflict) return;
        if (!keepMine) { _value = _remote?.DeepClone(); _text = Display(_remote); Error = ""; }
        HasConflict = false; NotifyAll(); _changed();
    }

    private void SetValue(JsonNode? value)
    {
        if (ReadOnly || JsonNode.DeepEquals(value, _value)) return;
        _value = value?.DeepClone(); _text = Display(value); Error = ""; Changed();
    }
    private void Changed()
    {
        _version++; _validation = null; _checkedScript = null; NotifyAll(); _changed();
    }
    private void NotifyAll() => Notify(string.Empty);
    private static bool IsNumeric(JsonNode? value) => value is JsonValue j && j.TryGetValue<double>(out _);
    private static double Number(JsonNode? value) => double.Parse(String(value), NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static string String(JsonNode? value) => value is JsonValue j && j.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
    private static string Display(JsonNode? value) => value is JsonObject or JsonArray ? value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : String(value);
}
