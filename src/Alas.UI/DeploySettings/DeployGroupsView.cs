using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alas.UI.DeploySettings;

/// <summary>
/// 部署设置分组渲染（上游 <c>components/DeployGroups.tsx</c> 的忠实迁移）：
/// 系统设置页渲染「除远程访问/WebUI 之外的全部」，远程访问页渲染「只有远程访问与 WebUI」——
/// 两边是**互补**关系，后端新增分组只会落到系统设置页，不会在界面上静默消失。
///
/// 每个字段一行的结构与上游一致：标签 + 说明（+ 状态/错误）、输入控件。
/// 输入即进草稿（由会话的队列负责提交与重试），行里只显示真实状态。
/// </summary>
public static class DeployGroupsView
{
    private sealed class GroupState { public IReadOnlyList<DeployGroup>? Groups; }
    private static readonly ConditionalWeakTable<StackPanel, GroupState> States = new();

    /// <summary>只在字段结构改变时重建；保存状态变化不得移除正在输入的控件和焦点。</summary>
    public static void Render(StackPanel host, IReadOnlyList<DeployGroup> groups, DeploySettingsSession session)
    {
        var state = States.GetOrCreateValue(host);
        if (SameGroups(state.Groups, groups)) return;
        state.Groups = groups;
        host.Children.Clear();
        foreach (var group in groups) host.Children.Add(BuildGroup(group, session));
    }

