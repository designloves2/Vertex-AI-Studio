using ImageStudio.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ImageStudio.App;

public sealed partial class MainWindow : Window
{
    readonly AppPaths _paths = new();
    AppSettings _s;
    readonly Store _store;
    ProxyClient _client;
    readonly StudioService _svc;

    Conversation? _conv;
    List<Message> _msgs = new();
    bool _trashMode;
    List<string> _imageModels = new();
    List<string> _chatModels = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _proxyTimer;
    readonly AppUpdater _updater = new();

    public MainWindow()
    {
        InitializeComponent();
        _s = AppSettings.Load(_paths.Settings);
        L.Init(_s.Language);
        Localizer.Apply(Content);
        VersionText.Text = "V." + _updater.CurrentVersion;
        Title = $"Vertex AI Studio Image V.{_updater.CurrentVersion}";
        _store = new Store(_paths);
        _client = new ProxyClient(_s.EffectiveProxyUrl, _s.TimeoutSeconds);
        _svc = new StudioService(_store, () => _s, () => _client);
        StudioService.Converter = ImageUtil.Convert;
        try { _store.PurgeTrash(); } catch { }

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        GearButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        PanelToggleButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Activated += (_, _) => UpdateTitleBarRegions();
        Closed += (_, _) => { _closed = true; SaveSettings(); _proxy.Dispose(); _store.Dispose(); };

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int w = Math.Min((int)(1480 * scale), area.Width - 40), h = Math.Min((int)(920 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));

        ApplyTheme();
        BuildSettingsPanel();
        BuildSettingsPage();
        ApplySideCollapsed();
        ApplyPanelCollapsed();
        QuickEnhance.IsOn = _s.EnhanceEnabled;
        QuickSearch.IsOn = _s.Generation.GoogleSearch;
        UpdateKeyHint();
        UpdateChips();

        RefreshConversationList();
        var last = _s.LastConversationId != null
            ? _store.ListConversations().FirstOrDefault(c => c.Id == _s.LastConversationId) : null;
        if (last != null) OpenConversation(last); else ShowConversation(null);

        _proxyTimer = DispatcherQueue.CreateTimer();
        _proxyTimer.Interval = TimeSpan.FromSeconds(20);
        _proxyTimer.Tick += async (_, _) => await CheckProxyAsync(false);
        _proxyTimer.Start();
        _ = StartupProxyAsync();
        if (_s.AutoCheckUpdates) _ = CheckForUpdateAsync(silent: true);
        Program.ListenForShowRequests(() => DispatcherQueue.TryEnqueue(BringToFront));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>A second launch was attempted: show this window instead.</summary>
    void BringToFront()
    {
        if (_closed) return;
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>Starts a new instance of the app and closes this one (language change).</summary>
    async void RestartApp()
    {
        SaveSettings();
        await _proxy.StopAsync();   // free port 3001 before the new instance starts its proxy
        Program.ReleaseSingleInstance();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); } catch { return; }
        Close();
    }

    // ---------------- auto-update ----------------

    /// <summary>Checks GitHub Releases; when a newer version exists, offers it in the banner.</summary>
    async Task<string> CheckForUpdateAsync(bool silent)
    {
        if (!_updater.IsInstalled)
            return L.T("개발 빌드라 자동 업데이트를 쓸 수 없습니다 (설치 파일로 설치한 앱만 업데이트됨).");
        try
        {
            var ver = await _updater.CheckAsync();
            if (ver == null) return L.F("최신 버전입니다 ({0}).", _updater.CurrentVersion);
            Banner.Title = L.F("새 버전 {0} 이(가) 있습니다", ver);
            Banner.Message = L.F("현재 {0}. 업데이트하면 앱이 다시 시작됩니다 (대화와 설정은 유지).", _updater.CurrentVersion);
            Banner.Severity = InfoBarSeverity.Informational;
            var btn = new Button { Content = L.T("업데이트 후 재시작") };
            btn.Click += async (_, _) => await ApplyUpdateAsync(btn);
            Banner.ActionButton = btn;
            Banner.IsOpen = true;
            return L.F("새 버전 {0} 이(가) 있습니다.", ver);
        }
        catch (Exception ex)
        {
            var msg = L.T("업데이트 확인 실패: ") + ex.Message;
            if (!silent) ShowBanner(L.T("업데이트 확인 실패"), ex.Message, InfoBarSeverity.Warning);
            return msg;
        }
    }

