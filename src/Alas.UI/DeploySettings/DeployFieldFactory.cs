using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alas.UI.DeploySettings;

/// <summary>一次输入的准备结果：提交值、要不要改写输入框显示的原文、以及本地校验错误。</summary>
public sealed record DeployPrepared(string Payload, string? DisplayText = null, string? Error = null);

/// <summary>
/// 输入值的归一化与校验（上游 <c>config/editors.ts</c> 的 <c>prepareValue</c>，逐条保留语义）：
///
/// ① 数值/时间的原文与提交值分开，保留负号、小数点等中间态；
/// ② 数值或时间被清空且字段没有 preserve_empty 时回落到原值并改写输入框 —— 配置加载时
///    `config_update()` 也把空值还原成默认值，两者必须一致；
/// ③ 非法数字 → invalidNumber；整数字段的小数或超精度 → invalidInteger；
///    超出 [Min, Max] → validateRange；错误时**保留用户原文**供修正。
/// </summary>
public static class DeployValuePreparer
{
    private static readonly Regex NumberPattern =
        new(@"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.Compiled);

    public static DeployPrepared Prepare(string? value, DeployFieldSpec field, string? current)
    {
        var text = value ?? string.Empty;
        var numeric = IsNumeric(field);
        if ((numeric || field.Kind == "datetime") && !field.PreserveEmpty && text.Trim().Length == 0)
        {
            var fallback = current ?? string.Empty;
            if (fallback.Length > 0) return new DeployPrepared(fallback, fallback);
        }
        if (!numeric) return new DeployPrepared(text);
        var trimmed = text.Trim();
        if (!NumberPattern.IsMatch(trimmed)
            || !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || double.IsNaN(number) || double.IsInfinity(number))
        {
            return new DeployPrepared(text, null, "请输入完整、有效的数字；输入内容已保留。");
        }
        if (field.Integer && number != Math.Floor(number))
        {
            return new DeployPrepared(text, null, "请输入有效整数；输入内容已保留。");
        }
        if (field.Integer && Math.Abs(number) > 9007199254740991d)
        {
            return new DeployPrepared(text, null, "请输入有效整数；输入内容已保留。");
        }
        if (field.Min is { } min && number < min)
        {
            return new DeployPrepared(text, null, RangeMessage(min, field.Max));
        }
        if (field.Max is { } max && number > max)
        {
            return new DeployPrepared(text, null, RangeMessage(field.Min, max));
        }
        return new DeployPrepared(NumberText(number));
    }

    private static bool IsNumeric(DeployFieldSpec field) =>
        field.Kind is "number" or "int" or "float";

    private static string NumberText(double number) =>
        number == Math.Floor(number) && Math.Abs(number) < 9007199254740991d
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);

    private static string RangeMessage(double? min, double? max) =>
        $"请输入 {(min is { } low ? low.ToString("0.###", CultureInfo.InvariantCulture) : "最小")} 到 " +
        $"{(max is { } high ? high.ToString("0.###", CultureInfo.InvariantCulture) : "最大")} 之间的数值。";
}

