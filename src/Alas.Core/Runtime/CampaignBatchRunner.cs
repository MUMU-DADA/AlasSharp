using System.Text.Json.Nodes;
using Alas.Campaign;
using Alas.Vision;

namespace Alas.Runtime;

/// <summary>一关的执行记录：上游结果 + 合同裁决 + 工件位置。</summary>
public sealed class StageRun
{
    public string Chapter { get; init; } = "";
    public string? Stage { get; set; }
    public CampaignPlanResult? Result { get; set; }
    /// <summary>合同违例码（空 = 结论站得住）。</summary>
    public List<string> ContractViolations { get; } = new();
    public RuntimeErrorKind ErrorKind { get; set; } = RuntimeErrorKind.None;
    public string? Error { get; set; }
    public string? ArtifactPath { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool Skipped { get; set; }

    /// <summary>本关的结论。被跳过的关卡报 `skipped`，不借用别的关卡的结论。</summary>
    public string Outcome => Skipped
        ? "skipped"
        : Result?.Outcome ?? (Result?.DryRun == true ? "dry_run" : "error");
    /// <summary>这一关算不算通关：**结论为 cleared 且过合同**，两者缺一不可。</summary>
    public bool Cleared => ContractViolations.Count == 0 && Result?.Cleared == true;
    public bool Failed => Skipped || Error is not null || ContractViolations.Count > 0
                          || (Result?.DryRun != true && !Cleared);
}

/// <summary>
/// 一批关卡的结果。批次的结论是"逐关结论的最坏者"，不是任何一个单字段：
/// error &gt; refused &gt; 未通关（撤退/战败/说不清/没打完）&gt; cleared。
/// </summary>
public sealed class CampaignBatchResult
{
    public List<StageRun> Stages { get; } = new();
    public string Outcome { get; set; } = "incomplete";
    public RuntimeErrorKind ErrorKind { get; set; } = RuntimeErrorKind.None;
    public bool StoppedEarly { get; set; }
    public string? StopReason { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? IndexPath { get; set; }
    public bool DryRun { get; set; }

    public bool Cleared => Outcome == "cleared";
    public int FailedCount => Stages.Count(s => s.Failed);
}

/// <summary>
/// 一次批量出击的运行参数。**会话级选项与任务级输入分开**：
/// 会话级是"这台机器怎么连"（RepoDirectory/Serial/…），
/// 任务级是"这一批怎么打"（上限、舰队、清图模式）——
/// 队列里不同任务可以有各自的这一份，不必共用一个全局配置。
/// </summary>
public sealed class CampaignRunSettings
{
    public double MaxSeconds { get; set; } = 1500;
    public int MaxRounds { get; set; } = 20;
    public bool RepeatUntilCleared { get; set; } = true;
    public bool ClearAll { get; set; }
    public int Fleet1 { get; set; } = 1;
    public int Fleet2 { get; set; }
    public int SubmarineFleet { get; set; }

    /// <summary>默认取会话选项（CLI 传进来的那一份）。</summary>
    public static CampaignRunSettings From(SessionOptions options) => new()
    {
        MaxSeconds = options.MaxSeconds,
        MaxRounds = options.MaxRounds,
        RepeatUntilCleared = options.RepeatUntilCleared,
        ClearAll = options.ClearAll,
        Fleet1 = options.Fleet1,
        Fleet2 = options.Fleet2,
        SubmarineFleet = options.SubmarineFleet,
    };
}

/// <summary>
/// 战役批量任务（R1：业务编排从 CLI 搬进 Alas.Core）。
///
/// 它只做四件事，且都是**通用**的（不认地图名、不写地图分支）：
///   1. 逐关调用上游驱动的 `s3_run_plan`，复用同一个常驻会话；
///   2. 每关过一遍结果合同（`SortieContract.Violations`），违例即失败；
///   3. 落盘证据（每关一份结果文档 + 批次 index.json + 会话日志），失败关也不例外；
///   4. 失败即停（可关），取消在关卡边界生效 —— 正在执行的上游出击不强行打断，
///      与 `max_seconds` 的既有语义一致（时间上限在上游操作边界检查）。
/// </summary>
public sealed class CampaignBatchRunner
{
    private readonly AlasSession _session;
    private readonly HashSet<string> _artifactNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一关失败后是否停掉整批。默认停 —— 状态未知时继续下一关会白耗账号资源。</summary>
    public bool StopOnFailure { get; set; } = true;

