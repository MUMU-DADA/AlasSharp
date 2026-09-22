using System.Text.Json;
using System.Text.Json.Nodes;

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
        if (root?["tasks"] is not JsonArray tasks)
            throw new ArgumentException("队列文件缺少 tasks 数组");
        var requests = new List<TaskRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tasks)
        {
            var request = new TaskRequest
            {
                Id = node?["id"]?.GetValue<string>()?.Trim() ?? "",
                Kind = node?["kind"]?.GetValue<string>()?.Trim() ?? "",
                Input = node?["input"] as JsonObject,
                Required = node?["required"]?.GetValue<bool>() ?? false,
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

    /// <summary>读断点文件里"已完成"的任务 id（文件不存在 = 空集，不是错误）。</summary>
    public static HashSet<string> ReadCompletedState(string? runDirectory)
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        if (runDirectory is null) return completed;
        string path = Path.Combine(runDirectory, "state.json");
        if (!File.Exists(path)) return completed;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["completed"] is JsonObject done)
                foreach (var (key, _) in done)
                    completed.Add(key);
        }
        catch (Exception)
        {
            // 断点文件坏了只是"续不上"，不该让整条队列失败；从这里开始重跑即可。
            return new HashSet<string>(StringComparer.Ordinal);
        }
        return completed;
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
