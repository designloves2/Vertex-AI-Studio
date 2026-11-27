using ImageStudio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace ImageStudio.App;

/// <summary>Embedded proxy lifecycle, first-run Google setup and the proxy settings card.</summary>
public sealed partial class MainWindow
{
    readonly ProxyHost _proxy = new();
    CancellationTokenSource? _setupCts;
    TaskCompletionSource<string?>? _projectChoice;
    TextBlock? _proxyStateText;
    TextBox? _proxyLogBox;

    ProxyConfig CurrentProxyConfig() =>
        new(_s.GcpProjectId ?? "", string.IsNullOrWhiteSpace(_s.GcpLocation) ? "global" : _s.GcpLocation, _s.ProxyDefaultModel, _s.ProxyPort);

    // =====================================================================
    // Startup
    // =====================================================================

    async Task StartupProxyAsync()
    {
        _proxy.Output += (line, _) => DispatcherQueue.TryEnqueue(() => AppendProxyLog(line));
        _proxy.StateChanged += _ => DispatcherQueue.TryEnqueue(UpdateProxyState);

        if (!_s.UseEmbeddedProxy) { await CheckProxyAsync(true); return; }
        ImportExistingProxySettingsOnce();
        if (!GoogleSetup.IsSignedIn || string.IsNullOrWhiteSpace(_s.GcpProjectId))
        {
            ShowSetup();
            // First run on this PC: start right away; the user only signs in in the browser.
            if (!GoogleSetup.IsSignedIn) _ = RunSetupAsync(forceLogin: false);
            return;
        }
        await StartEmbeddedProxyAsync();
    }

    /// <summary>Brings the existing Vertex OpenAI Proxy's settings (.env) into the app, once.</summary>
    void ImportExistingProxySettingsOnce()
    {
        if (_s.ProxyImportChecked || !string.IsNullOrWhiteSpace(_s.GcpProjectId)) return;
        _s.ProxyImportChecked = true;
        if (GoogleSetup.FindExistingProxySettings() is { } imp) ApplyImported(imp);
        SaveSettings();
    }

    void ApplyImported(GoogleSetup.ImportedProxySettings imp)
    {
        _s.GcpProjectId = imp.ProjectId;
        if (!string.IsNullOrWhiteSpace(imp.Location)) _s.GcpLocation = imp.Location;
        if (!string.IsNullOrWhiteSpace(imp.Model)) _s.ProxyDefaultModel = imp.Model;
        AppendProxyLog(L.F("[기존 프록시 설정을 가져옴: {0} → 프로젝트 {1}]", imp.Source, imp.ProjectId));
    }

    async Task<bool> StartEmbeddedProxyAsync()
    {
        if (_proxy.IsRunning) return true;
        ProxyText.Text = L.T("내장 프록시 시작 중…");
        var err = _proxy.Start(CurrentProxyConfig());
        if (err != null)
        {
            ShowBanner(L.T("내장 프록시를 시작할 수 없습니다"), err, InfoBarSeverity.Error);
            UpdateProxyState();
            return false;
        }
        var ok = await _proxy.WaitHealthyAsync(TimeSpan.FromSeconds(30));
        if (!ok) ShowBanner(L.T("내장 프록시가 응답하지 않습니다"), L.T("설정 › 내장 프록시의 로그를 확인하세요."), InfoBarSeverity.Error);
        await CheckProxyAsync(true);
        UpdateProxyState();
        return ok;
    }

    async Task RestartEmbeddedProxyAsync()
    {
        await _proxy.StopAsync();
        RecreateClient();
        if (_s.UseEmbeddedProxy) await StartEmbeddedProxyAsync();
        else await CheckProxyAsync(true);
    }

    void UpdateProxyState()
    {
        if (_proxyStateText == null) return;
        _proxyStateText.Text = !_s.UseEmbeddedProxy ? L.T("사용 안 함 (외부 프록시 사용)")
            : _proxy.IsRunning ? L.F("실행 중 · 127.0.0.1:{0}", _proxy.Port)
            : L.T("중지됨");
    }

    void AppendProxyLog(string line)
    {
        if (_proxyLogBox == null) return;
        var t = _proxyLogBox.Text + line + "\n";
        if (t.Length > 60000) t = t[^50000..];
        _proxyLogBox.Text = t;
        _proxyLogBox.SelectionStart = t.Length;
    }

