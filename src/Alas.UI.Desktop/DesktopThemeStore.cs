using System;
using System.IO;
using System.Text;
using Alas.UI.Theming;

namespace Alas.UI.Desktop;

/// <summary>
/// 桌面首选项存储：写在 &lt;LocalApplicationData&gt;/AlasSharp 下，路径由系统目录推导，不硬编码用户路径。
/// 采用「临时文件 + 覆盖移动」的原子写，避免断电/崩溃留下半截 JSON；任何读写异常都转成可展示的状态文本。
/// 只存界面偏好（主题/配色/语言相关），不保存任何凭据。
/// </summary>
public sealed class DesktopThemeStore : IThemeStore
{
    private readonly string _path;

    public DesktopThemeStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlasSharp");
        _path = Path.Combine(root, "ui-preferences.json");
    }

    public string? Status { get; private set; }

    public ThemePreference Load()
    {
        try
        {
            if (!File.Exists(_path)) return ThemePreference.Default;
            var json = File.ReadAllText(_path, Encoding.UTF8);
            Status = null;
            return ThemePreference.FromJson(json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status = $"读取界面偏好失败（{Describe(error)}），本次使用默认主题。文件：{_path}";
            return ThemePreference.Default;
        }
    }

    public void Save(ThemePreference preference)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"ui-preferences.{Environment.ProcessId}.tmp");
            File.WriteAllText(temporary, preference.ToJson(), new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
            Status = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = $"保存界面偏好失败（{Describe(error)}），重启后会回到上次成功保存的主题。文件：{_path}";
        }
    }

    private static string Describe(Exception error) => error switch
    {
        UnauthorizedAccessException => "没有写入权限",
        DirectoryNotFoundException => "目录不存在",
        _ => error.Message,
    };
}
