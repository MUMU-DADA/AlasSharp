using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 任务的最终结论。**只有这几个值**，前端/调度/报告都按它分派，
/// 各业务域不许再发明自己的成功词（那是 R2 的通用状态模型要求）。
/// </summary>
public enum TaskOutcome
{
    /// <summary>任务达成（战役域 = 每一关都过合同且结算是 cleared）。</summary>
    Succeeded,
    /// <summary>执行了但没达成（撤退/战败/报错/没打完）。</summary>
    Failed,
    /// <summary>没执行：前置条件不满足、被取消、或被前序失败跳过。</summary>
    Skipped,
    /// <summary>被安全联锁拒绝（例如真跑没开 allow_actions）。</summary>
    Refused,
    /// <summary>dry-run：只读规则，不碰游戏。</summary>
    DryRun,
}

/// <summary>
/// 一个任务请求。`Input` 是**该业务域自己的输入模型**（JSON），
/// 通用层不解释它 —— 由对应的 <see cref="ITaskRunner"/> 校验并消费。
/// </summary>
public sealed class TaskRequest
{
    /// <summary>队列内唯一 id；工件命名与断点续跑都按它来。</summary>
    public string Id { get; set; } = "";
    /// <summary>域标识，例如 `campaign_batch`。</summary>
    public string Kind { get; set; } = "";
    public JsonObject? Input { get; set; }
    /// <summary>前置条件不满足时算不算失败：true = 停下整条队列；false（默认）= 记 skipped 继续。</summary>
    public bool Required { get; set; }

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>
/// 任务结果。**证据在 <see cref="Evidence"/> 里**（上游调用、结果合同裁决、工件路径…），
/// 报告与前端只读这里，不去解析业务域的内部对象。
/// </summary>
public sealed class TaskResult
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public TaskOutcome Outcome { get; set; } = TaskOutcome.Failed;
    public RuntimeErrorKind ErrorKind { get; set; } = RuntimeErrorKind.None;
    public string? Error { get; set; }
    public string? StopReason { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? ArtifactPath { get; set; }
    public JsonObject? Evidence { get; set; }
    /// <summary>
    /// 任务开始前的状态快照（页面集合、是否在图内及帧来源）。设备会话取上游最后缓存帧，
    /// 离线会话取宿主帧，不新增设备动作；这是最后观测状态，不承诺实时。
    /// 无缓存如实记 `available=false`，不回退到另一来源的旧画面。
    /// </summary>
    public JsonObject? BoundaryState { get; set; }
    /// <summary>前置条件不满足时的原因（与"跑失败"区分开）。</summary>
    public List<string> UnmetPreconditions { get; } = new();

    public bool Succeeded => Outcome == TaskOutcome.Succeeded;
    public bool Failed => Outcome == TaskOutcome.Failed || Outcome == TaskOutcome.Refused;

    /// <summary>稳定字符串（诊断脚本按它断言，不要随意改）。</summary>
    public string OutcomeName => Outcome switch
    {
        TaskOutcome.Succeeded => "succeeded",
        TaskOutcome.Failed => "failed",
        TaskOutcome.Skipped => "skipped",
        TaskOutcome.Refused => "refused",
        TaskOutcome.DryRun => "dry_run",
        _ => "failed",
    };
}

/// <summary>任务执行上下文：会话（宿主/设备/日志/工件目录）+ 会话选项。</summary>
public sealed class TaskContext
{
    public AlasSession Session { get; }
    public SessionOptions Options => Session.Options;
    public SessionLog Log => Session.Log;
    public string? RunDirectory => Session.RunDirectory;

    public TaskContext(AlasSession session) => Session = session;
}

/// <summary>
/// 一个业务域的运行器。**新业务域先实现这个接口**（在 Alas.Core 里），
/// 再考虑要不要暴露到 CLI 或前端 —— 不许在命令分支里复制一套状态机。
/// </summary>
public interface ITaskRunner
{
    /// <summary>域标识，必须与 <see cref="TaskRequest.Kind"/> 一致。</summary>
    string Kind { get; }

    /// <summary>
    /// 前置条件：返回**不满足的原因**（空 = 可以跑）。
    /// 只做"能不能开始"的判断，不碰游戏状态 —— 需要设备/页面的检查放到任务里，
    /// 因为那些必须走视觉宿主、必须留证据。
    /// </summary>
    IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context);

    /// <summary>执行并把结论写进 <see cref="TaskResult"/>；异常请自行分类后抛出或转成结果。</summary>
    TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token);
}
