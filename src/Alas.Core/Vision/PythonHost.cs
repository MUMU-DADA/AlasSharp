using System.Runtime.InteropServices;
using System.Text;

namespace Alas.Vision;

/// <summary>进程内 CPython 宿主的启动配置。</summary>
public sealed class PythonHostOptions
{
    /// <summary>基础解释器目录（含 <c>python3xx.dll</c>）。注意不是 venv 的 Scripts 目录。</summary>
    public required string PythonHome { get; init; }

    /// <summary>追加到 <c>sys.path</c> 的目录：venv 的 site-packages、tools 目录等。</summary>
    public required string[] ExtraPaths { get; init; }

    /// <summary>解释器 DLL 路径；留空则自动在 <see cref="PythonHome"/> 里找 <c>python3*.dll</c>。</summary>
    public string? PythonDll { get; init; }

    /// <summary>提供 <c>handle_line(str) -&gt; str</c> 的 Python 模块名。</summary>
    public string BootstrapModule { get; init; } = "alas_vision";

    /// <summary>入口函数名。</summary>
    public string EntryFunction { get; init; } = "handle_line";

    /// <summary>
    /// 从 ALAS 仓库目录推导配置：读 <c>.venv/pyvenv.cfg</c> 的 <c>home</c> 得到基础解释器，
    /// 再把 venv 的 site-packages 与给定 tools 目录加进 sys.path。
    /// </summary>
    public static PythonHostOptions FromAlasFork(string forkDirectory, string toolsDirectory)
    {
        string fork = Path.GetFullPath(forkDirectory);
        string cfg = Path.Combine(fork, ".venv", "pyvenv.cfg");
        if (!File.Exists(cfg))
            throw new FileNotFoundException($"找不到 venv 配置: {cfg}");

        string? home = null;
        foreach (string line in File.ReadAllLines(cfg))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line[..eq].Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
            {
                home = line[(eq + 1)..].Trim();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidDataException($"{cfg} 里没有 home 项");

        string sitePackages = Path.Combine(fork, ".venv", "Lib", "site-packages");
        return new PythonHostOptions
        {
            PythonHome = home,
            ExtraPaths = new[] { sitePackages, Path.GetFullPath(toolsDirectory), fork },
        };
    }
}

/// <summary>
/// 进程内 CPython 宿主（路线甲的核心约定：**识图不重写**）。
///
/// 为什么不用 Python.NET：本机 NuGet 不通、离线缓存里没有它。但**不需要**它也能做到进程内 ——
/// CPython 的 C API 是稳定 ABI，直接用 P/Invoke 调 <c>python3xx.dll</c> 即可，
/// 本项目只需要 8 个函数，而且协议刻意设计成 <c>str -&gt; str</c> 的单一入口，
/// 把跨语言边界的复杂度压到最低。
///
/// 与进程外 worker（<see cref="VisionWorker"/>）调的是**同一份** <c>alas_vision.handle_line()</c>，
/// 因此两种宿主语义完全一致，可以互换。
///
/// GIL 处理：初始化后立刻用 <c>PyEval_SaveThread</c> 释放 GIL，
/// 之后每次调用都用 <c>PyGILState_Ensure/Release</c> 包住，因此**可以从任意线程调用**。
/// </summary>
public sealed class PythonHost : IDisposable
{
    private const string DllImportName = "__python_dll__";

    private static IntPtr _pythonLib;
    private static readonly object InitLock = new();
    private static bool _resolverInstalled;

    private readonly IntPtr _module;
    private readonly IntPtr _entry;
    private readonly string _entryName;
    private IntPtr _threadState;
    private bool _disposed;

    private PythonHost(IntPtr module, IntPtr entry, string entryName)
    {
        _module = module;
        _entry = entry;
        _entryName = entryName;
    }

    public string PythonVersion { get; private set; } = "";

    public static PythonHost Start(PythonHostOptions options)
    {
        lock (InitLock)
        {
            string pythonHome = Path.GetFullPath(options.PythonHome);
            string dllPath = options.PythonDll ?? FindPythonDll(pythonHome);

            // python3xx.dll 依赖同目录的 vcruntime140.dll，先把该目录加入 DLL 搜索路径
            SetDllDirectory(pythonHome);
            _pythonLib = NativeLibrary.Load(dllPath);

            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(PythonHost).Assembly, (name, _, _) =>
                    name == DllImportName ? _pythonLib : IntPtr.Zero);
                _resolverInstalled = true;
            }

            // 用环境变量配置解释器：比 P/Invoke PyConfig 简单得多，且对嵌入场景足够
            Environment.SetEnvironmentVariable("PYTHONHOME", pythonHome);
            Environment.SetEnvironmentVariable("PYTHONPATH",
                string.Join(';', options.ExtraPaths.Where(p => !string.IsNullOrWhiteSpace(p))));
            Environment.SetEnvironmentVariable("PYTHONUTF8", "1");
            Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", "1");
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
            // 上游 module.logger 会写文件日志；这里让它保持默认（会在 ALAS 的 log/ 下生成），
            // 与控制台无关，进程内宿主无需额外处理。

