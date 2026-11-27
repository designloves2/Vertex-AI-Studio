using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ImageStudio.Core;

/// <summary>Settings the embedded proxy (index.js) needs; passed as environment variables, no .env file.</summary>
public sealed record ProxyConfig(string ProjectId, string Location, string DefaultModel, int Port);

/// <summary>
/// Runs the bundled Vertex OpenAI Proxy (node + index.js) as a child process.
/// The child is put in a kill-on-close Job Object, so it never outlives the app (even on a crash).
/// Layout: release = &lt;app&gt;\proxy\{index.js, node_modules, node\node.exe}; dev = &lt;repo&gt;\proxy (+ node on PATH).
/// </summary>
public sealed class ProxyHost : IDisposable
{
    readonly object _lock = new();
    readonly LinkedList<string> _log = new();
    const int MaxLog = 800;
    Process? _proc;
    IntPtr _job;

    public event Action<string, bool>? Output;     // line, isError
    public event Action<bool>? StateChanged;       // running

    public string? ProxyDir { get; } = FindProxyDir();
    public string? NodePath => FindNode(ProxyDir);
    public bool IsRunning => _proc is { HasExited: false };
    public int? Port { get; private set; }

    public IReadOnlyList<string> RecentLog { get { lock (_lock) return _log.ToList(); } }

    static string? FindProxyDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "proxy");
            if (File.Exists(Path.Combine(p, "index.js"))) return p;
            dir = dir.Parent;
        }
        return null;
    }

    static string? FindNode(string? proxyDir)
    {
        if (proxyDir != null)
            foreach (var b in new[] { Path.Combine(proxyDir, "node", "node.exe"), Path.Combine(proxyDir, "node.exe") })
                if (File.Exists(b)) return b;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static bool PortInUse(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(200) && c.Connected;
        }
        catch { return false; }
    }

    void Add(string line, bool err)
    {
        lock (_lock)
        {
            _log.AddLast(line);
            while (_log.Count > MaxLog) _log.RemoveFirst();
        }
        Output?.Invoke(line, err);
    }

    /// <summary>Starts the proxy; returns an error message, or null on success.</summary>
    public string? Start(ProxyConfig cfg)
    {
        if (IsRunning) return null;
        if (ProxyDir == null) return "내장 프록시 파일(proxy\\index.js)을 찾을 수 없습니다.";
        if (!Directory.Exists(Path.Combine(ProxyDir, "node_modules"))) return "내장 프록시의 node_modules가 없습니다 (proxy 폴더에서 npm ci 필요).";
        var node = NodePath;
        if (node == null) return "node.exe를 찾을 수 없습니다.";
        if (PortInUse(cfg.Port)) return $"포트 {cfg.Port}을(를) 이미 다른 프로그램이 쓰고 있습니다. 설정에서 포트를 바꾸세요.";

        var psi = new ProcessStartInfo(node, "index.js")
        {
            WorkingDirectory = ProxyDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // dotenv never overrides existing variables, so these win over any stray .env.
        psi.Environment["GOOGLE_CLOUD_PROJECT_ID"] = cfg.ProjectId;
        psi.Environment["GOOGLE_CLOUD_LOCATION"] = cfg.Location;
        psi.Environment["GOOGLE_CLOUD_MODEL_ID"] = cfg.DefaultModel;
        psi.Environment["PORT"] = cfg.Port.ToString();
        psi.Environment["FORCE_COLOR"] = "0";

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Add(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Add(e.Data, true); };
        p.Exited += (_, _) => { if (_proc == p) { Add($"[프록시 종료됨, 코드 {SafeExit(p)}]", true); StateChanged?.Invoke(false); } };
        try { p.Start(); }
        catch (Exception ex) { return "node 실행 실패: " + ex.Message; }
        AttachToJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        Port = cfg.Port;
        Add($"[내장 프록시 시작: 포트 {cfg.Port}, 프로젝트 {cfg.ProjectId}, 리전 {cfg.Location}]", false);
        StateChanged?.Invoke(true);
        return null;
    }

    static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    public async Task StopAsync()
    {
        var p = _proc;
        _proc = null;
        if (p is { HasExited: false })
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        if (Port is int port)
            for (var i = 0; i < 40 && PortInUse(port); i++) await Task.Delay(150);
        StateChanged?.Invoke(false);
    }

    /// <summary>Polls /health until the proxy answers (or the process dies / time runs out).</summary>
    public async Task<bool> WaitHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (Port is not int port) return false;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && IsRunning)
        {
            try
            {
                using var r = await http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (r.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(300, ct);
        }
        return false;
    }

    public void Dispose()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    // ---------- Job Object: kill the child when the app exits ----------

    void AttachToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ptr, (uint)len);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch { /* best effort: StopAsync / Dispose still kill it on a normal exit */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
