using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Alas.UI.Theming;

namespace Alas.UI.Browser;

/// <summary>
/// 浏览器首选项存储：写在**本源** localStorage（键 azurpilot.ui.preferences），与上游四个 azurpilot.* 键同一存储域。
/// 只走最小的 JS 桥（getItem/setItem/removeItem），不引入其它 web 依赖；异常转成可展示状态文本。
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserThemeStore : IThemeStore
{
    private const string Key = "azurpilot.ui.preferences";

    public string? Status { get; private set; }

    public ThemePreference Load()
    {
        try
        {
            var json = GetItem(Key);
            Status = null;
            return string.IsNullOrEmpty(json) ? ThemePreference.Default : ThemePreference.FromJson(json);
        }
        catch (Exception error) when (error is JSException or JsonException or InvalidOperationException)
        {
            Status = $"读取界面偏好失败（{error.Message}），本次使用默认主题。";
            return ThemePreference.Default;
        }
    }

    public void Save(ThemePreference preference)
    {
        try
        {
            SetItem(Key, preference.ToJson());
            Status = null;
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            // 隐私模式或存储配额用尽时 localStorage 会直接抛错，这里如实反馈给界面。
            Status = $"保存界面偏好失败（{error.Message}），刷新后会回到上次成功保存的主题。";
        }
    }

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);
}