            Py_InitializeEx(0);
            if (PyErr_Occurred() != IntPtr.Zero) { PyErr_Print(); }

            // ⚠️ 不要依赖 PYTHONPATH 环境变量：在已经跑起来的 .NET 进程里改它，
            //    CPython 的 CRT 未必读得到（实测导入 alas_vision 直接 ModuleNotFoundError）。
            //    改成初始化之后直接往 sys.path 里插，确定性更好。
            //    逆序插入以保持传入顺序（每次都是 insert(0)）。
            foreach (string path in options.ExtraPaths.Reverse())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string literal = path.Replace("\\", "\\\\").Replace("'", "\\'");
                if (PyRun_SimpleString($"import sys; sys.path.insert(0, '{literal}')") != 0)
                {
                    PyErr_Print();
                    throw new InvalidOperationException($"把 {path} 加入 sys.path 失败");
                }
            }

            string version = Marshal.PtrToStringUTF8(Py_GetVersion()) ?? "";
            IntPtr module = PyImport_ImportModule(options.BootstrapModule);
            if (module == IntPtr.Zero)
            {
                PyErr_Print();
                throw new InvalidOperationException(
                    $"导入 Python 模块 {options.BootstrapModule} 失败（检查 sys.path）");
            }

            IntPtr entry = PyObject_GetAttrString(module, options.EntryFunction);
            if (entry == IntPtr.Zero)
            {
                PyErr_Print();
                throw new InvalidOperationException(
                    $"{options.BootstrapModule} 里没有 {options.EntryFunction}");
            }

            var host = new PythonHost(module, entry, options.EntryFunction)
            {
                PythonVersion = version.Split(' ').FirstOrDefault() ?? version,
            };
            // 释放 GIL，之后的调用一律用 PyGILState 包住 → 可跨线程
            host._threadState = PyEval_SaveThread();
            return host;
        }
    }

    private static string FindPythonDll(string home)
    {
        var candidates = Directory.GetFiles(home, "python3*.dll")
            .Where(f => !Path.GetFileName(f).Equals("python3.dll", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f)
            .ToList();
        if (candidates.Count == 0)
            throw new FileNotFoundException($"在 {home} 里找不到 python3xx.dll");
        return candidates[0];
    }

    /// <summary>把一行请求 JSON 交给 Python，取回一行响应 JSON。</summary>
    public string Call(string requestJson)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PythonHost));

        IntPtr gil = PyGILState_Ensure();
        try
        {
            IntPtr arg = PyUnicode_FromString(requestJson);
            if (arg == IntPtr.Zero) { PyErr_Print(); throw new InvalidOperationException("构造 Python 字符串失败"); }
            try
            {
                IntPtr result = PyObject_CallOneArg(_entry, arg);
                if (result == IntPtr.Zero)
                {
                    PyErr_Print();
                    throw new InvalidOperationException($"{_entryName} 调用失败");
                }
                try
                {
                    IntPtr utf8 = PyUnicode_AsUTF8(result);
                    if (utf8 == IntPtr.Zero)
                    {
                        PyErr_Print();
                        throw new InvalidOperationException($"{_entryName} 未返回字符串");
                    }
                    return Marshal.PtrToStringUTF8(utf8)
                           ?? throw new InvalidOperationException("返回值不是合法 UTF-8");
                }
                finally { Py_DecRef(result); }
            }
            finally { Py_DecRef(arg); }
        }
        finally { PyGILState_Release(gil); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_threadState != IntPtr.Zero)
            {
                PyEval_RestoreThread(_threadState);   // 回收 GIL 后才能安全终结
                _threadState = IntPtr.Zero;
            }
            Py_DecRef(_entry);
            Py_DecRef(_module);
            Py_FinalizeEx();
        }
        catch (Exception)
        {
            // 终结期异常不致命（进程即将退出）
        }
    }

    // ---------------------------------------------------------------- C API
    private const string Py = DllImportName;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Py_InitializeEx(int initsigs);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Py_GetVersion();

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PyRun_SimpleString(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyImport_ImportModule(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyObject_GetAttrString(IntPtr obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyObject_CallOneArg(IntPtr callable, IntPtr arg);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyUnicode_FromString(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string s);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyUnicode_AsUTF8(IntPtr unicode);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Py_DecRef(IntPtr obj);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyErr_Occurred();

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PyErr_Print();

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyEval_SaveThread();

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PyEval_RestoreThread(IntPtr tstate);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PyGILState_Ensure();

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PyGILState_Release(IntPtr state);

    [DllImport(Py, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Py_FinalizeEx();
}
