using System.Text;
using System.Text.Json;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("Alas.Engine.Cli <observe|navigate> --adb <executable> --serial <device> --server <cn|en|jp|tw> --assets <directory> --python <executable> --artifacts <directory>");
    Console.WriteLine("observe: capture and identify one frame, with device actions disabled.");
    Console.WriteLine("navigate: additionally requires --package <Android package> --page <destination>; --timeout <seconds> defaults to 120. Performs game clicks and recovery.");
    return 0;
}
try
{
    if (args[0] is not ("observe" or "navigate")) throw new ArgumentException("Unknown command");
    bool navigate = args[0] == "navigate";
    string[] required = ["--adb", "--serial", "--server", "--assets", "--python", "--artifacts", .. navigate ? new[] { "--package", "--page" } : []];
    var allowed = required.Concat(navigate ? ["--timeout"] : Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
    {
        if (!allowed.Contains(args[i]) || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            throw new ArgumentException("Unknown or incomplete argument");
        if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate argument");
    }
    if (required.Any(key => !values.ContainsKey(key))) throw new ArgumentException("Required arguments are missing");
    var server = values["--server"] switch
    {
        "cn" => GameServer.Cn, "en" => GameServer.En, "jp" => GameServer.Jp, "tw" => GameServer.Tw,
        _ => throw new ArgumentException("Unknown game server")
    };
    var timeout = values.TryGetValue("--timeout", out var seconds)
        ? TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)) : TimeSpan.FromMinutes(2);
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var result = await NavigationRun.RunAsync(new NavigationRunOptions(values["--adb"], values["--serial"], server,
            values["--assets"], values["--python"], values["--artifacts"], values.GetValueOrDefault("--page"),
            values.GetValueOrDefault("--package"), timeout), cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return result.Error is null ? 0 : 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