    async Task ApplyUpdateAsync(Button btn)
    {
        if (_cts != null)
        {
            ShowBanner(L.T("생성 중에는 업데이트할 수 없습니다"), L.T("작업이 끝난 뒤 다시 눌러 주세요."), InfoBarSeverity.Warning);
            return;
        }
        btn.IsEnabled = false;
        try
        {
            await _updater.DownloadAsync(p => DispatcherQueue.TryEnqueue(() => btn.Content = L.F("다운로드 {0}%", p)));
            SaveSettings();
            _updater.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            btn.IsEnabled = true;
            btn.Content = L.T("업데이트 후 재시작");
            ShowBanner(L.T("업데이트 실패"), ex.Message, InfoBarSeverity.Error);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---------------- title bar ----------------

    bool _closed;

    void UpdateTitleBarRegions()
    {
        // Activated can still fire while the window is closing; Content then throws.
        if (_closed || AppWindow == null) return;
        try { if (Content?.XamlRoot == null) return; } catch { return; }
        var scale = Content.XamlRoot.RasterizationScale;
        var rects = new List<RectInt32>();
        foreach (FrameworkElement el in new FrameworkElement[] { GearButton, PanelToggleButton })
        {
            if (el.ActualWidth <= 0) continue;
            var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            rects.Add(new RectInt32((int)(p.X * scale), (int)(p.Y * scale), (int)(el.ActualWidth * scale), (int)(el.ActualHeight * scale)));
        }
        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
    }

    void ApplyTheme()
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = _s.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    void SaveSettings()
    {
        try { _s.Save(_paths.Settings); } catch { }
    }

    // ---------------- proxy status ----------------

    async Task CheckProxyAsync(bool loadModels)
    {
        bool ok;
        try
        {
            if (loadModels || _imageModels.Count == 0)
            {
                var img = await _client.ListModelsAsync("image");
                var chat = await _client.ListModelsAsync("chat");
                _imageModels = img.Select(m => m.Id).ToList();
                _chatModels = chat.Select(m => m.Id).ToList();
                FillModelCombos();
            }
            else await _client.ListModelsAsync("image");
            ok = true;
        }
        catch { ok = false; }

        var host = new Uri(_s.EffectiveProxyUrl).Authority;
        var kind = _s.UseEmbeddedProxy ? L.T("내장 프록시") : "Proxy";
        ProxyDot.Fill = new SolidColorBrush(ok ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F) : Windows.UI.Color.FromArgb(255, 0xE8, 0x4A, 0x4A));
        ProxyText.Text = ok ? $"{kind} {host} · {_s.Generation.Region}" : L.F("{0} {1} · 연결 안 됨", kind, host);
        if (!ok && SetupPage.Visibility != Visibility.Visible)
            _proxyBannerShown = ShowBannerTracked(L.T("프록시에 연결할 수 없습니다"),
                _s.UseEmbeddedProxy ? L.T("내장 프록시가 실행되지 않았습니다. 설정 › 내장 프록시에서 상태와 로그를 확인하세요.")
                                    : L.T("Vertex OpenAI Proxy 앱을 실행하세요. (") + _s.ProxyUrl + ")", InfoBarSeverity.Warning);
        else if (Banner.IsOpen && _proxyBannerShown) { Banner.IsOpen = false; _proxyBannerShown = false; }
    }

    bool _proxyBannerShown;

    bool ShowBannerTracked(string title, string message, InfoBarSeverity sev) { ShowBanner(title, message, sev); return true; }

    void ShowBanner(string title, string message, InfoBarSeverity sev)
    {
        _proxyBannerShown = false;
        Banner.ActionButton = null;
        Banner.Title = title;
        Banner.Message = message;
        Banner.Severity = sev;
        Banner.IsOpen = true;
    }

    // ---------------- collapsible panes ----------------

    void ToggleSide_Click(object sender, RoutedEventArgs e)
    {
        _s.SessionListCollapsed = !_s.SessionListCollapsed;
        ApplySideCollapsed();
        SaveSettings();
    }

    void ApplySideCollapsed()
    {
        var c = _s.SessionListCollapsed;
        SideColumn.Width = new GridLength(c ? 48 : 240);
        SideExpanded.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        SideRail.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
    }

    void RailSearch_Click(object sender, RoutedEventArgs e)
    {
        _s.SessionListCollapsed = false;
        ApplySideCollapsed();
        SearchBox.Focus(FocusState.Programmatic);
    }

    void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        _s.SettingsPanelCollapsed = !_s.SettingsPanelCollapsed;
        ApplyPanelCollapsed();
        SaveSettings();
    }

    void ApplyPanelCollapsed()
    {
        var c = _s.SettingsPanelCollapsed;
        SetColumn.Width = new GridLength(c ? 48 : 320);
        SetExpanded.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        SetRail.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
    }

    void Gear_Click(object sender, RoutedEventArgs e) =>
        SettingsPage.Visibility = SettingsPage.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    void Back_Click(object sender, RoutedEventArgs e) => SettingsPage.Visibility = Visibility.Collapsed;

    // ---------------- conversation list ----------------

