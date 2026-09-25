using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Alas.UI.DeploySettings;

namespace Alas.UI.Browser;

/// <summary>Tab-session storage for unconfirmed deploy edits; never uses persistent localStorage.</summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserDeployDraftStore : IDeployDraftStore
{
    public string? Read(string key)
    {
        try { return GetItem(key); }
        catch (Exception error) when (error is JSException or InvalidOperationException) { throw Unavailable(error); }
    }

    public void Write(string key, string content)
    {
        try { SetItem(key, content); }
        catch (Exception error) when (error is JSException or InvalidOperationException) { throw Unavailable(error); }
    }

    public void Remove(string key)
    {
        try { RemoveItem(key); }
        catch (Exception error) when (error is JSException or InvalidOperationException) { throw Unavailable(error); }
    }

    private static InvalidOperationException Unavailable(Exception error) =>
        new("浏览器会话存储不可用，草稿只保留在当前页面内存中。", error);

    [JSImport("globalThis.sessionStorage.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.sessionStorage.setItem")]
    private static partial void SetItem(string key, string content);

    [JSImport("globalThis.sessionStorage.removeItem")]
    private static partial void RemoveItem(string key);
}
