using System.Globalization;

namespace Alas.Server;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("Alas.Server [--root <运行根目录>] [--repo <上游>] [--data <数据>] " +
                    "[--tools <宿主脚本>] [--workspace <控制工作区>] [--artifacts <工件>] " +
                    "[--ui-root <预构建 wwwroot>] [--port 8765]");
                Console.WriteLine("仅监听 127.0.0.1；root 默认是程序目录，其余相对路径均相对于 root。" +
                    "未指定 ui-root 时提供本地队列控制页；自动化仍需要配置上游和 Python 依赖。");
                return 0;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (key is not ("--root" or "--repo" or "--data" or "--tools" or "--workspace" or
                    "--artifacts" or "--ui-root" or "--port"))
                    throw new ArgumentException($"未知服务参数: {key}");
                if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{key} 缺少值");
                if (!values.TryAdd(key, args[i])) throw new ArgumentException($"重复服务参数: {key}");
            }
            string root = Path.GetFullPath(values.GetValueOrDefault("--root", AppContext.BaseDirectory));
            string Resolve(string key, string fallback) => Path.GetFullPath(values.GetValueOrDefault(key, fallback), root);
            int port = 8765;
            if (values.TryGetValue("--port", out var text) &&
                (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
                throw new ArgumentException("--port 必须在 1–65535 之间");
            string repo = Resolve("--repo", Environment.GetEnvironmentVariable("ALAS_REPO") ?? ".runtime/engine");
            string data = Resolve("--data", Environment.GetEnvironmentVariable("ALAS_DATA") ?? "data");
            string tools = Resolve("--tools", "tools");
            string workspace = Resolve("--workspace", ".runtime/control");
            string? artifacts = values.ContainsKey("--artifacts") ? Resolve("--artifacts", "") : null;
            string? ui = values.ContainsKey("--ui-root") ? Resolve("--ui-root", "") : null;
            return await new ControlServer(root, repo, data, tools, artifacts, workspace, port, ui).RunAsync();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[服务启动失败] {error.Message}");
            return 2;
        }
    }
}