    // =====================================================================
    // First-run setup page
    // =====================================================================

    static readonly string[] SetupStepNames =
    {
        L.T("Google Cloud CLI 준비"), L.T("Google 계정 로그인"), L.T("Google Cloud 프로젝트 선택"),
        L.T("Vertex AI 권한 확인·부여 (roles/aiplatform.user)"), L.T("Vertex AI API 사용 설정"), L.T("내장 프록시 시작"),
    };
    enum StepState { Pending, Running, Done, Warn, Failed }
    readonly StepState[] _steps = new StepState[SetupStepNames.Length];
    readonly string?[] _stepNotes = new string?[SetupStepNames.Length];

    void ShowSetup()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        SetupPage.Visibility = Visibility.Visible;
        RenderSteps();
    }

    void HideSetup() => SetupPage.Visibility = Visibility.Collapsed;

    void SetStep(int i, StepState st, string? note = null)
    {
        _steps[i] = st;
        _stepNotes[i] = note;
        DispatcherQueue.TryEnqueue(RenderSteps);
    }

    void RenderSteps()
    {
        SetupSteps.Children.Clear();
        for (int i = 0; i < SetupStepNames.Length; i++)
        {
            var (glyph, color) = _steps[i] switch
            {
                StepState.Done => ("", Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F)),
                StepState.Running => ("", Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF)),
                StepState.Warn => ("", Windows.UI.Color.FromArgb(255, 0xF5, 0xC0, 0x4A)),
                StepState.Failed => ("", Windows.UI.Color.FromArgb(255, 0xE8, 0x4A, 0x4A)),
                _ => ("", Windows.UI.Color.FromArgb(255, 0x88, 0x88, 0x88)),
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{i + 1}. {SetupStepNames[i]}", Style = Res("RowLabel") });
            if (!string.IsNullOrEmpty(_stepNotes[i])) text.Children.Add(new TextBlock { Text = _stepNotes[i], Style = Res("SubText") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            SetupSteps.Children.Add(row);
        }
    }

    void SetupLogLine(string line) => DispatcherQueue.TryEnqueue(() =>
    {
        SetupLog.Text += line + "\n";
        SetupLog.SelectionStart = SetupLog.Text.Length;
    });

    void SetupBusy(bool busy)
    {
        SetupRing.IsActive = busy;
        SetupRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SetupStartButton.IsEnabled = !busy;
        SetupCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SetupStartText.Text = busy ? L.T("설정 진행 중…") : L.T("다시 시도");
    }

    void SetupMessageShow(string title, string msg, InfoBarSeverity sev)
    {
        SetupMessage.Title = title;
        SetupMessage.Message = msg;
        SetupMessage.Severity = sev;
        SetupMessage.IsOpen = true;
    }

    async void SetupStart_Click(object sender, RoutedEventArgs e) => await RunSetupAsync(forceLogin: GoogleSetup.IsSignedIn && _steps[1] == StepState.Failed);
    void SetupCancel_Click(object sender, RoutedEventArgs e) { _setupCts?.Cancel(); _projectChoice?.TrySetResult(null); }

    void SetupLater_Click(object sender, RoutedEventArgs e)
    {
        _setupCts?.Cancel();
        HideSetup();
        ShowBanner(L.T("Google 설정이 끝나지 않았습니다"), L.T("설정(⚙) › 내장 프록시에서 언제든 'Google 계정 설정'을 다시 실행할 수 있습니다."), InfoBarSeverity.Warning);
    }

    /// <summary>The whole first-run flow. forceLogin = sign in again even if credentials exist.</summary>
    async Task RunSetupAsync(bool forceLogin)
    {
        if (_setupCts != null) return;
        _setupCts = new CancellationTokenSource();
        var ct = _setupCts.Token;
        Array.Fill(_steps, StepState.Pending);
        Array.Fill(_stepNotes, null);
        SetupMessage.IsOpen = false;
        SetupProjectPanel.Visibility = Visibility.Collapsed;
        ShowSetup();
        SetupBusy(true);
        int step = 0;
        try
        {
            // 1. gcloud
            SetStep(step = 0, StepState.Running);
            var gcloud = GoogleSetup.FindGcloud();
            if (gcloud == null)
            {
                SetStep(0, StepState.Running, L.T("설치되어 있지 않아 자동으로 설치합니다 (몇 분)."));
                gcloud = await GoogleSetup.InstallGcloudAsync(SetupLogLine, ct);
                if (gcloud == null) throw new InvalidOperationException(L.T("Google Cloud CLI를 설치하지 못했습니다. 인터넷 연결을 확인하세요."));
            }
            SetStep(0, StepState.Done, gcloud);

            // 2. sign-in
            SetStep(step = 1, StepState.Running);
            if (forceLogin || !GoogleSetup.IsSignedIn)
            {
                SetStep(1, StepState.Running, L.T("브라우저 창에서 Google 계정으로 로그인하세요."));
                if (!await GoogleSetup.SignInAsync(gcloud, SetupLogLine, ct))
                    throw new InvalidOperationException(L.T("로그인이 완료되지 않았습니다."));
            }
            var token = await GoogleSetup.GetAccessTokenAsync(gcloud, ct);
            var email = await GoogleSetup.GetEmailAsync(token, ct);
            SetStep(1, StepState.Done, email ?? L.T("로그인됨"));

            // 3. project
            SetStep(step = 2, StepState.Running, L.T("프로젝트 목록 확인 중…"));
            var projects = await GoogleSetup.ListProjectsAsync(token, ct);
            string? project = null;
            if (!string.IsNullOrWhiteSpace(_s.GcpProjectId) && projects.Any(p => p.Id == _s.GcpProjectId)) project = _s.GcpProjectId;
            else if (projects.Count == 1) project = projects[0].Id;
            else if (projects.Count == 0)
                throw new InvalidOperationException(L.T("이 계정에 Google Cloud 프로젝트가 없습니다. https://console.cloud.google.com/projectcreate 에서 프로젝트를 만든 뒤 다시 시도하세요."));
            else
            {
                SetStep(2, StepState.Running, L.F("프로젝트가 {0}개 있습니다. 아래에서 고르세요.", projects.Count));
                project = await AskProjectAsync(projects);
                if (project == null) throw new OperationCanceledException();
            }
            _s.GcpProjectId = project;
            SaveSettings();
            SetStep(2, StepState.Done, project);

            // Same order as install-windows.ps1: default project + ADC quota project, then IAM, then API.
            await GoogleSetup.SetDefaultProjectAsync(gcloud, project!, SetupLogLine, ct);
            if (await GoogleSetup.SetQuotaProjectAsync(gcloud, project!, SetupLogLine, ct) != 0)
                SetupLogLine(L.T("quota project 설정에 실패했습니다. 계속 진행합니다."));

            // 4. Vertex AI permission (install-windows.ps1 grants the role before enabling the API)
            SetStep(step = 3, StepState.Running);
            var permIssue = await GoogleSetup.EnsureVertexPermissionAsync(gcloud, token, project!, email, SetupLogLine, ct);
            SetStep(3, permIssue == null ? StepState.Done : StepState.Warn, permIssue ?? L.T("Vertex AI 사용자 권한 확인됨"));

            // 5. Vertex AI API
            SetStep(step = 4, StepState.Running);
            await GoogleSetup.EnableVertexApiAsync(gcloud, token, project!, SetupLogLine, ct);
            SetStep(4, StepState.Done, L.T("aiplatform.googleapis.com 사용 중"));

            // 6. embedded proxy
            SetStep(step = 5, StepState.Running);
            _s.UseEmbeddedProxy = true;
            RecreateClient();
            await _proxy.StopAsync();
            var ok = await StartEmbeddedProxyAsync();
            if (!ok) throw new InvalidOperationException(L.T("내장 프록시가 시작되지 않았습니다. 진행 로그와 설정 › 내장 프록시 로그를 확인하세요."));
            SetStep(5, StepState.Done, $"127.0.0.1:{_s.ProxyPort}");

            BuildSettingsPage();
            SetupMessageShow(L.T("설정 완료"), permIssue == null ? L.T("이제 이미지를 만들 수 있습니다.") : L.T("권한 문제가 남아 있어 요청이 실패할 수 있습니다. ") + permIssue,
                permIssue == null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (permIssue == null)
            {
                await Task.Delay(1500);
                HideSetup();
            }
        }
        catch (OperationCanceledException)
        {
            SetStep(step, StepState.Failed, L.T("취소됨"));
        }
        catch (Exception ex)
        {
            SetStep(step, StepState.Failed, ex.Message);
            SetupMessageShow(L.T("설정을 완료하지 못했습니다"), ex.Message, InfoBarSeverity.Error);
            SetupLogLine(L.T("오류: ") + ex);
        }
        finally
        {
            _setupCts?.Dispose();
            _setupCts = null;
            SetupBusy(false);
            SetupProjectPanel.Visibility = Visibility.Collapsed;
        }
    }

    Task<string?> AskProjectAsync(List<GcpProject> projects)
    {
        _projectChoice = new TaskCompletionSource<string?>();
        var tcs = _projectChoice;
        DispatcherQueue.TryEnqueue(() =>
        {
            SetupProjectCombo.Items.Clear();
            foreach (var p in projects)
                SetupProjectCombo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(p.Name) ? p.Id : $"{p.Name}  ({p.Id})", Tag = p.Id });
            SetupProjectCombo.SelectedIndex = 0;
            SetupProjectPanel.Visibility = Visibility.Visible;
            SetupProjectOk.Click -= ProjectOk;
            SetupProjectOk.Click += ProjectOk;
        });
        return tcs.Task;

        void ProjectOk(object s, RoutedEventArgs e)
        {
            if (SetupProjectCombo.SelectedItem is ComboBoxItem { Tag: string id }) tcs.TrySetResult(id);
            SetupProjectPanel.Visibility = Visibility.Collapsed;
        }
    }

    // =====================================================================
    // Settings page card
    // =====================================================================

    void BuildProxyCard(Panel page)
    {
        var c = new StackPanel();

        _proxyStateText = new TextBlock { Style = Res("RowLabel") };
        var restart = new Button { Content = L.T("재시작") };
        restart.Click += async (_, _) => { restart.IsEnabled = false; await RestartEmbeddedProxyAsync(); restart.IsEnabled = true; };
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        stateRow.Children.Add(_proxyStateText);
        stateRow.Children.Add(restart);
        Row(c, L.T("상태"), stateRow);
        UpdateProxyState();

        var external = new StackPanel { Visibility = _s.UseEmbeddedProxy ? Visibility.Collapsed : Visibility.Visible };
        var embedded = new StackPanel { Visibility = _s.UseEmbeddedProxy ? Visibility.Visible : Visibility.Collapsed };
        Row(c, L.T("내장 프록시 사용 (별도 프록시 앱 불필요)"), Toggle(_s.UseEmbeddedProxy, async v =>
        {
            _s.UseEmbeddedProxy = v;
            embedded.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            external.Visibility = v ? Visibility.Collapsed : Visibility.Visible;
            await RestartEmbeddedProxyAsync();
        }));

        // ---- embedded settings ----
        var auth = new TextBlock
        {
            Style = Res("SubText"),
            Text = GoogleSetup.IsSignedIn ? L.T("Google 계정: 로그인됨 (Application Default Credentials)") : L.T("Google 계정: 로그인 필요"),
        };
        var setupBtn = new Button { Content = L.T("Google 계정 설정 (로그인·API·권한)") };
        setupBtn.Click += async (_, _) => await RunSetupAsync(forceLogin: true);
        Row(embedded, L.T("Google 로그인과 Vertex 설정을 다시 실행"), setupBtn);
        embedded.Children.Add(auth);

        var projectBox = new TextBox { Text = _s.GcpProjectId ?? "", MinWidth = 220, PlaceholderText = L.T("프로젝트 ID") };
        projectBox.LostFocus += (_, _) => { _s.GcpProjectId = projectBox.Text.Trim(); SaveSettings(); };
        var pick = new Button { Content = L.T("목록") };
        pick.Click += async (_, _) => await PickProjectIntoAsync(projectBox, pick);
        var projRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        projRow.Children.Add(projectBox);
        projRow.Children.Add(pick);
        Row(embedded, L.T("Google Cloud 프로젝트"), projRow);

        var loc = new TextBox { Text = _s.GcpLocation, MinWidth = 220 };
        loc.LostFocus += (_, _) => { _s.GcpLocation = string.IsNullOrWhiteSpace(loc.Text) ? "global" : loc.Text.Trim(); SaveSettings(); };
        Row(embedded, L.T("리전 (GOOGLE_CLOUD_LOCATION)"), loc);
        var model = new TextBox { Text = _s.ProxyDefaultModel, MinWidth = 220 };
        model.LostFocus += (_, _) => { _s.ProxyDefaultModel = model.Text.Trim(); SaveSettings(); };
        Row(embedded, L.T("기본 모델 (요청에 모델이 없을 때)"), model);
        Row(embedded, L.T("포트 (기존 프록시 3000과 분리)"), Number(_s.ProxyPort, 1024, 65535, v => { _s.ProxyPort = v; SaveSettings(); }));

        var import = new Button { Content = L.T("가져오기…") };
        import.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            if (GoogleSetup.ReadEnv(Path.Combine(folder.Path, ".env")) is { } imp)
            {
                ApplyImported(imp);
                SaveSettings();
                BuildSettingsPage();
                ShowBanner(L.T("기존 프록시 설정을 가져왔습니다"), L.F("프로젝트 {0} · 재시작을 누르면 적용됩니다.", imp.ProjectId), InfoBarSeverity.Success);
            }
            else ShowBanner(L.T("가져올 설정이 없습니다"), L.T("선택한 폴더에 프로젝트 ID가 들어 있는 .env가 없습니다."), InfoBarSeverity.Warning);
        };
        Row(embedded, L.T("기존 Vertex OpenAI Proxy 폴더의 .env 가져오기"), import);
        embedded.Children.Add(new TextBlock { Text = L.T("프로젝트·리전·모델·포트를 바꾼 뒤에는 '재시작'을 눌러 적용하세요."), Style = Res("SubText") });

        _proxyLogBox = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260, FontSize = 12,
            FontFamily = new FontFamily("Consolas"), Text = string.Join("\n", _proxy.RecentLog) + "\n",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_proxyLogBox, ScrollBarVisibility.Auto);
        embedded.Children.Add(new Expander { Header = L.T("프록시 로그"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = _proxyLogBox, Margin = new Thickness(0, 8, 0, 0) });
        c.Children.Add(embedded);

        // ---- external proxy ----
        var url = new TextBox { Text = _s.ProxyUrl, MinWidth = 280 };
        var status = new TextBlock { Style = Res("SubText") };
        var test = new Button { Content = L.T("연결 테스트") };
        test.Click += async (_, _) =>
        {
            ApplyProxyUrl(url.Text);
            status.Text = L.T("확인 중…");
            try
            {
                var models = await _client.ListModelsAsync("image", refresh: true);
                status.Text = L.F("연결됨 · 이미지 모델 {0}개", models.Count);
                await CheckProxyAsync(true);
            }
            catch (ProxyException ex) { status.Text = ex.UserMessage; }
        };
        url.LostFocus += (_, _) => ApplyProxyUrl(url.Text);
        var urlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        urlRow.Children.Add(url);
        urlRow.Children.Add(test);
        Row(external, L.T("외부 프록시 주소"), urlRow);
        external.Children.Add(status);
        c.Children.Add(external);

        Row(c, L.T("요청 타임아웃 (초)"), Number(_s.TimeoutSeconds, 60, 1200, v => { _s.TimeoutSeconds = v; RecreateClient(); }));
        Card(page, L.T("내장 프록시 · Google 계정"), c);
    }

    async Task PickProjectIntoAsync(TextBox box, Button anchor)
    {
        var gcloud = GoogleSetup.FindGcloud();
        if (gcloud == null || !GoogleSetup.IsSignedIn)
        {
            ShowBanner(L.T("먼저 Google 계정 설정을 실행하세요"), L.T("로그인해야 프로젝트 목록을 볼 수 있습니다."), InfoBarSeverity.Warning);
            return;
        }
        anchor.IsEnabled = false;
        try
        {
            var token = await GoogleSetup.GetAccessTokenAsync(gcloud, CancellationToken.None);
            var projects = await GoogleSetup.ListProjectsAsync(token, CancellationToken.None);
            var menu = new MenuFlyout();
            foreach (var p in projects)
            {
                var id = p.Id;
                var item = new ToggleMenuFlyoutItem { Text = string.IsNullOrEmpty(p.Name) ? id : $"{p.Name}  ({id})", IsChecked = id == box.Text.Trim() };
                item.Click += (_, _) => { box.Text = id; _s.GcpProjectId = id; SaveSettings(); };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = L.T("프로젝트가 없습니다"), IsEnabled = false });
            menu.ShowAt(box);
        }
        catch (Exception ex) { ShowBanner(L.T("프로젝트 목록을 불러오지 못했습니다"), ex.Message, InfoBarSeverity.Error); }
        finally { anchor.IsEnabled = true; }
    }
}
