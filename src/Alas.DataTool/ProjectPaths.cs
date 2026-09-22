namespace Alas.DataTool;

/// <summary>
/// 运行时项目布局的唯一解析入口。
///
/// 工具既可能从发布目录运行，也可能由 <c>dotnet run</c> 启动；把目录推导集中在这里，
/// 避免每个命令各自维护一套容易漂移的相对路径。
/// </summary>
internal sealed class ProjectPaths
{
    private ProjectPaths(string root, string data, string repo)
    {
        RootDirectory = root;
        DataDirectory = data;
        RepoDirectory = repo;
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string RepoDirectory { get; }
    public string ToolsDirectory => Path.Combine(RootDirectory, "tools");

    public static ProjectPaths Resolve()
    {
        string root = FindRoot(AppContext.BaseDirectory);
        string data = Environment.GetEnvironmentVariable("ALAS_DATA")
                      ?? Path.Combine(root, "data");
        string repo = Environment.GetEnvironmentVariable("ALAS_REPO")
                      ?? Path.Combine(root, ".runtime", "engine");
        return new ProjectPaths(Path.GetFullPath(root), Path.GetFullPath(data),
                                Path.GetFullPath(repo));
    }

    private static string FindRoot(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        while (current is not null)
        {
            string candidate = current.FullName;
            if (Directory.Exists(Path.Combine(candidate, "tools")) &&
                Directory.Exists(Path.Combine(candidate, "src")))
                return candidate;
            current = current.Parent;
        }

        // 保持旧发布布局下的可诊断行为：调用方稍后会对缺失的数据/仓库给出具体错误。
        return Path.GetFullPath(Path.Combine(start, "..", "..", "..", "..", ".."));
    }
}
