using System.Text;
using System.Text.RegularExpressions;

namespace Alas.Engine.Devices;

public interface IApplicationHealth
{
    ValueTask<bool> IsRunningAsync(CancellationToken token);
    ValueTask StopAsync(CancellationToken token);
    ValueTask RefreshOrientationAsync(CancellationToken token);
}

/// <summary>Application health belongs to C# device control, never to the vision service.</summary>
// Ported source snapshots:
// module/device/app_control.py: 3d66546a762a9dc73ea89bd7618e249d5c7da9350895526c40b9ce901eff8033
// module/device/method/adb.py: 37dc092c025ba442250f9012a438cd55dfcc65a32f939acf66aba4f8a863b2f1
// module/device/connection.py: 358a3a229e1208ea7c8c832584e44eb9204cf6b43466ae5460f391ea573fd0b7
public sealed class AdbApplication : IApplicationHealth
{
    private readonly string _executable, _serial, _package;
    private readonly bool _allowActions;
    private readonly IProcessRunner _process;
    public int? Orientation { get; private set; }
    public bool OrientationWasReported { get; private set; }
    public AdbApplication(string executable, string serial, string package, bool allowActions, IProcessRunner? process = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (!Regex.IsMatch(package, @"\A[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid Android application package", nameof(package));
        (_executable, _serial, _package, _allowActions) = (executable, serial, package, allowActions);
        _process = process ?? new ProcessRunner();
    }
    private Task<ProcessResponse> RunAsync(string[] arguments, CancellationToken token)
        => _process.RunAsync(_executable, ["-s", _serial, "shell", .. arguments], TimeSpan.FromSeconds(30), token);
    public async ValueTask<bool> IsRunningAsync(CancellationToken token)
    {
        // AppControl.app_is_running compares the foreground application, not mere process existence.
        // Preserve Adb.app_current_adb's window-first and last-activity fallback order.
        var focused = Regex.Match(await QueryAsync(["dumpsys", "window", "windows"], token),
            @"mCurrentFocus=Window{.*\s+(?<package>[^\s]+)/(?<activity>[^\s]+)\}", RegexOptions.CultureInvariant);
        if (focused.Success) return focused.Groups["package"].Value == _package;
        var activities = Regex.Matches(await QueryAsync(["dumpsys", "activity", "top"], token),
            @"ACTIVITY (?<package>[^\s]+)/(?<activity>[^/\s]+) \w+ pid=(?<pid>\d+)", RegexOptions.CultureInvariant);
        if (activities.Count == 0) throw new IOException("Cannot determine the focused Android application");
        return activities[^1].Groups["package"].Value == _package;
    }
    private async ValueTask<string> QueryAsync(string[] arguments, CancellationToken token)
    {
        var result = await RunAsync(arguments, token);
        if (result.ExitCode != 0) throw new IOException("Cannot inspect Android application: " + result.Error);
        return Encoding.UTF8.GetString(result.Output);
    }
    public async ValueTask StopAsync(CancellationToken token)
    {
        if (!_allowActions) throw new InvalidOperationException("Device actions are disabled for this session");
        var result = await RunAsync(["am", "force-stop", _package], token);
        if (result.ExitCode != 0) throw new IOException("Cannot stop Android application: " + result.Error);
    }
    public async ValueTask RefreshOrientationAsync(CancellationToken token)
    {
        // Connection.get_orientation assumes normal orientation when the display report is missing/invalid.
        var match = Regex.Match(await QueryAsync(["dumpsys", "display"], token),
            @".*DisplayViewport{.*valid=true, .*orientation=(?<orientation>\d+), .*deviceWidth=(?<width>\d+), deviceHeight=(?<height>\d+).*",
            RegexOptions.CultureInvariant);
        OrientationWasReported = match.Success && int.TryParse(match.Groups["orientation"].Value, out int parsed) && parsed is >= 0 and <= 3;
        Orientation = OrientationWasReported ? int.Parse(match.Groups["orientation"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }
}

public sealed class GameNotRunningException(string message) : Exception(message);
public sealed class HumanTakeoverRequiredException(string message) : Exception(message);