    public CampaignBatchRunner(AlasSession session) => _session = session;

    public CampaignBatchResult Run(IReadOnlyList<string> chapters, CancellationToken token = default,
                                   CampaignRunSettings? settings = null)
    {
        if (chapters.Count == 0) throw new ArgumentException("至少要有一关");
        var options = _session.Options;
        var run = settings ?? CampaignRunSettings.From(options);
        var batch = new CampaignBatchResult { DryRun = options.DryRun };
        string indexName = NextIndexName();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _session.Log.Info("batch", "开始批量任务", new Dictionary<string, object?>
        {
            ["chapters"] = chapters.Count,
            ["dry_run"] = options.DryRun,
            ["max_seconds"] = run.MaxSeconds,
            ["max_rounds"] = run.MaxRounds,
            ["artifacts"] = _session.RunDirectory,
        });

        foreach (var chapter in chapters)
        {
            var stage = new StageRun { Chapter = chapter };
            batch.Stages.Add(stage);
            // 取消在**关卡边界**生效：不打断正在跑的上游出击（它没有可中断点）。
            if (token.IsCancellationRequested)
            {
                stage.Skipped = true;
                stage.Error = "调用方取消：未开始这一关";
                stage.ErrorKind = RuntimeErrorKind.Cancelled;
                batch.Outcome = "cancelled";
                batch.ErrorKind = RuntimeErrorKind.Cancelled;
                batch.StoppedEarly = true;
                batch.StopReason = "cancelled";
                // 后面的关卡如实记为"被取消"，不要假装它们成功了。
                foreach (var rest in chapters.Skip(batch.Stages.Count))
                    batch.Stages.Add(new StageRun
                    {
                        Chapter = rest,
                        Skipped = true,
                        Error = "调用方取消：未开始这一关",
                        ErrorKind = RuntimeErrorKind.Cancelled,
                    });
                _session.Log.Warn("batch", "已在关卡边界取消", new Dictionary<string, object?>
                {
                    ["chapter"] = chapter,
                });
                break;
            }

            var stageWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                stage.Result = _session.Vision.RunCampaignPlan(
                    chapter,
                    dryRun: options.DryRun,
                    allowActions: options.AllowActions,
                    maxSeconds: run.MaxSeconds,
                    maxRounds: run.MaxRounds,
                    repeatUntilCleared: run.RepeatUntilCleared,
                    fleet1: run.Fleet1,
                    fleet2: run.Fleet2,
                    submarineFleet: run.SubmarineFleet,
                    clearAll: run.ClearAll,
                    serial: options.Serial,
                    artifactsDir: _session.RunDirectory,
                    // 运行中请求撤退：**约定路径** `<运行目录>\withdraw.request` —— 文件出现即请求，
                    // 宿主会在下一次战斗之前调用上游自己的 withdraw()，本局判 outcome=withdrawn。
                    // 与 `--stop-file` 同一套习惯（文件出现即生效），但语义不同：
                    // stop-file 是"停下队列"，这个是"让本局按玩家撤退结束"。
                    withdrawFile: _session.RunDirectory is null
                        ? null
                        : Path.Combine(_session.RunDirectory, "withdraw.request"));
            }
            catch (Exception error)
            {
                var wrapped = RuntimeErrors.Wrap(error, $"执行 {chapter} 失败");
                stage.ErrorKind = wrapped.Kind;
                stage.Error = wrapped.Message;
            }
            stageWatch.Stop();
            stage.ElapsedSeconds = Math.Round(stageWatch.Elapsed.TotalSeconds, 1);
            stage.Stage = stage.Result?.Stage ?? stage.Stage;

            Judge(stage);
            stage.ArtifactPath = WriteStageArtifact(stage);
            LogStage(stage);

            if (stage.Failed && StopOnFailure)
            {
                batch.StoppedEarly = true;
                batch.StopReason = stage.ErrorKind == RuntimeErrorKind.None
                    ? $"关卡未通过: {stage.Outcome}"
                    : $"关卡错误: {RuntimeErrors.Name(stage.ErrorKind)}";
                // 后面的关卡没跑，如实记录"被跳过"，不要假装它们成功了。
                foreach (var rest in chapters.Skip(batch.Stages.Count))
                    batch.Stages.Add(new StageRun
                    {
                        Chapter = rest,
                        Skipped = true,
                        Error = $"前序关卡失败（{stage.Chapter}），按失败即停跳过",
                        ErrorKind = stage.ErrorKind,
                    });
                break;
            }
        }

