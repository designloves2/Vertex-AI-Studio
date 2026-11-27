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
    /// <summary>
    /// Frees the port before starting: a proxy left over from an earlier run (any node.exe listening there,
    /// e.g. after a crash or a forced update) is killed. Any other program is left alone and reported.
    /// </summary>
    public async Task<string?> StartAsync(ProxyConfig cfg)
    {
        if (!IsRunning && PortInUse(cfg.Port) && ListeningProcess(cfg.Port) is Process other)
        {
            using (other)
            {
                var name = SafeName(other);
                if (!name.Equals("node", StringComparison.OrdinalIgnoreCase))
                    return L.F("포트 {0}을(를) 다른 프로그램({1}, PID {2})이 쓰고 있습니다. 그 프로그램을 끄거나 설정에서 포트를 바꾸세요.", cfg.Port, name, other.Id);
                Add(L.F("[포트 {0}에 남아 있던 프록시(node, PID {1})를 종료합니다]", cfg.Port, other.Id), false);
                try { other.Kill(entireProcessTree: true); await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            for (var i = 0; i < 40 && PortInUse(cfg.Port); i++) await Task.Delay(150);
        }
        return Start(cfg);
    }

    static string SafeName(Process p) { try { return p.ProcessName; } catch { return "?"; } }

    /// <summary>Process listening on 127.0.0.1/0.0.0.0/[::]:port (from `netstat -ano`), or null.</summary>
    public static Process? ListeningProcess(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var ns = Process.Start(psi)!;
            var text = ns.StandardOutput.ReadToEnd();
            ns.WaitForExit(5000);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Proto  Local  Foreign  State  PID
                if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts[1].EndsWith(":" + port) || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(parts[4], out var pid) && pid > 0) return Process.GetProcessById(pid);
            }
        }
        catch { }
        return null;
    }

    public string? Start(ProxyConfig cfg)
    {
        if (IsRunning) return null;
        if (ProxyDir == null) return L.T("내장 프록시 파일(proxy\\index.js)을 찾을 수 없습니다.");
        if (!Directory.Exists(Path.Combine(ProxyDir, "node_modules"))) return L.T("내장 프록시의 node_modules가 없습니다 (proxy 폴더에서 npm ci 필요).");
        var node = NodePath;
        if (node == null) return L.T("node.exe를 찾을 수 없습니다.");
        if (PortInUse(cfg.Port)) return L.F("포트 {0}을(를) 이미 다른 프로그램이 쓰고 있습니다. 설정에서 포트를 바꾸세요.", cfg.Port);

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
        p.Exited += (_, _) => { if (_proc == p) { Add(L.F("[프록시 종료됨, 코드 {0}]", SafeExit(p)), true); StateChanged?.Invoke(false); } };
        try { p.Start(); }
        catch (Exception ex) { return L.T("node 실행 실패: ") + ex.Message; }
        AttachToJob(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        Port = cfg.Port;
        Add(L.F("[내장 프록시 시작: 포트 {0}, 프로젝트 {1}, 리전 {2}]", cfg.Port, cfg.ProjectId, cfg.Location), false);
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
