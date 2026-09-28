using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Devices;

/// <summary>The upstream ADB drag fallback, including its destination click. This is not minitouch.</summary>
public sealed class AdbFleetDrag(IGameDevice device, IUiDriver ui)
{
    public static readonly SourceFile Source = new("module/device/control.py",
        "6c66e68c85fb7daf86d86f7623512776227dc05a53dcdfcb45592697f55fe6b8");
    public async ValueTask DragAsync(PixelPoint start, PixelPoint end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await device.SwipeAsync(start, end, TimeSpan.FromMilliseconds(500), "DRAG", token);
        var area = new Rectangle(end.X - 10, end.Y - 10, end.X + 10, end.Y + 10);
        if (ui is UiDriver driver) await driver.ClickNamedAreaAsync(area, null, token);
        else await ui.ClickAreaAsync(area, token);
    }
}
