using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Alas.UI.Settings;

namespace Alas.UI.EngineSettings;

/// <summary>
/// 后端设置快照 → 共享设置会话模型的适配器。
///
/// 系统设置页与远程访问页共用同一份会话与同一条草稿队列，翻译只有这一处；
/// 页面、外壳接线和离屏检查都从这里取模型，避免各自再写一份而在分组键或字段类型上漂移。
/// </summary>
public static class SettingsEditorSchemaAdapter
{
    /// <summary>
    /// 翻译快照。返回 null 表示后端还没给出数据（页面保持加载态）。
    /// 后端把整次读取标为失败（有错误且没有任何分组）时抛出，交给会话走上游的 error 通道。
    /// </summary>
    public static SettingsEditorSchema? ToSchema(SettingsSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        if (!string.IsNullOrEmpty(snapshot.Error) && !snapshot.HasGroups)
        {
            throw new InvalidOperationException(snapshot.Error);
        }
        var groups = snapshot.Groups.Select(group => new SettingsEditorGroup(
            group.GroupKey,
            group.Title,
            group.Fields.Select(field => new SettingsEditorFieldSpec(
                field.Key, field.Label, field.Kind, field.Help, field.Options,
                PreserveEmpty: false, field.Min, field.Max, field.IsInteger || field.Kind == "int",
                ReadOnly: !field.Editable)).ToList())).ToList();
        var values = new Dictionary<string, string?>();
        foreach (var field in snapshot.Groups.SelectMany(group => group.Fields))
        {
            values[field.Key] = snapshot.Value(field.Key) ?? field.Value;
        }
        return new SettingsEditorSchema(groups, values, SettingsEditorRemoteStatus.Disabled, snapshot.Error);
    }

    /// <summary>
    /// 把一次后端读取包成会话用的读函数：页面与检查都走这条路径，读取语义只有一份。
    /// </summary>
    public static Func<CancellationToken, Task<SettingsEditorSchema?>> Read(
        Func<CancellationToken, Task<SettingsSnapshot?>> read) =>
        async cancellationToken => ToSchema(await read(cancellationToken));
}
