using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Alas.UI.Overview;

namespace Alas.UI.Browser;

[SupportedOSPlatform("browser")]
public sealed partial class BrowserResourceSelectionStore : IResourceSelectionStore
{
    public string? Read(string instance) => GetItem("azurpilot.resources." + instance);
    public void Write(string instance, string json) => SetItem("azurpilot.resources." + instance, json);
    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);
    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);
}
