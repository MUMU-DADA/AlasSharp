using System.Text;
using Avalonia.Platform.Storage;

namespace Alas.UI.Platform;

public interface IUiFiles
{
    Task<(string Name, string Content)?> OpenJsonAsync();
    Task SaveAsync(string name, string mediaType, Func<Task<byte[]>> content,
        CancellationToken cancellationToken = default);

    Task SaveTextAsync(string name, Func<Task<string>> content) =>
        SaveAsync(name, "application/json", async () => Encoding.UTF8.GetBytes(await content()));
}

/// <summary>Use Avalonia's native/browser storage provider; all chosen paths remain on the client.</summary>
public sealed class UiFiles(Func<IStorageProvider?> provider) : IUiFiles
{
    public async Task<(string Name, string Content)?> OpenJsonAsync()
    {
        var storage = provider() ?? throw new InvalidOperationException("文件选择尚未就绪");
        if (!storage.CanOpen) throw new NotSupportedException("当前环境不支持选择文件");
        var selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON 配置") { Patterns = ["*.json"], MimeTypes = ["application/json"] }],
        });
        if (selected.Count == 0) return null;
        using var file = selected[0];
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[2_000_001];
        int count = await reader.ReadBlockAsync(buffer.AsMemory());
        if (count > 2_000_000) throw new InvalidOperationException("配置文件超过 2000000 个字符");
        return (file.Name, new string(buffer, 0, count));
    }

    public async Task SaveAsync(string name, string mediaType, Func<Task<byte[]>> content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storage = provider() ?? throw new InvalidOperationException("文件保存尚未就绪");
        if (!storage.CanSave) throw new NotSupportedException("当前环境不支持保存文件");
        // Request the picker during the input event, before a network read consumes browser
        // user activation. Fetch successfully before opening/truncating the destination.
        using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出文件", SuggestedFileName = name,
            DefaultExtension = Path.GetExtension(name).TrimStart('.'), ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(mediaType)
                { Patterns = ["*" + Path.GetExtension(name)], MimeTypes = [mediaType] }],
        });
        if (file is null) throw new OperationCanceledException("已取消导出");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = await content();
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