    private static bool SameGroups(IReadOnlyList<DeployGroup>? left, IReadOnlyList<DeployGroup> right) =>
        left is not null && left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Key == pair.Second.Key && pair.First.Title == pair.Second.Title
            && pair.First.Fields.Count == pair.Second.Fields.Count
            && pair.First.Fields.Zip(pair.Second.Fields).All(fields =>
                fields.First with { Options = null } == fields.Second with { Options = null }
                && (fields.First.Options ?? Array.Empty<string>())
                    .SequenceEqual(fields.Second.Options ?? Array.Empty<string>())));

    /// <summary>构建一个分组的可视单元：section.panel.config-group，外观取自皮肤。</summary>
    public static Control BuildGroup(DeployGroup group, DeploySettingsSession session)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var field in group.Fields) rows.Children.Add(BuildRow(field, session));
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Name = $"DeployGroupTitle{group.Key}", Text = group.Title, FontSize = 15, FontWeight = FontWeight.SemiBold,
        });
        content.Children.Add(rows);
        return new Border
        {
            Name = $"DeployGroup{group.Key}",
            Classes = { "panel" },
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content,
        };
    }

    /// <summary>构建一个字段行：标签 + 说明 + 输入控件 + 草稿状态。</summary>
    public static Control BuildRow(DeployFieldSpec field, DeploySettingsSession session)
    {
        var edits = session.Edits;
        var configured = session.CurrentValue(field.Key);
        var updating = false;

        var labels = new StackPanel { Spacing = 2 };
        labels.Children.Add(new TextBlock { Name = $"DeployFieldLabel{field.Key}", Text = field.Label, FontSize = 12 });
        if (!string.IsNullOrEmpty(field.Help))
        {
            labels.Children.Add(new TextBlock
            {
                Name = "DeployFieldHelp", Text = field.Help, FontSize = 11, Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // 状态行：已保存 / 正在保存… / 等待连接后保存… / 错误（错误另附「重试保存」）。
        var status = new TextBlock
        {
            Name = $"DeployFieldStatus{field.Key}", FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false,
        };
        var retry = new Button
        {
            Name = $"DeployFieldRetry{field.Key}", Content = "重试保存", FontSize = 11,
            Padding = new Thickness(8, 3), HorizontalAlignment = HorizontalAlignment.Left,
            // 保持可见、按失败状态禁用：完全隐藏的控件 Avalonia 不会放进可视树，
            // 于是"这个字段可以重试"既看不到、也无从核对。命令挂在按钮上（不是只挂事件），
            // 命令可达性检查据此确认界面上确有重试入口。
            IsEnabled = false,
            Command = session.RetryFailedCommand,
        };
        if (field.Key == "Password")
        {
            labels.Children.Add(new TextBlock
            {
                Name = "DeployFieldPasswordHelp", Text = "留空表示不修改现有密码。", FontSize = 11,
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
        }
        labels.Children.Add(status);
        labels.Children.Add(retry);

        var display = DeployFieldFactory.DisplayValue(field, edits, configured);
        var parent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 8,
        };
        // 控件先声明再用：输入回调里需要把它自己交回给「回落到默认值时改写输入框」。
        Control? control = null;
        control = DeployFieldFactory.Create(field, display, field.ReadOnly is false, value =>
        {
            if (updating) return;
            var prepared = DeployValuePreparer.Prepare(value, field, session.CurrentValue(field.Key));
            edits.Change(field.Key, prepared.Payload, prepared.DisplayText ?? value, prepared.Error);
            if (prepared.DisplayText is { } rewritten)
            {
                // 回落到默认值时输入框要跟着改写：否则界面显示空、实际提交的是默认值。
                updating = true;
                try { ReplaceText(control!, rewritten); }
                finally { updating = false; }
            }
        });

        DeployEdit? previousEdit = null;
        string? previousConfigured = null;
        bool? previousReady = null;
        void Refresh()
        {
            var edit = edits.Edit(field.Key);
            var currentConfigured = session.CurrentValue(field.Key);
            var ready = edits.Ready;
            if (previousReady == ready && Equals(previousEdit, edit) && previousConfigured == currentConfigured)
                return;
            previousEdit = edit;
            previousConfigured = currentConfigured;
            previousReady = ready;
            if (control is not null && !control.IsKeyboardFocusWithin)
            {
                updating = true;
                try
                {
                    DeployFieldFactory.UpdateValue(control, field,
                        DeployFieldFactory.DisplayValue(field, edits, currentConfigured));
                }
                finally { updating = false; }
            }
            switch (edit?.Status)
            {
                case DeployEditStatus.Saved:
                    status.Text = "已保存";
                    status.IsVisible = true;
                    retry.IsEnabled = false;
                    break;
                case DeployEditStatus.Saving:
                    status.Text = "正在保存…";
                    status.IsVisible = true;
                    retry.IsEnabled = false;
                    break;
                case DeployEditStatus.Queued:
                    status.Text = edits.Ready ? "正在保存…" : "等待连接后保存…";
                    status.IsVisible = true;
                    retry.IsEnabled = false;
                    break;
                case DeployEditStatus.Error:
                    // 永久校验错误保留原文供修正；只有可重试失败才给「重试保存」。
                    status.Text = "⚠ " + (edit.Error ?? "保存失败") + " 输入已保留。";
                    status.IsVisible = true;
                    retry.IsEnabled = edit.Retryable;
                    break;
                default:
                    status.IsVisible = false;
                    retry.IsEnabled = false;
                    break;
            }
            ToolTip.SetTip(status, status.Text);
            Avalonia.Automation.AutomationProperties.SetName(status, status.Text);
        }

        control!.LostFocus += (_, _) => Refresh();
        Refresh();
        void ArrangeFields(double width)
        {
            var narrow = width < 564;
            parent.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "220,*");
            Grid.SetColumn(control!, narrow ? 0 : 1);
            Grid.SetRow(control!, narrow ? 1 : 0);
        }
        parent.SizeChanged += (_, args) => ArrangeFields(args.NewSize.Width);
        ArrangeFields(0);
        parent.Children.Add(labels);
        parent.Children.Add(control!);
        var row = new StackPanel
        {
            Name = $"DeployFieldRow{field.Key}",
            Spacing = 4,
            Children = { parent },
        };
        void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(DeploySettingsSession.Groups)) Refresh();
        }
        // 旧页面/旧字段不能被会话事件永久持有；再次导航挂载时恢复订阅。
        row.AttachedToVisualTree += (_, _) =>
        {
            edits.Changed += Refresh;
            session.PropertyChanged += OnSessionChanged;
            Refresh();
        };
        row.DetachedFromVisualTree += (_, _) =>
        {
            edits.Changed -= Refresh;
            session.PropertyChanged -= OnSessionChanged;
        };
        return row;
    }

    /// <summary>把回落后的默认值写回输入控件（只处理可编辑的文本类控件）。</summary>
    private static void ReplaceText(Control control, string text)
    {
        if (control is TextBox box && box.Text != text) box.Text = text;
    }
}
