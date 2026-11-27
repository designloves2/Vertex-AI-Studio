using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImageStudio.Core;

public sealed record GcpProject(string Id, string Name);

/// <summary>
/// First-run Google Cloud setup, so that signing in is all a new PC needs:
///  1. Google Cloud CLI (installed silently per user if missing)
///  2. Browser sign-in → Application Default Credentials (same file the proxy uses)
///  3. Project pick (auto when the account has one)
///  4. Vertex AI API enabled on the project (Service Usage API)
///  5. Vertex AI permission checked, and roles/aiplatform.user granted when the account may do so
///  6. ADC quota project set
/// Ported from the vertex-openai-proxy Windows app (GoogleAuth.cs) and extended with steps 4–5.
/// </summary>
public static class GoogleSetup
{
    const string InstallerUrl = "https://dl.google.com/dl/cloudsdk/channels/rapid/GoogleCloudSDKInstaller.exe";
    public const string VertexService = "aiplatform.googleapis.com";
    const string VertexPermission = "aiplatform.endpoints.predict";
    const string VertexRole = "roles/aiplatform.user";

    public static string AdcPath => Path.Combine(
        Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "gcloud", "application_default_credentials.json");

    public static bool IsSignedIn => File.Exists(AdcPath);

    // ---------------- gcloud ----------------

