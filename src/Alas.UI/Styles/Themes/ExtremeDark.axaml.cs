using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alas.UI.Styles.Themes;

/// <summary>
/// Extreme.Dark.axaml 的类型化包装：XAML 在编译期解析，主题服务用静态类型引用它，
/// 避免运行时 ResourceInclude(uri) 的动态加载（裁剪不安全，IL2026）。
/// </summary>
public partial class ExtremeDark : ResourceDictionary
{
    public ExtremeDark() => AvaloniaXamlLoader.Load(this);
}