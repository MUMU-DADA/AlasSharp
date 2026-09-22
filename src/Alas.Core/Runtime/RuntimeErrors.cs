using Alas.Vision;

namespace Alas.Runtime;

/// <summary>
/// 统一错误分类（R1 交付物之一）。分类决定**谁来处理**：
///   设备/宿主类 → 重连或换后端；上游类 → 看调用栈修兼容垫片；
///   合同类 → 结果本身有问题，绝不能当成成功；取消/超时 → 释放资源、保留证据后正常收尾。
/// </summary>
public enum RuntimeErrorKind
{
    None = 0,
    /// <summary>设备不在线/连接失败/截图失败。</summary>
    DeviceUnavailable,
    /// <summary>识图宿主不可用（解释器起不来、协议栈坏了）。</summary>
    HostUnavailable,
    /// <summary>章节被拒绝执行（安全联锁未开、章节规则缺失）。</summary>
    ChapterRefused,
    /// <summary>上游执行报错（含地图初始化失败）。</summary>
    UpstreamError,
    /// <summary>结果通过了运行但**没过结果合同**：数值/字段自相矛盾。</summary>
    ContractViolation,
    /// <summary>被调用方取消（在关卡边界生效）。</summary>
    Cancelled,
    /// <summary>时间/轮次上限到顶。</summary>
    Timeout,
    /// <summary>宿主本身的问题（反序列化失败、协议响应不合法等）。</summary>
    Internal,
}

/// <summary>带分类的运行时异常。所有对外抛出的错误都应该是这个类型。</summary>
public sealed class AlasRuntimeException : Exception
{
    public RuntimeErrorKind Kind { get; }

    public AlasRuntimeException(RuntimeErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;
}

public static class RuntimeErrors
{
    /// <summary>
    /// 把任意异常归到一类。**只有明确认识的情况才细分**，其余一律 Internal ——
    /// 猜错分类比不分类更糟（会让调用方走错恢复路径）。
    /// </summary>
    public static RuntimeErrorKind Classify(Exception error) => error switch
    {
        AlasRuntimeException runtime => runtime.Kind,
        OperationCanceledException => RuntimeErrorKind.Cancelled,
        TimeoutException => RuntimeErrorKind.Timeout,
        VisionWorkerException vision when vision.Operation == "s3_run_plan" =>
            RuntimeErrorKind.UpstreamError,
        VisionWorkerException => RuntimeErrorKind.HostUnavailable,
        IOException => RuntimeErrorKind.DeviceUnavailable,
        _ => RuntimeErrorKind.Internal,
    };

    public static AlasRuntimeException Wrap(Exception error, string context)
        => error as AlasRuntimeException
           ?? new AlasRuntimeException(Classify(error), $"{context}: {error.Message}", error);

    /// <summary>给日志/报告用的短名（稳定字符串，不要改，诊断脚本按它断言）。</summary>
    public static string Name(RuntimeErrorKind kind) => kind switch
    {
        RuntimeErrorKind.None => "none",
        RuntimeErrorKind.DeviceUnavailable => "device_unavailable",
        RuntimeErrorKind.HostUnavailable => "host_unavailable",
        RuntimeErrorKind.ChapterRefused => "chapter_refused",
        RuntimeErrorKind.UpstreamError => "upstream_error",
        RuntimeErrorKind.ContractViolation => "contract_violation",
        RuntimeErrorKind.Cancelled => "cancelled",
        RuntimeErrorKind.Timeout => "timeout",
        _ => "internal",
    };
}
