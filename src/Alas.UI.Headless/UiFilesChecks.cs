using System.Text;
using System.Reflection;
using Avalonia.Platform.Storage;
using Alas.UI.Platform;

namespace Alas.UI.Headless;

internal static class UiFilesChecks
{
    public static async Task Verify()
    {
        var file = new MemoryFile();
        var provider = new MemoryProvider { File = file.Item };
        IUiFiles files = new UiFiles(() => provider.Provider);
        int reads = 0;
        var picker = new TaskCompletionSource<IStorageFile?>();
        provider.SaveResult = picker.Task;
        var save = files.SaveAsync("fixture.json", "application/json", () =>
        { reads++; return Task.FromResult(Encoding.UTF8.GetBytes("配置")); });
        Check(provider.SaveCalls == 1 && reads == 0 && file.WriteCalls == 0,
            "save picker opens before reading content or touching the destination");
        picker.SetResult(file.Item);
        await save;
        Check(Encoding.UTF8.GetString(file.Written!.ToArray()) == "配置" && file.Written.ToArray().Length == 6,
            "UTF-8 output truncates stale trailing bytes and preserves non-ASCII content");

        provider.SaveResult = Task.FromResult<IStorageFile?>(null);
        await Expect<OperationCanceledException>(() => files.SaveAsync("cancel.json", "application/json", () =>
        { reads++; return Task.FromResult(Array.Empty<byte>()); }));
        Check(reads == 1 && file.WriteCalls == 1, "cancelled picker does not read or truncate");
        provider.SaveResult = null;
        await Expect<IOException>(() => files.SaveAsync("failed.json", "application/json", () =>
            Task.FromException<byte[]>(new IOException("fixture read failed"))));
        Check(file.WriteCalls == 1, "failed content read leaves the destination unopened");
        byte[] binary = [0, 255, 137, 80, 78, 71];
        await files.SaveAsync("fixture.png", "image/png", () => Task.FromResult(binary));
        Check(file.Written!.ToArray().SequenceEqual(binary), "binary chart export is not transcoded as text");

        file.Input = [..Encoding.UTF8.GetPreamble(), ..Encoding.UTF8.GetBytes("{\"name\":\"配置\"}")];
        var opened = await files.OpenJsonAsync();
        Check(opened is { Name: "fixture.json", Content: "{\"name\":\"配置\"}" }, "import consumes the BOM and only returns file name and content");
        file.Input = Encoding.UTF8.GetBytes(new string('a', 2_000_001));
        await Expect<InvalidOperationException>(async () => { await files.OpenJsonAsync(); });
        provider.File = null;
        Check(await files.OpenJsonAsync() is null, "cancelled import returns no file");
        Console.WriteLine("PASS: platform file order, cancellation, failed reads, bounded imports and binary exports");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    // Avalonia marks these interfaces as non-implementable in C#; a dynamic test double
    // supplies only picker/stream operations, without loading a native dialog backend.
    public class StorageProxy : DispatchProxy
    {
        public Func<string, object?> Operation = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Operation(method!.Name);
        public static T For<T>(Func<string, object?> operation) where T : class
        {
            T proxy = Create<T, StorageProxy>();
            ((StorageProxy)(object)proxy).Operation = operation;
            return proxy;
        }
    }

    private sealed class MemoryFile
    {
        public readonly IStorageFile Item;
        public byte[] Input = [];
        public MemoryStream? Written;
        public int WriteCalls;
        public MemoryFile() => Item = StorageProxy.For<IStorageFile>(name => name switch
        {
            "get_Name" => "fixture.json",
            "OpenReadAsync" => Task.FromResult<Stream>(new MemoryStream(Input)),
            "OpenWriteAsync" => OpenWrite(),
            "Dispose" => null,
            _ => throw new NotSupportedException(name),
        });
        private Task<Stream> OpenWrite()
        {
            WriteCalls++;
            Written = new MemoryStream(); Written.Write(new byte[100]); Written.Position = 0;
            return Task.FromResult<Stream>(Written);
        }
    }

    private sealed class MemoryProvider
    {
        public readonly IStorageProvider Provider;
        public IStorageFile? File;
        public Task<IStorageFile?>? SaveResult;
        public int SaveCalls;
        public MemoryProvider() => Provider = StorageProxy.For<IStorageProvider>(name => name switch
        {
            "get_CanOpen" or "get_CanSave" => true,
            "OpenFilePickerAsync" => Task.FromResult<IReadOnlyList<IStorageFile>>(File is null ? [] : [File]),
            "SaveFilePickerAsync" => Save(),
            _ => throw new NotSupportedException(name),
        });
        private Task<IStorageFile?> Save() { SaveCalls++; return SaveResult ?? Task.FromResult(File); }
    }
}
