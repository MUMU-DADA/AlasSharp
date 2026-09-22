namespace Alas.Runtime;

/// <summary>
/// 一次会话/一批任务的全部输入。**CLI 只负责把这些字段从参数解析出来**，
/// 业务判定（能不能跑、跑几关、什么算成功）一律不在这里 —— 那是
/// <see cref="CampaignBatchRunner"/> 与结果合同的事（R1 阶段门槛：
/// "CLI 参数不会再复制一套业务状态机"）。
/// </summary>
public sealed class SessionOptions
{
    /// <summary>ALAS 仓库目录（宿主按它推导解释器与上游模块）。</summary>
    public string RepoDirectory { get; set; } = "";
    /// <summary>本项目的 tools 目录（宿主会加进 sys.path）。</summary>
    public string ToolsDirectory { get; set; } = "";
    /// <summary>
    /// 上游数据契约目录（`data/`）。离线任务（如活动章节清点）读它，
    /// 不需要设备也不需要启动宿主 —— 但仍然**只读**，不做任何生成动作。
    /// </summary>
    public string DataDirectory { get; set; } = "";
    public string? AdbPath { get; set; }
    public string? Serial { get; set; }
    public string ScreenshotBackend { get; set; } = "scrcpy";
    public string ControlBackend { get; set; } = "MaaTouch";

    /// <summary>dry-run：只读规则，不初始化设备、不碰游戏。</summary>
    public bool DryRun { get; set; } = true;
    /// <summary>真跑的第二把锁；`DryRun=false` 且它为假时**在启动宿主之前**就拒绝。</summary>
    public bool AllowActions { get; set; }

    public double MaxSeconds { get; set; } = 1500;
    public int MaxRounds { get; set; } = 20;
    public bool RepeatUntilCleared { get; set; } = true;
    public bool ClearAll { get; set; }
    public int Fleet1 { get; set; } = 1;
    public int Fleet2 { get; set; }
    public int SubmarineFleet { get; set; }

    /// <summary>证据落盘根目录；为空表示不落盘（失败帧仍会落到宿主默认目录）。</summary>
    public string? ArtifactsDirectory { get; set; }

    /// <summary>跑之前把参数校验成"可执行"或抛出 —— 不允许带着半截配置启动宿主。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RepoDirectory))
            throw new ArgumentException("缺少 ALAS 仓库目录（RepoDirectory）");
        if (string.IsNullOrWhiteSpace(ToolsDirectory))
            throw new ArgumentException("缺少 tools 目录（ToolsDirectory）");
        if (MaxSeconds <= 0) throw new ArgumentException("时间上限必须为正数");
        if (MaxRounds <= 0) throw new ArgumentException("轮次上限必须为正数");
        if (Fleet1 <= 0) throw new ArgumentException("第一舰队必须大于 0");
        if (Fleet2 < 0) throw new ArgumentException("第二舰队不能为负");
        if (SubmarineFleet < 0) throw new ArgumentException("潜艇舰队不能为负");
        if (!DryRun && !AllowActions)
            throw new ArgumentException("真跑需要 AllowActions（宿主侧还有一道硬性安全联锁）");
    }

    /// <summary>要不要把设备后端配置进宿主：只有真跑才需要，dry-run 一律不碰设备。</summary>
    public bool ShouldConfigureDevice => !DryRun && AllowActions && !string.IsNullOrWhiteSpace(Serial);
}
