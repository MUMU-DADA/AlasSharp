using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alas.Core.Diagnostics;

/// <summary>
/// 从**一次运行目录**（`--artifacts` 写出的那一份）解析出对照所需的信息，省掉真机验证时手填路径。
///
/// 解析优先级与来源（每一项都回填到 <see cref="Source"/>，输出里会打印，不做隐式猜测）：
/// <list type="number">
///   <item>章节模块名：`queue.json` 的 `tasks[].input.chapters[0]`；没有则用 `task-*.json` 的 `chapter`；</item>
///   <item>运行开关：`queue.json` 的 `input.clear_all` → `clear_all` 变体；`fleet1/fleet2/submarine` 只作为
///         "这一局用了哪些舰队位"的记录，**不当作舰队所在格**（那要识别结果，除非显式给 `--fleet-1`）；</item>
///   <item>上游日志：显式 `--log`，或运行目录内唯一的 `*.log`/`*.txt`。
///         多个候选必须显式选择；shadow 观测和引擎共享日志不能证明动作日志归属。</item>
/// </list>
/// 找不到章节或日志时**返回 null 并给出原因**，由调用方报错——不猜一个关卡来凑。
/// </summary>
internal sealed record CampaignRunDirectoryInput(
    string RunDirectory,
    string? ChapterModule,
    string? LogPath,
    bool ClearAll,
    IReadOnlyList<string> Source);

internal static class CampaignRunDirectory
{
    public static bool TryResolve(string runDirectory, string repoDirectory, string? explicitLog,
                                  out CampaignRunDirectoryInput? input, out string? error)
    {
        // repoDirectory 仅保留调用兼容；不能从共享引擎目录猜测本次运行日志。
        input = null;
        error = null;
        if (!Directory.Exists(runDirectory))
        {
            error = $"找不到运行目录：{runDirectory}";
            return false;
        }

        var source = new List<string>();
        string? chapterModule = null;
        bool clearAll = false;

        string queuePath = Path.Combine(runDirectory, "queue.json");
        if (File.Exists(queuePath))
        {
            try
            {
                var queue = JsonNode.Parse(File.ReadAllText(queuePath))?["tasks"] as JsonArray;
                var task = queue?.FirstOrDefault()?["input"] as JsonObject;
                chapterModule = (task?["chapters"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
                clearAll = task?["clear_all"]?.GetValue<bool>() ?? false;
                if (chapterModule is not null) source.Add("章节来自 queue.json");
            }
            catch (Exception failure) when (failure is JsonException or IOException or InvalidOperationException)
            {
                source.Add("queue.json 解析失败，改用 task 工件");
            }
        }
        if (chapterModule is null)
        {
            string? taskArtifact = Directory.GetFiles(runDirectory, "task-*.json").FirstOrDefault();
            if (taskArtifact is not null)
            {
                try
                {
                    chapterModule = JsonNode.Parse(File.ReadAllText(taskArtifact))?["chapter"]?.GetValue<string>();
                    if (chapterModule is not null) source.Add("章节来自 task 工件");
                }
                catch (Exception failure) when (failure is JsonException or IOException or InvalidOperationException)
                {
                    // 落到下面的"找不到章节"分支
                }
            }
        }
        if (string.IsNullOrEmpty(chapterModule))
        {
            error = $"运行目录 {runDirectory} 里没有可用的章节信息（queue.json / task-*.json 都没有）";
            return false;
        }

        string? logPath = null;
        if (!string.IsNullOrEmpty(explicitLog))
        {
            if (Path.GetFileName(explicitLog).StartsWith("shadow-", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(explicitLog).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                error = "shadow-*.json 是钩子观测工件，不能作为动作日志；请用 --log 指定上游动作日志";
                return false;
            }
            logPath = File.Exists(explicitLog) ? explicitLog : null;
            if (logPath is null)
            {
                error = $"指定的日志不存在：{explicitLog}";
                return false;
            }
            source.Add("日志来自 --log");
        }
        if (logPath is null)
        {
            string[] candidates = Directory.GetFiles(runDirectory)
                .Where(path => Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase)
                            || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 1)
            {
                error = $"运行目录存在多个日志候选，归属有歧义：{string.Join(", ", candidates.Select(Path.GetFileName))}；"
                    + "请用 --log 显式指定本次运行的上游动作日志";
                return false;
            }
            if (candidates.Length == 1)
            {
                logPath = candidates[0];
                source.Add("日志来自运行目录内唯一的日志文件");
            }
        }
        if (logPath is null)
        {
            error = $"运行目录 {runDirectory} 中没有局部动作日志；请用 --log 显式指定本次运行的上游动作日志。"
                + "shadow 仅含钩子观测，不使用其 upstream_log 或引擎共享 log/ 猜测日志归属";
            return false;
        }

        input = new CampaignRunDirectoryInput(runDirectory, chapterModule, logPath, clearAll, source);
        return true;
    }
}