        watch.Stop();
        batch.ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        // 被跳过的关卡也要留档：批处理提前停止时，"哪些关了、哪些没跑"本身就是证据，
        // 只在 index.json 里列一行不够（单关工件缺失会让人误以为漏写）。
        if (_session.RunDirectory is not null)
            foreach (var stage in batch.Stages.Where(s => s.ArtifactPath is null))
            {
                stage.ArtifactPath = WriteStageArtifact(stage);
                LogStage(stage);
            }
        Aggregate(batch);
        batch.IndexPath = WriteIndex(batch, indexName);
        _session.Log.Info("batch", "批量任务结束", new Dictionary<string, object?>
        {
            ["outcome"] = batch.Outcome,
            ["cleared"] = batch.Cleared,
            ["stages"] = batch.Stages.Count,
            ["failed"] = batch.FailedCount,
            ["elapsed_s"] = batch.ElapsedSeconds,
        });
        return batch;
    }

    /// <summary>合同裁决：**结论只认合同**，这里不额外发明通关条件。</summary>
    private void Judge(StageRun stage)
    {
        if (stage.Result is null) return;
        stage.ContractViolations.AddRange(SortieContract.Violations(stage.Result, _session.RunDirectory));
        if (stage.ContractViolations.Count > 0)
        {
            stage.ErrorKind = RuntimeErrorKind.ContractViolation;
            stage.Error ??= "结果未通过合同: " + string.Join(", ", stage.ContractViolations);
            return;
        }
        var result = stage.Result;
        if (result.Refused == true || result.Outcome == "refused")
        {
            stage.ErrorKind = RuntimeErrorKind.ChapterRefused;
            stage.Error ??= result.Reason ?? "章节被拒绝执行";
        }
        else if (result.Error is not null || result.Outcome == "error")
        {
            stage.ErrorKind = RuntimeErrorKind.UpstreamError;
            stage.Error ??= result.Error ?? "上游报错";
        }
    }

    private static void Aggregate(CampaignBatchResult batch)
    {
        if (batch.DryRun)
        {
            // dry-run 没有"通关"这个概念：只有"规则读没读到"。
            batch.Outcome = batch.Stages.Any(s => s.Result is null) ? "error" : "dry_run";
            batch.ErrorKind = batch.Outcome == "error"
                ? batch.Stages.First(s => s.Result is null).ErrorKind : RuntimeErrorKind.None;
            return;
        }
        var failed = batch.Stages.FirstOrDefault(s => s.Failed);
        if (failed is not null)
        {
            batch.ErrorKind = failed.ErrorKind;
            batch.Outcome = failed.ErrorKind switch
            {
                RuntimeErrorKind.ContractViolation => "error",
                RuntimeErrorKind.ChapterRefused => "refused",
                RuntimeErrorKind.UpstreamError => "error",
                RuntimeErrorKind.Cancelled => "cancelled",
                RuntimeErrorKind.Timeout => "incomplete",
                RuntimeErrorKind.DeviceUnavailable or RuntimeErrorKind.HostUnavailable => "error",
                RuntimeErrorKind.Internal => "error",
                // 没报错、没过合同、也不是通关：照实报这一关自己的结论（撤退/战败/说不清/没打完）。
                _ => failed.Outcome,
            };
            return;
        }
        batch.Outcome = "cleared";
    }

    private void LogStage(StageRun stage)
    {
        var fields = new Dictionary<string, object?>
        {
            ["chapter"] = stage.Chapter,
            ["stage"] = stage.Stage,
            ["outcome"] = stage.Outcome,
            ["cleared"] = stage.Cleared,
            ["elapsed_s"] = stage.ElapsedSeconds,
            ["violations"] = stage.ContractViolations.Count,
        };
        if (stage.Error is not null) fields["error"] = stage.Error;
        if (stage.Skipped) fields["skipped"] = true;
        _session.Log.Add(stage.Failed ? "ERROR" : "INFO", "stage",
                         SortieContract.Describe(stage.Result ?? new SortieResult(),
                                                 _session.RunDirectory), fields);
    }

