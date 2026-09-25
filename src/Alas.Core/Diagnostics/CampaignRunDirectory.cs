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
///   <item>上游日志：① 运行目录里直接有的 `*.log`/`*.txt`；② `shadow-*.json` 的 `upstream_log` →
///         去引擎仓库 `log/` 下找；③ 引擎仓库 `log/` 下最新的一份。</item>
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
            logPath = Directory.GetFiles(runDirectory, "*.log").Concat(Directory.GetFiles(runDirectory, "*.txt"))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (logPath is not null) source.Add("日志来自运行目录内的日志文件");
        }
        string logDirectory = Path.Combine(repoDirectory, "log");
        if (logPath is null)
        {
            string? shadow = Directory.GetFiles(runDirectory, "shadow-*.json").FirstOrDefault();
            if (shadow is not null)
            {
                try
                {
                    string? name = JsonNode.Parse(File.ReadAllText(shadow))?["upstream_log"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name) && File.Exists(Path.Combine(logDirectory, name)))
                    {
                        logPath = Path.Combine(logDirectory, name);
                        source.Add("日志来自 shadow 工件记录的 upstream_log");
                    }
                }
                catch (Exception failure) when (failure is JsonException or IOException or InvalidOperationException)
                {
                    // 落到"最新日志"分支
                }
            }
        }
        if (logPath is null && Directory.Exists(logDirectory))
        {
            // 用**运行目录自身的时间窗**挑日志：取运行目录里最早的工件写入时间为起点（-60 秒容差），
            // 只认这个时刻之后写的日志——避免把别的会话/别的检查的日志当成"本次运行"。
            DateTime floor = Directory.GetFiles(runDirectory)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.UtcNow)
                .Min()
                .AddSeconds(-60);
            string?[] candidates = Directory.GetFiles(logDirectory, "*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            logPath = candidates.FirstOrDefault(path => path is not null
                                                        && File.GetLastWriteTimeUtc(path) >= floor);
            if (logPath is not null)
            {
                source.Add("日志取引擎 log/ 目录里**运行时间窗内**的一份");
            }
            else
            {
                logPath = candidates.FirstOrDefault();
                if (logPath is not null)
                {
                    source.Add("日志取引擎 log/ 目录里最新的一份（**时间窗内没有匹配，可能不是本次运行**）");
                }
            }
        }
        if (logPath is null)
        {
            error = $"运行目录与引擎 log/ 都找不到上游日志（运行目录 {runDirectory}）";
            return false;
        }

        input = new CampaignRunDirectoryInput(runDirectory, chapterModule, logPath, clearAll, source);
        return true;
    }
}