    void RefreshConversationList()
    {
        var items = ConversationList.Items;
        items.Clear();
        var list = _store.ListConversations(SearchBox.Text, _trashMode);
        if (_trashMode)
        {
            items.Add(GroupHeader(L.T("휴지통 · 30일 후 영구 삭제")));
            foreach (var c in list) items.Add(ConversationItem(c));
        }
        else
        {
            string? group = null;
            foreach (var c in list)
            {
                var g = GroupOf(c.UpdatedAt);
                if (g != group) { items.Add(GroupHeader(g)); group = g; }
                var item = ConversationItem(c);
                items.Add(item);
                if (c.Id == _conv?.Id) ConversationList.SelectedItem = item;
            }
        }
        var trash = _store.TrashCount();
        TrashText.Text = _trashMode ? L.T("← 대화 목록으로") : L.F("휴지통 ({0})", trash);
    }

    static string GroupOf(long ms)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.Date;
        var today = DateTime.Today;
        if (d == today) return L.T("오늘");
        if (d == today.AddDays(-1)) return L.T("어제");
        if (d > today.AddDays(-7)) return L.T("지난 7일");
        if (d > today.AddDays(-30)) return L.T("지난 30일");
        return d.ToString(L.T("yyyy년 M월"));
    }

    static ListViewItem GroupHeader(string text) => new()
    {
        Content = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["SubText"] },
        IsHitTestVisible = false, IsTabStop = false, MinHeight = 28, Padding = new Thickness(6, 8, 6, 0),
    };

    ListViewItem ConversationItem(Conversation c)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = c.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var n = new TextBlock { Text = c.MessageCount.ToString(), Style = (Style)Application.Current.Resources["SubText"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(n, 1);
        g.Children.Add(n);
        var item = new ListViewItem { Content = g, Tag = c, Padding = new Thickness(10, 0, 10, 0), MinHeight = 36 };

        var menu = new MenuFlyout();
        if (_trashMode)
        {
            menu.Items.Add(MenuItem(L.T("복원"), "", () => { _store.RestoreConversation(c.Id); RefreshConversationList(); }));
            menu.Items.Add(MenuItem(L.T("영구 삭제"), "", async () =>
            {
                if (await Confirm(L.T("영구 삭제"), L.F("\"{0}\" 대화와 이미지를 영구 삭제합니다. 되돌릴 수 없습니다.", c.Title), L.T("삭제")))
                {
                    c.DeletedAt = 0; _store.SaveConversation(c); _store.PurgeTrash(all: true); RefreshConversationList();
                }
            }));
        }
        else
        {
            menu.Items.Add(MenuItem(L.T("이름 변경"), "", async () => await RenameConversation(c)));
            menu.Items.Add(MenuItem(L.T("휴지통으로 이동"), "", () =>
            {
                _store.TrashConversation(c.Id);
                if (_conv?.Id == c.Id) ShowConversation(null);
                RefreshConversationList();
            }));
        }
        item.ContextFlyout = menu;
        return item;
    }

    static MenuFlyoutItem MenuItem(string text, string glyph, Action click)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += (_, _) => click();
        return mi;
    }

    async Task RenameConversation(Conversation c)
    {
        var box = new TextBox { Text = c.Title, SelectionStart = 0, SelectionLength = c.Title.Length };
        var dlg = new ContentDialog
        {
            Title = L.T("이름 변경"), Content = box, PrimaryButtonText = L.T("저장"), CloseButtonText = L.T("취소"),
            DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            c.Title = box.Text.Trim();
            _store.SaveConversation(c);
            RefreshConversationList();
        }
    }

    async Task<bool> Confirm(string title, string text, string ok)
    {
        var dlg = new ContentDialog
        {
            Title = title, Content = text, PrimaryButtonText = ok, CloseButtonText = L.T("취소"),
            DefaultButton = ContentDialogButton.Close, XamlRoot = Content.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationList.SelectedItem is ListViewItem { Tag: Conversation c } && c.Id != _conv?.Id)
        {
            if (_trashMode) return;
            OpenConversation(c);
        }
    }

    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RefreshConversationList();

    void Trash_Click(object sender, RoutedEventArgs e)
    {
        _trashMode = !_trashMode;
        RefreshConversationList();
    }

    void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_trashMode) { _trashMode = false; RefreshConversationList(); }
        ShowConversation(null);
        ConversationList.SelectedItem = null;
        PromptBox.Focus(FocusState.Programmatic);
    }

    void OpenConversation(Conversation c)
    {
        _conv = c;
        _msgs = _store.LoadMessages(c.Id);
        // Turns left pending by a crash/close are shown as cancelled.
        foreach (var m in _msgs.Where(m => m.Status == MessageStatus.Pending))
        {
            m.Status = MessageStatus.Cancelled;
            m.Error = L.T("앱이 종료되어 중단됨");
            _store.SaveMessage(m);
        }
        _s.LastConversationId = c.Id;
        SaveSettings();
        ClearContinue();
        RenderMessages();
    }

    void ShowConversation(Conversation? c)
    {
        _conv = c;
        _msgs = new();
        _s.LastConversationId = null;
        ClearContinue();
        RenderMessages();
    }
}
