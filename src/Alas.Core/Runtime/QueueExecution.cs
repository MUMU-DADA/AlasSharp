using Alas.Tasks;

namespace Alas.Runtime;

/// <summary>一次队列执行及其断点、停止请求的运行时记录。</summary>
public sealed class QueueExecutionResult
{
    public required IReadOnlyList<TaskRequest> Requests { get; init; }
    public required QueueResult Queue { get; init; }
    public string? ResumeStatePath { get; init; }
    public IReadOnlyCollection<string> ResumeCompleted { get; init; } = Array.Empty<string>();
    public bool StopFileTriggered { get; init; }
}

/// <summary>队列文件的单会话入口。CLI 只传公共选项和取消信号。</summary>
public static class QueueExecution
{
    public static QueueExecutionResult RunFile(string queueFile, SessionOptions options,
                                               bool stopOnFailure = true, bool resume = false,
                                               string? resumeState = null, string? stopFile = null,
                                               CancellationToken token = default,
                                               Action<string?>? onSessionStarted = null,
                                               SessionLog? log = null,
                                               AlasSession? sharedSession = null)
    {
        var requests = TaskQueueFile.Parse(File.ReadAllText(queueFile));
        options.ResolveArtifactsDirectory();
        sharedSession?.NormalizeRunOptions(options);
        if (resumeState is not null) resumeState = Path.GetFullPath(resumeState);
        string? stopPath = stopFile is null ? null : Path.GetFullPath(stopFile);
        if (resumeState is not null && !resume)
            throw new ArgumentException("--resume-state 需要同时指定 --resume");
        if (resumeState is not null && !File.Exists(resumeState))
            throw new ArgumentException($"找不到断点文件: {resumeState}");
        string? statePath = resume
            ? resumeState ?? TaskQueueFile.LatestState(options.ArtifactsDirectory, null)
            : null;
        IReadOnlyCollection<string> completed = resume
            ? TaskQueueFile.ReadCompletedState(statePath, requests, options)
            : Array.Empty<string>();
        AlasSession? ownedSession = null;
        try
        {
            AlasSession session;
            if (sharedSession is null)
            {
                ownedSession = AlasSession.Start(options, log: log);
                session = ownedSession;
            }
            else
            {
                sharedSession.PrepareRun(options, log);
                session = sharedSession;
            }

            // 由运行时告知调用方当前工件目录，不能通过扫描新增目录猜测归属。
            onSessionStarted?.Invoke(session.RunDirectory);
            var queue = new TaskQueue(session) { StopOnFailure = stopOnFailure }
                .Register(new CampaignBatchTask())
                .Register(new AccountStateTask())
                .Register(new OsStateTask())
                .Register(new OsActionTask())
                .Register(new EventStateTask())
                .Register(new TaskCatalogTask())
                .Register(new NavigateTask())
                .Register(new TaskScheduleTask())
                .Register(new PeriodicPlanTask())
                .Register(new PeriodicPreflightTask())
                .Register(new ConfigGetTask())
                .Register(new PeriodicRunTask())
                .Register(new ToolRunTask())
                .Register(new ObserveTask());

            foreach (var id in completed) queue.ResumeCompleted.Add(id);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            int stopFileTriggered = 0;
            void CheckStopFile()
            {
                if (stopPath is null || stop.IsCancellationRequested || !File.Exists(stopPath)) return;
                Interlocked.Exchange(ref stopFileTriggered, 1);
                stop.Cancel();
            }
            CheckStopFile();
            using var watcher = stopPath is null ? null
                : new Timer(_ => CheckStopFile(), null, 200, 200);
            var result = queue.Run(requests, stop.Token);
            return new QueueExecutionResult
            {
                Requests = requests,
                Queue = result,
                ResumeStatePath = statePath,
                ResumeCompleted = completed,
                StopFileTriggered = Volatile.Read(ref stopFileTriggered) != 0,
            };
        }
        finally
        {
            if (sharedSession is not null)
                sharedSession.CompleteRun();
            // The control server owns its shared session and disposes it at shutdown.
            ownedSession?.Dispose();
        }
    }
}