    private string? WriteStageArtifact(StageRun stage)
    {
        if (_session.RunDirectory is null) return null;
        var document = new JsonObject
        {
            ["chapter"] = stage.Chapter,
            ["stage"] = stage.Stage,
            ["cleared"] = stage.Cleared,
            ["failed"] = stage.Failed,
            ["skipped"] = stage.Skipped,
            ["elapsed_s"] = stage.ElapsedSeconds,
            ["error_kind"] = RuntimeErrors.Name(stage.ErrorKind),
            ["error"] = stage.Error,
            ["contract_violations"] = new JsonArray(
                stage.ContractViolations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            ["result"] = stage.Result is null
                ? null
                : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(stage.Result)),
        };
        string label = StageFileLabel(stage.Chapter, stage.Stage);
        string name = $"sortie-{label}.json";
        // 两关的 `stage` 标签撞名时（活动章节确实可能都叫 a1）绝不能互相覆盖 ——
        // 覆盖等于把先跑那关的证据丢了，而且事后看不出来。
        if (_artifactNames.Contains(name) || File.Exists(Path.Combine(_session.RunDirectory, name)))
        {
            name = $"sortie-{label}-{stage.Chapter.Split('.').Last()}.json";
        }
        string candidate = name;
        for (int suffix = 2; _artifactNames.Contains(name)
             || File.Exists(Path.Combine(_session.RunDirectory, name)); suffix++)
            name = $"{Path.GetFileNameWithoutExtension(candidate)}-{suffix}.json";
        _artifactNames.Add(name);
        return _session.WriteArtifact(name, document);
    }

    /// <summary>
    /// 工件文件名里的关卡标签。没跑起来的关卡（被跳过/直接报错）拿不到上游给的 `stage`，
    /// 就从章节模块名推 —— 但这里**只做命名**，不参与任何判定（不是按地图分支）。
    /// </summary>
    private static string StageFileLabel(string chapter, string? stage)
    {
        if (!string.IsNullOrWhiteSpace(stage)) return stage.Replace('/', '_');
        var match = System.Text.RegularExpressions.Regex.Match(chapter, @"campaign_(\d+)_(\d+)$");
        if (match.Success) return $"{match.Groups[1].Value}-{match.Groups[2].Value}";
        return chapter.Split('.').Last().Replace('/', '_');
    }

    private string NextIndexName()
    {
        if (_session.RunDirectory is null) return "index.json";
        for (int number = 1; ; number++)
        {
            string name = number == 1 ? "index.json" : $"index-{number}.json";
            if (!File.Exists(Path.Combine(_session.RunDirectory, name))) return name;
        }
    }

    private string? WriteIndex(CampaignBatchResult batch, string name)
    {
        if (_session.RunDirectory is null) return null;
        var stages = new JsonArray();
        foreach (var stage in batch.Stages)
        {
            stages.Add(new JsonObject
            {
                ["chapter"] = stage.Chapter,
                ["stage"] = stage.Stage,
                ["outcome"] = stage.Outcome,
                ["cleared"] = stage.Cleared,
                ["failed"] = stage.Failed,
                ["skipped"] = stage.Skipped,
                ["error_kind"] = RuntimeErrors.Name(stage.ErrorKind),
                ["error"] = stage.Error,
                ["artifact"] = stage.ArtifactPath,
            });
        }
        var index = new JsonObject
        {
            ["contract"] = SortieContract.Version,
            ["dry_run"] = batch.DryRun,
            ["requested_stages"] = batch.Stages.Count,
            ["outcome"] = batch.Outcome,
            ["cleared"] = batch.Cleared,
            ["error_kind"] = RuntimeErrors.Name(batch.ErrorKind),
            ["stopped_early"] = batch.StoppedEarly,
            ["stop_reason"] = batch.StopReason,
            ["elapsed_s"] = batch.ElapsedSeconds,
            ["host_start_count"] = _session.HostStartCount,
            ["device_configure_count"] = _session.DeviceConfigureCount,
            ["stages"] = stages,
        };
        return _session.WriteArtifact(name, index);
    }
}