    public static string? FindGcloud()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "gcloud.cmd"));
        var tail = Path.Combine("Google", "Cloud SDK", "google-cloud-sdk", "bin", "gcloud.cmd");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), tail));
        if (Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } la) candidates.Add(Path.Combine(la, tail));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), tail));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), tail));
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// <summary>Downloads Google's Cloud CLI installer and runs it silently for the current user (no UAC).</summary>
    public static async Task<string?> InstallGcloudAsync(Action<string> log, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "VertexAIStudio-gcloud");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "GoogleCloudSDKInstaller.exe");
        log(L.T("Google Cloud CLI 다운로드 중…"));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        await using (var src = await http.GetStreamAsync(InstallerUrl, ct))
        await using (var dst = File.Create(exe))
            await src.CopyToAsync(dst, ct);
        log(L.T("Google Cloud CLI 설치 중 (몇 분 걸립니다)…"));
        using var p = Process.Start(new ProcessStartInfo(exe, "/S /singleuser /noreporting /nostartmenu /nodesktop")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await p.WaitForExitAsync(ct);
        // The NSIS bootstrapper can return before its component install finishes.
        for (var i = 0; i < 600 && FindGcloud() == null; i++) await Task.Delay(1000, ct);
        return FindGcloud();
    }

    /// <summary>`gcloud auth application-default login`: opens the browser; the user signs in there.</summary>
    public static async Task<bool> SignInAsync(string gcloud, Action<string> log, CancellationToken ct) =>
        await RunAsync(gcloud, "auth application-default login --quiet", log, ct) == 0 && File.Exists(AdcPath);

    public static Task<int> SetQuotaProjectAsync(string gcloud, string projectId, Action<string> log, CancellationToken ct) =>
        RunAsync(gcloud, $"auth application-default set-quota-project {projectId} --quiet", log, ct);

    public static async Task<string> GetAccessTokenAsync(string gcloud, CancellationToken ct)
    {
        var token = (await CaptureAsync(gcloud, "auth application-default print-access-token --quiet", ct)).Trim();
        if (string.IsNullOrEmpty(token) || token.Contains(' '))
            throw new InvalidOperationException(L.T("액세스 토큰을 가져오지 못했습니다. 다시 로그인하세요."));
        return token;
    }

    static async Task<string> CaptureAsync(string gcloud, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{gcloud}\" {args}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? stdout : "";
    }

    static async Task<int> RunAsync(string gcloud, string args, Action<string> log, CancellationToken ct)
    {
        log($"> gcloud {args}");
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{gcloud}\" {args}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        return p.ExitCode;
    }

    /// <summary>
    /// Runs a gcloud command authenticated with the ADC access token (--access-token-file), so the
    /// browser sign-in is the only login needed — gcloud's own `auth login` is not required.
    /// </summary>
    static async Task<int> RunWithTokenAsync(string gcloud, string args, string token, Action<string> log, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"vas-token-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, token, ct);
        try { return await RunAsync(gcloud, $"{args} --access-token-file=\"{file}\" --quiet", log, ct); }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>`gcloud config set project` (same as install-windows.ps1).</summary>
    public static Task<int> SetDefaultProjectAsync(string gcloud, string projectId, Action<string> log, CancellationToken ct) =>
        RunAsync(gcloud, $"config set project {projectId} --quiet", log, ct);

    // ---------------- Google Cloud REST (with the ADC access token) ----------------

    static HttpClient Http(string token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    static async Task<(int Status, JsonNode? Body, string Raw)> SendAsync(HttpClient http, HttpMethod m, string url, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = raw.Length > 0 ? JsonNode.Parse(raw) : null; } catch { }
        return ((int)resp.StatusCode, node, raw);
    }

    static string ErrorText((int Status, JsonNode? Body, string Raw) r) =>
        $"HTTP {r.Status}: {(string?)r.Body?["error"]?["message"] ?? r.Raw[..Math.Min(200, r.Raw.Length)]}";

    public static async Task<List<GcpProject>> ListProjectsAsync(string token, CancellationToken ct)
    {
        using var http = Http(token);
        var list = new List<GcpProject>();
        string? page = null;
        do
        {
            var url = "https://cloudresourcemanager.googleapis.com/v1/projects?pageSize=200&filter=lifecycleState%3AACTIVE"
                      + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var r = await SendAsync(http, HttpMethod.Get, url, null, ct);
            if (r.Status != 200) throw new InvalidOperationException(L.T("프로젝트 목록 조회 실패 — ") + ErrorText(r));
            foreach (var p in r.Body?["projects"]?.AsArray() ?? new JsonArray())
                list.Add(new GcpProject((string?)p!["projectId"] ?? "", (string?)p["name"] ?? ""));
            page = (string?)r.Body?["nextPageToken"];
        } while (!string.IsNullOrEmpty(page));
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<string?> GetEmailAsync(string token, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var r = await SendAsync(http, HttpMethod.Get, "https://oauth2.googleapis.com/tokeninfo?access_token=" + Uri.EscapeDataString(token), null, ct);
        return (string?)r.Body?["email"];
    }

    /// <summary>Enables aiplatform.googleapis.com on the project (no-op when already on). Waits for the operation.</summary>
    public static async Task EnableVertexApiAsync(string gcloud, string token, string projectId, Action<string> log, CancellationToken ct)
    {
        using var http = Http(token);
        var svc = $"https://serviceusage.googleapis.com/v1/projects/{projectId}/services/{VertexService}";
        var state = await SendAsync(http, HttpMethod.Get, svc, null, ct);
        if (state.Status == 200 && (string?)state.Body?["state"] == "ENABLED") { log(L.T("Vertex AI API: 이미 사용 설정됨")); return; }

        // Same command as install-windows.ps1; gcloud waits for the operation itself.
        log(L.T("Vertex AI API 사용 설정 중 (gcloud services enable)…"));
        if (await RunWithTokenAsync(gcloud, $"services enable {VertexService} --project {projectId}", token, log, ct) == 0)
        {
            log(L.T("Vertex AI API: 사용 설정 완료"));
            return;
        }

        log(L.T("gcloud로 설정하지 못해 Service Usage API로 다시 시도합니다…"));
        var op = await SendAsync(http, HttpMethod.Post, svc + ":enable", new JsonObject(), ct);
        if (op.Status != 200)
        {
            var msg = ErrorText(op);
            if (msg.Contains("billing", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("이 프로젝트에 결제 계정이 연결되어 있지 않습니다. Google Cloud 콘솔에서 결제를 연결한 뒤 다시 시도하세요. (") + msg + ")");
            throw new InvalidOperationException(L.T("Vertex AI API 사용 설정 실패 — ") + msg
                + L.T(" · 콘솔에서 직접 사용 설정할 수 있습니다: https://console.cloud.google.com/apis/library/aiplatform.googleapis.com?project=") + projectId);
        }
        var name = (string?)op.Body?["name"];
        for (var i = 0; i < 90 && op.Body?["done"]?.GetValue<bool>() != true && name != null; i++)
        {
            await Task.Delay(2000, ct);
            op = await SendAsync(http, HttpMethod.Get, "https://serviceusage.googleapis.com/v1/" + name, null, ct);
        }
        if (op.Body?["error"] is JsonNode err) throw new InvalidOperationException(L.T("Vertex AI API 사용 설정 실패 — ") + (string?)err["message"]);
        log(L.T("Vertex AI API: 사용 설정 완료"));
    }

    public static async Task<bool> HasVertexPermissionAsync(string token, string projectId, CancellationToken ct)
    {
        using var http = Http(token);
        var r = await SendAsync(http, HttpMethod.Post,
            $"https://cloudresourcemanager.googleapis.com/v1/projects/{projectId}:testIamPermissions",
            new JsonObject { ["permissions"] = new JsonArray(VertexPermission) }, ct);
        return r.Status == 200 && (r.Body?["permissions"]?.AsArray().Any(p => (string?)p == VertexPermission) ?? false);
    }

    /// <summary>
    /// Makes sure the signed-in account can call Vertex AI on the project. When the permission is missing,
    /// adds roles/aiplatform.user for the account (works for project owners / IAM admins).
    /// Returns null when OK, otherwise a message for the user.
    /// </summary>
    public static async Task<string?> EnsureVertexPermissionAsync(string gcloud, string token, string projectId, string? email, Action<string> log, CancellationToken ct)
    {
        if (await HasVertexPermissionAsync(token, projectId, ct)) { log(L.T("Vertex AI 권한: 확인됨")); return null; }
        if (string.IsNullOrEmpty(email))
            return L.T("계정 이메일을 확인할 수 없어 권한을 자동으로 추가하지 못했습니다.");

        log(L.F("Vertex AI 권한 추가 중 ({0} → {1})…", VertexRole, email));
        // Same command as install-windows.ps1.
        if (await RunWithTokenAsync(gcloud,
                $"projects add-iam-policy-binding {projectId} --member=user:{email} --role={VertexRole} --condition=None",
                token, log, ct) == 0)
            return await WaitForPermissionAsync(token, projectId, log, ct);

        log(L.T("gcloud로 추가하지 못해 Resource Manager API로 다시 시도합니다…"));
        using var http = Http(token);
        var baseUrl = $"https://cloudresourcemanager.googleapis.com/v1/projects/{projectId}";
        var get = await SendAsync(http, HttpMethod.Post, baseUrl + ":getIamPolicy", new JsonObject(), ct);
        if (get.Status != 200 || get.Body is not JsonObject policy)
            return L.F("프로젝트 권한 정보를 읽을 수 없습니다. 프로젝트 소유자에게 '{0}' 역할을 요청하세요. ({1})", VertexRole, ErrorText(get));

        var member = "user:" + email;
        var bindings = policy["bindings"] as JsonArray ?? new JsonArray();
        policy["bindings"] = bindings;
        var binding = bindings.OfType<JsonObject>().FirstOrDefault(b => (string?)b["role"] == VertexRole && b["condition"] == null);
        if (binding == null) { binding = new JsonObject { ["role"] = VertexRole, ["members"] = new JsonArray() }; bindings.Add(binding); }
        var members = (JsonArray)binding["members"]!;
        if (!members.Any(m => (string?)m == member)) members.Add(member);

        var set = await SendAsync(http, HttpMethod.Post, baseUrl + ":setIamPolicy", new JsonObject { ["policy"] = policy.DeepClone() }, ct);
        if (set.Status != 200)
            return L.F("권한을 자동으로 추가할 수 없습니다. 프로젝트 소유자에게 '{0}'(Vertex AI 사용자) 역할을 요청하세요. ({1})", VertexRole, ErrorText(set));

        return await WaitForPermissionAsync(token, projectId, log, ct);
    }

    static async Task<string?> WaitForPermissionAsync(string token, string projectId, Action<string> log, CancellationToken ct)
    {
        // IAM changes take a little while to propagate.
        for (var i = 0; i < 20; i++)
        {
            if (await HasVertexPermissionAsync(token, projectId, ct)) { log(L.T("Vertex AI 권한: 추가 완료")); return null; }
            await Task.Delay(3000, ct);
        }
        log(L.T("Vertex AI 권한: 추가했지만 아직 반영 중입니다 (몇 분 후 적용)."));
        return null;
    }

    // ---------------- import from an existing proxy install ----------------

    public sealed record ImportedProxySettings(string ProjectId, string? Location, string? Model, string Source);

    /// <summary>Finds the .env of an existing Vertex OpenAI Proxy (its autostart entry, or the usual repo folder).</summary>
    public static ImportedProxySettings? FindExistingProxySettings(IEnumerable<string>? extraDirs = null)
    {
        var dirs = new List<string>();
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (run?.GetValue("VertexOpenAIProxyGui") is string cmd)
            {
                var exe = cmd.Trim().Trim('"').Split('"')[0].Trim();
                if (Path.GetDirectoryName(exe) is { } d) dirs.Add(d);
            }
        }
        catch { }
        if (extraDirs != null) dirs.AddRange(extraDirs);
        dirs.Add(@"C:\AI\vertex-openai-proxy");
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            if (ReadEnv(Path.Combine(d, ".env")) is { } s) return s;
        return null;
    }

    public static ImportedProxySettings? ReadEnv(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var v = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(path))
            {
                var t = raw.Trim().TrimStart('\uFEFF');
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq > 0) v[t[..eq].Trim()] = t[(eq + 1)..].Trim().Trim('"');
            }
            if (!v.TryGetValue("GOOGLE_CLOUD_PROJECT_ID", out var p) || string.IsNullOrWhiteSpace(p)) return null;
            return new ImportedProxySettings(p, v.GetValueOrDefault("GOOGLE_CLOUD_LOCATION"), v.GetValueOrDefault("GOOGLE_CLOUD_MODEL_ID"), path);
        }
        catch { return null; }
    }
}
