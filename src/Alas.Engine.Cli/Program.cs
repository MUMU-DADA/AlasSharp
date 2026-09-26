using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("Alas.Engine.Cli observe --adb <executable> --serial <device> --server <cn|en|jp|tw> --assets <assets directory> --python <executable> --artifacts <directory>");
    Console.WriteLine("Captures and identifies one frame through the new C# engine. Read-only; no game task, navigation or settlement claim.");
    return 0;
}
try
{
    if (args[0] != "observe") throw new ArgumentException("The only implemented diagnostic command is observe");
    string[] required = ["--adb", "--serial", "--server", "--assets", "--python", "--artifacts"];
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
    {
        if (!required.Contains(args[i], StringComparer.Ordinal) || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            throw new ArgumentException("Unknown or incomplete observe argument");
        if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate observe argument");
    }
    if (required.Any(key => !values.ContainsKey(key))) throw new ArgumentException("All observe arguments are required");
    var server = values["--server"] switch
    {
        "cn" => GameServer.Cn, "en" => GameServer.En, "jp" => GameServer.Jp, "tw" => GameServer.Tw,
        _ => throw new ArgumentException("Unknown game server")
    };
    string artifacts = Path.Combine(Path.GetFullPath(values["--artifacts"]), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(artifacts);
    await using var vision = new PythonTemplateVision(Path.GetFullPath(values["--python"]),
        Path.Combine(AppContext.BaseDirectory, "Imaging", "Worker", "vision_worker.py"));
    var driver = new UiDriver(server, new AdbDevice(values["--adb"], values["--serial"], allowActions: false),
        vision, new AssetFiles(values["--assets"]));
    try
    {
        var pages = await new PageObserver(driver, UpstreamPages.Create()).ObserveAsync();
        var frame = driver.Frame ?? throw new InvalidOperationException("Observation has no frame");
        await File.WriteAllBytesAsync(Path.Combine(artifacts, "frame.png"), frame.Png.ToArray());
        var result = new
        {
            engine = "Alas.Engine", operation = "observe", actionsAllowed = false, server = values["--server"],
            frame.Sequence, frame.CapturedAt, frameSha256 = Convert.ToHexStringLower(SHA256.HashData(frame.Png.Span)),
            pages, meaning = "single_frame_observation"
        };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "observation.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { pages, artifacts, actions = 0 }));
        return 0;
    }
    catch (Exception error)
    {
        string? failureFrame = null;
        if (driver.Frame is { } frame)
        {
            failureFrame = "failure.png";
            await File.WriteAllBytesAsync(Path.Combine(artifacts, failureFrame), frame.Png.ToArray());
        }
        await File.WriteAllTextAsync(Path.Combine(artifacts, "failure.json"), JsonSerializer.Serialize(new
        {
            error = error.ToString(), failure_frames = failureFrame is null ? Array.Empty<string>() : new[] { failureFrame }
        }));
        throw;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
