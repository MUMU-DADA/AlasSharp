using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 任务队列的输入模型（队列文件 + 断点文件）。
///
/// 队列文件：
/// <code>
/// { "tasks": [ { "id": "clear-1-1", "kind": "campaign_batch",
///                "input": { "chapters": ["campaign.campaign_main.campaign_1_1"] },
///                "required": false } ] }
/// </code>
/// 队列文件**只描述"跑什么"**，不描述"怎么算成功"：判定在各域的 <see cref="ITaskRunner"/> 里。
/// 断点文件由 <see cref="TaskQueue"/> 写在工件目录里（`state.json`），`--resume` 读它。
/// </summary>
public static class TaskQueueFile
{
    public static List<TaskRequest> Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"队列文件不是合法 JSON: {e.Message}");
        }
        if (root is not JsonObject document || document["tasks"] is not JsonArray tasks)
            throw new ArgumentException("队列文件缺少 tasks 数组");
        var requests = new List<TaskRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tasks)
        {
            if (node is not JsonObject item)
                throw new ArgumentException("队列中的任务必须是 JSON 对象");
            if (item["input"] is JsonNode input && input is not JsonObject)
                throw new ArgumentException("任务 input 必须是 JSON 对象或 null");
            var request = new TaskRequest
            {
                Id = item["id"]?.GetValue<string>()?.Trim() ?? "",
                Kind = item["kind"]?.GetValue<string>()?.Trim() ?? "",
                Input = item["input"] as JsonObject,
                Required = item["required"]?.GetValue<bool>() ?? false,
            };
            if (request.Id.Length == 0)
                throw new ArgumentException("队列里有任务缺少 id");
            if (!seen.Add(request.Id))
                throw new ArgumentException($"队列里有重复的任务 id: {request.Id}");
            requests.Add(request);
        }
        if (requests.Count == 0) throw new ArgumentException("队列文件里没有任何任务");
        return requests;
    }

    /// <summary>只读取请求、前序任务及运行参数仍与当前队列一致的完成项。</summary>
    public static HashSet<string> ReadCompletedState(string? statePath,
                                                     IReadOnlyList<TaskRequest> requests,
                                                     SessionOptions options)
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        if (statePath is null) return completed;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(statePath));
            if (root is not JsonObject document || document["completed"] is not JsonObject done)
                throw new ArgumentException($"断点文件缺少 completed 对象: {statePath}");
            for (int index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                if (done[request.Id] is not JsonObject entry) continue;
                string? outcome = entry["outcome"] is JsonValue value &&
                                  value.TryGetValue<string>(out var name) ? name : null;
                if (outcome is not ("succeeded" or "dry_run" or "carried_over") ||
                    entry["identity"] is not JsonObject identity)
                    throw new ArgumentException($"断点文件包含非完成结论或缺少任务身份: {statePath}: {request.Id}");
                if (JsonNode.DeepEquals(identity, ResumeIdentity(requests, index, options)))
                    completed.Add(request.Id);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"断点文件不可读取或不是合法 JSON: {statePath}: {error.Message}", error);
        }
        return completed;
    }

    public static JsonObject ResumeIdentity(IReadOnlyList<TaskRequest> requests, int index,
                                            SessionOptions options)
    {
        var request = requests[index];
        var preceding = new JsonArray();
        for (int prior = 0; prior < index; prior++)
        {
            var earlier = requests[prior];
            preceding.Add(new JsonObject
            {
                ["id"] = earlier.Id,
                ["kind"] = earlier.Kind,
                ["required"] = earlier.Required,
                ["input"] = earlier.Input?.DeepClone(),
            });
        }
        return new JsonObject
        {
            ["kind"] = request.Kind,
            ["required"] = request.Required,
            ["input"] = request.Input?.DeepClone(),
            ["preceding_tasks"] = preceding,
            ["session"] = new JsonObject
            {
                ["repo_directory"] = options.RepoDirectory,
                ["tools_directory"] = options.ToolsDirectory,
                ["data_directory"] = options.DataDirectory,
                ["adb_path"] = options.AdbPath,
                ["serial"] = options.Serial,
                ["screenshot_backend"] = options.ScreenshotBackend,
                ["control_backend"] = options.ControlBackend,
                ["dry_run"] = options.DryRun,
                ["allow_actions"] = options.AllowActions,
                ["read_only_device"] = options.ReadOnlyDevice,
                ["max_seconds"] = options.MaxSeconds,
                ["max_rounds"] = options.MaxRounds,
                ["repeat_until_cleared"] = options.RepeatUntilCleared,
                ["clear_all"] = options.ClearAll,
                ["fleet1"] = options.Fleet1,
                ["fleet2"] = options.Fleet2,
                ["submarine_fleet"] = options.SubmarineFleet,
            },
        };
    }

    /// <summary>
    /// 找**上一次**运行的断点文件。
    ///
    /// 为什么需要它：每次运行的工件落在 `<artifacts>/<时间戳>/` 下，所以"本次运行目录"里
    /// 永远不会有上一轮的 `state.json` —— 直接读本次目录等于 `--resume` 从来没生效过。
    /// 这里按目录名（时间戳）取最新的一份，并排除本次运行目录。
    /// </summary>
    public static string? LatestState(string? artifactsRoot, string? excludeDirectory)
    {
        if (artifactsRoot is null || !Directory.Exists(artifactsRoot)) return null;
        string exclude = excludeDirectory is null
            ? "" : Path.GetFullPath(excludeDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var directory in Directory.GetDirectories(artifactsRoot)
                     // 与 `report` / `runs` 同一口径：只认真正的运行目录。否则一个手工备份目录
                     // 或拷贝出来的旧运行目录（里面也带 state.json）会把 `--resume` 引到别的运行上，
                     // 表现为"跳过了一批其实没跑过的任务"——静默跳过正是最难查的那种错。
                     .Where(Alas.Runtime.RunReport.IsRunDirectory)
                     .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            if (exclude.Length > 0 &&
                string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                              exclude, StringComparison.OrdinalIgnoreCase))
                continue;
            string path = Path.Combine(directory, "state.json");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