/// <summary>
/// 按字段类型生成输入控件（上游 <c>components/FieldInput.tsx</c> 的分支，顺序与判定逐条对齐）：
/// 只读时间 → 只读原文；yaml → 多行代码框；multiselect → 复选组；
/// 布尔 → 开关；有 options → 下拉（当前值不在选项里时补一个「未设置」项）；
/// textarea/task_priority → 多行；password → 密码框；其余按数值或文本。
///
/// 控件只把「用户输入了什么」交回调用方，提交与重试由草稿队列负责——这与上游一致：
/// 字段控件不自己保存，保存队列在页面之外。
/// </summary>
public static class DeployFieldFactory
{
    /// <summary>
    /// 生成输入控件。<paramref name="onInput"/> 收到用户输入的原文（每次改动都调用，立即进草稿）。
    /// </summary>
    public static Control Create(DeployFieldSpec field, string? value, bool editable, Action<string> onInput)
    {
        var text = value ?? string.Empty;
        if (field.Kind == "datetime" && !editable)
        {
            return new TextBox
            {
                Text = text.Replace('T', ' '), FontSize = 12, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch, IsReadOnly = true, IsEnabled = false,
            };
        }
        if (field.Kind is "yaml" or "textarea" or "task_priority")
        {
            var multiline = new TextBox
            {
                Text = text, FontSize = 12, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 80,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsEnabled = editable,
            };
            if (field.Kind == "yaml") multiline.FontFamily = new FontFamily("Consolas, Menlo, monospace");
            Watch(multiline, editable, onInput);
            return multiline;
        }
        if (field.Kind == "multiselect")
        {
            var selected = Split(text);
            var host = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var option in field.Options ?? Array.Empty<string>())
            {
                var item = new CheckBox
                {
                    Content = option, IsEnabled = editable, IsChecked = selected.Contains(option), FontSize = 12,
                    Margin = new Thickness(0, 0, 10, 6),
                };
                if (editable)
                {
                    item.PropertyChanged += (_, args) =>
                    {
                        if (args.Property != CheckBox.IsCheckedProperty) return;
                        var chosen = host.Children.OfType<CheckBox>()
                            .Where(box => box.IsChecked == true)
                            .Select(box => box.Content?.ToString() ?? string.Empty)
                            .Where(chosenOption => chosenOption.Length > 0);
                        onInput(string.Join(",", chosen));
                    };
                }
                host.Children.Add(item);
            }
            return host;
        }
        if (field.Kind is "checkbox" or "bool")
        {
            var on = text is "true" or "True" or "1";
            var toggle = new CheckBox
            {
                IsChecked = on, IsEnabled = editable, Content = on ? "开" : "关", FontSize = 12,
            };
            if (editable)
            {
                toggle.PropertyChanged += (_, args) =>
                {
                    if (args.Property != CheckBox.IsCheckedProperty) return;
                    var nowOn = toggle.IsChecked == true;
                    toggle.Content = nowOn ? "开" : "关";
                    onInput(nowOn ? "true" : "false");
                };
            }
            return toggle;
        }
        if (field.Options is { Count: > 0 })
        {
            var combo = new ComboBox { MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = editable, FontSize = 12 };
            var options = field.Options.ToList();
            var known = options.Contains(text);
            if (!known) combo.Items.Add(text.Length == 0 ? "未设置" : text);
            foreach (var option in options) combo.Items.Add(option);
            combo.SelectedIndex = known ? options.IndexOf(text) + (known ? 0 : 1) : 0;
            if (editable)
            {
                combo.PropertyChanged += (_, args) =>
                {
                    if (args.Property != ComboBox.SelectedItemProperty) return;
                    if (combo.SelectedItem is not string chosen) return;
                    // 「未设置」占位项代表原来的空值，不能被当成一个真实选项提交。
                    if (!known && combo.SelectedIndex == 0) return;
                    onInput(chosen);
                };
            }
            return combo;
        }
        var input = new TextBox
        {
            Text = field.Kind == "datetime" ? text.Replace('T', ' ') : text, FontSize = 12, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = editable,
            PasswordChar = field.Kind == "password" ? '•' : '\0',
        };
        Watch(input, editable, onInput);
        return input;
    }

    /// <summary>读取回执更新现有控件；调用方抑制此更新触发的输入回调。</summary>
    public static void UpdateValue(Control control, DeployFieldSpec field, string? value)
    {
        var text = value ?? string.Empty;
        switch (control)
        {
            case TextBox box:
                var shown = field.Kind == "datetime" ? text.Replace('T', ' ') : text;
                if (box.Text != shown) box.Text = shown;
                break;
            case CheckBox toggle:
                var on = text is "true" or "True" or "1";
                toggle.IsChecked = on;
                toggle.Content = on ? "开" : "关";
                break;
            case ComboBox combo:
                var option = combo.Items.OfType<string>().FirstOrDefault(item => item == text);
                if (option is null)
                {
                    option = text.Length == 0 ? "未设置" : text;
                    if (!combo.Items.Contains(option)) combo.Items.Add(option);
                }
                combo.SelectedItem = option;
                break;
            case WrapPanel multi:
                var selected = Split(text);
                foreach (var item in multi.Children.OfType<CheckBox>())
                    item.IsChecked = selected.Contains(item.Content?.ToString() ?? string.Empty);
                break;
        }
    }

    /// <summary>
    /// 取值用于展示：草稿原文优先，否则配置值（上游 <c>edits.edits[key]?.value ?? field.value</c>）。
    /// </summary>
    public static string? DisplayValue(DeployFieldSpec field, DeployEditQueue edits, string? configured) =>
        edits.Edit(field.Key)?.Value ?? configured;

    private static void Watch(TextBox box, bool editable, Action<string> onInput)
    {
        if (!editable) return;
        // 用属性变更管线而不是 TextChanged：后者在离屏（无渲染循环）里由程序化赋值时不触发。
        box.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty) onInput(box.Text ?? string.Empty);
        };
    }

    private static List<string> Split(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}
