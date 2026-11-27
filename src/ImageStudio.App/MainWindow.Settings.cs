using ImageStudio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;

namespace ImageStudio.App;

public sealed partial class MainWindow
{
    bool _settingsBuilding;
    ComboBox _modelCombo = null!, _enhanceCombo = null!, _pageEnhanceCombo = null!, _pageCompactCombo = null!;
    ToggleSwitch _enhanceSw = null!, _searchSw = null!;
    TextBlock _ratioLabel = null!;
    TextBox _outDirBox = null!;

    // =====================================================================
    // Right-hand image settings panel
    // =====================================================================

    void BuildSettingsPanel()
    {
        _settingsBuilding = true;
        var g = _s.Generation;
        var p = SetPanel;
        while (p.Children.Count > 1) p.Children.RemoveAt(1);

        Header(p, L.T("모델 설정"));
        _modelCombo = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Right };
        _modelCombo.SelectionChanged += (_, _) =>
        {
            if (_settingsBuilding || _modelCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            g.Model = id;
            Changed();
        };
        // Model labels are long ("Nano Banana 2 lite (gemini-…)"), so the picker gets its own full-width line.
        p.Children.Add(new TextBlock { Text = L.T("모델"), Style = Res("RowLabel"), Margin = new Thickness(0, 6, 0, 4) });
        _modelCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        p.Children.Add(_modelCombo);
        Row(p, L.T("출력"), Seg(new[] { L.T("이미지+텍스트"), L.T("이미지") }, (int)g.Output, i => { g.Output = (OutputModality)i; Changed(); }));
        Row(p, L.T("사고 수준"), Seg(new[] { L.T("낮음"), L.T("중간"), L.T("높음") }, (int)g.Thinking, i => { g.Thinking = (ThinkingLevel)i; Changed(); }));
        Row(p, L.T("미디어 해상도"), Combo(new[] { L.T("기본값"), L.T("낮음"), L.T("보통"), L.T("높음") }, (int)g.MediaResolution, i => { g.MediaResolution = (MediaResolution)i; Changed(); }));
        Sub(p, L.T("참조 이미지를 모델이 해석하는 정밀도 (높을수록 토큰 증가)"));

        _ratioLabel = new TextBlock { Foreground = ThemeBrush("AccentText"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Row(p, L.T("가로세로 비율"), _ratioLabel);
        var ratios = new Grid { ColumnSpacing = 5, RowSpacing = 5, Margin = new Thickness(0, 2, 0, 6) };
        for (int c = 0; c < 4; c++) ratios.ColumnDefinitions.Add(new ColumnDefinition());
        var ratioButtons = new List<ToggleButton>();
        for (int i = 0; i < GenerationSettings.AspectRatios.Length; i++)
        {
            var r = GenerationSettings.AspectRatios[i];
            if (i % 4 == 0) ratios.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var b = new ToggleButton
            {
                Content = r == "auto" ? L.T("자동") : r, IsChecked = g.AspectRatio == r, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 4, 0, 5),
            };
            b.Click += (_, _) =>
            {
                g.AspectRatio = r;
                foreach (var o in ratioButtons) o.IsChecked = o == b;
                Changed();
            };
            ratioButtons.Add(b);
            Grid.SetRow(b, i / 4);
            Grid.SetColumn(b, i % 4);
            ratios.Children.Add(b);
        }
        p.Children.Add(ratios);
        Row(p, L.T("출력 해상도"), Seg(new[] { "1K", "2K", "4K" }, (int)g.Size, i => { g.Size = (ImageSize)i; Changed(); }));
        Row(p, L.T("출력 형식"), Seg(new[] { "PNG", "JPEG" }, (int)g.Format, i => { g.Format = (OutputFormat)i; Changed(); }));

        p.Children.Add(new TextBlock { Text = L.T("출력 디렉터리"), Style = Res("RowLabel"), Margin = new Thickness(0, 8, 0, 4) });
        var dirRow = new Grid { ColumnSpacing = 6 };
        dirRow.ColumnDefinitions.Add(new ColumnDefinition());
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _outDirBox = new TextBox { Text = _s.OutputDirectory, FontSize = 12 };
        _outDirBox.LostFocus += (_, _) => { _s.OutputDirectory = _outDirBox.Text.Trim(); SaveSettings(); };
        var browse = new Button { Content = L.T("찾아보기"), FontSize = 12 };
        browse.Click += async (_, _) => await PickOutputDir();
        Grid.SetColumn(browse, 1);
        dirRow.Children.Add(_outDirBox);
        dirRow.Children.Add(browse);
        p.Children.Add(dirRow);

        Row(p, L.T("인물 생성"), Combo(new[] { L.T("허용 (모든 연령)"), L.T("허용 (성인 전용)"), L.T("허용 안함") }, (int)g.Person, i => { g.Person = (PersonGeneration)i; Changed(); }));
        Row(p, L.T("유명인"), Combo(new[] { L.T("유명인 허용"), L.T("유명인 허용 안함") }, g.AllowCelebrities ? 0 : 1, i => { g.AllowCelebrities = i == 0; Changed(); }));
        Sub(p, L.T("현재 Vertex 이미지 모델에는 이 옵션이 없어 적용되지 않습니다 (모델 기본 정책 사용)"));

        Header(p, L.T("고급 모드"));
        var card = new StackPanel();
        _searchSw = Toggle(g.GoogleSearch, v => { g.GoogleSearch = v; QuickSearchSync(); Changed(); });
        Row(card, L.T("Google 검색"), _searchSw);
        Row(card, L.T("Google 서비스의 검색 결과"), Toggle(g.GoogleSearchServices, v => { g.GoogleSearchServices = v; Changed(); }), sub: true);
        Row(card, L.T("파트너"), Toggle(g.Partner, v => { g.Partner = v; Changed(); }));
        Row(card, L.T("그라운딩 파트너의 검색 결과"), Toggle(g.PartnerResults, v => { g.PartnerResults = v; Changed(); }), sub: true);
        p.Children.Add(new Border { Style = Res("CardStyle"), Child = card, Margin = new Thickness(0, 4, 0, 4) });
        Sub(p, L.T("현재 이미지 모델은 검색 도구를 지원하지 않아 요청에서 자동으로 제외됩니다. 파트너 그라운딩은 Vertex에 해당 기능이 없습니다."));
        Row(p, L.T("리전"), Combo(new[] { L.T("global (전역)"), "us-central1", "europe-west4", "asia-northeast3" },
            Math.Max(0, Array.IndexOf(new[] { "global", "us-central1", "europe-west4", "asia-northeast3" }, g.Region)),
            i => { g.Region = new[] { "global", "us-central1", "europe-west4", "asia-northeast3" }[i]; Changed(); _ = CheckProxyAsync(false); }));
        Row(p, L.T("출력 토큰 한도"), Number(g.MaxOutputTokens, 256, 65536, v => { g.MaxOutputTokens = v; Changed(); }));
        Row(p, L.T("모델 대답 스트리밍"), Toggle(g.Streaming, v => { g.Streaming = v; Changed(); }));

        Header(p, L.T("프롬프트 인핸스"));
        _enhanceSw = Toggle(_s.EnhanceEnabled, v => { _s.EnhanceEnabled = v; QuickEnhanceSync(); Changed(); });
        Row(p, L.T("사용"), _enhanceSw);
        _enhanceCombo = ModelChooser(_s.EnhanceModel, v => { _s.EnhanceModel = v; SyncEnhanceCombos(); Changed(); });
        Row(p, "LLM", _enhanceCombo);
        Sub(p, L.T("Vertex 대화 모델 목록에서 선택 (직접 입력 가능)"));

        Header(p, L.T("입력"));
        Row(p, L.T("전송 키"), Seg(new[] { "Enter", "Ctrl+Enter" }, (int)_s.SendKey, i => { _s.SendKey = (SendKey)i; UpdateKeyHint(); Changed(); }));
        Sub(p, L.T("선택하지 않은 키는 프롬프트 줄바꿈으로 사용"));

        Header(p, L.T("컨텍스트 / 캐시"));
        Row(p, L.T("기록에 포함할 이미지"), Number(_s.HistoryImageCount, 0, 10, v => { _s.HistoryImageCount = v; Changed(); }));
        Row(p, L.T("자동 압축 임계치 (%)"), Number(_s.AutoCompactPercent, 0, 100, v => { _s.AutoCompactPercent = v; Changed(); }));
        Sub(p, L.T("0 = 자동 압축 끔 · 수동은 /compact 또는 상단 압축 버튼"));

        FillModelCombos();
        UpdateRatioLabel();
        _settingsBuilding = false;
    }

    void Changed()
    {
        if (_settingsBuilding) return;
        UpdateRatioLabel();
        UpdateChips();
        UpdateContext();
        SaveSettings();
    }

    void UpdateRatioLabel() => _ratioLabel.Text = _s.Generation.AspectRatio == "auto"
        ? L.F("자동 ({0})", GenerationSettings.AutoAspectDefault) : _s.Generation.AspectRatio;

    void QuickEnhanceSync() { _settingsBuilding = true; QuickEnhance.IsOn = _s.EnhanceEnabled; _settingsBuilding = false; }
    void QuickSearchSync() { _settingsBuilding = true; QuickSearch.IsOn = _s.Generation.GoogleSearch; _settingsBuilding = false; }

    /// <summary>Quick toggles under the composer changed → reflect in the panel.</summary>
    void SyncSettingsPanel()
    {
        _settingsBuilding = true;
        _enhanceSw.IsOn = _s.EnhanceEnabled;
        _searchSw.IsOn = _s.Generation.GoogleSearch;
        _settingsBuilding = false;
    }

    void FillModelCombos()
    {
        if (_modelCombo == null) return;
        var was = _settingsBuilding;
        _settingsBuilding = true;
        _modelCombo.Items.Clear();
        var ids = _imageModels.ToList();
        if (!ids.Contains(_s.Generation.Model)) ids.Insert(0, _s.Generation.Model);
        foreach (var id in ids)
        {
            var name = ModelCatalog.DisplayName(id);
            var item = new ComboBoxItem { Content = ModelCatalog.Label(id), Tag = id };
            ToolTipService.SetToolTip(item, id);
            _modelCombo.Items.Add(item);
            if (id == _s.Generation.Model) _modelCombo.SelectedItem = item;
        }
        // Editable ComboBox.Text is lost when Items are rebuilt (and ignored before load),
        // so restore it from settings and select the matching item when there is one.
        foreach (var (cb, value) in new[] { (_enhanceCombo, _s.EnhanceModel), (_pageEnhanceCombo, _s.EnhanceModel), (_pageCompactCombo, _s.CompactModel ?? "") })
        {
            if (cb == null) continue;
            cb.Items.Clear();
            foreach (var id in _chatModels) cb.Items.Add(id);
            if (value.Length > 0 && !_chatModels.Contains(value)) cb.Items.Insert(0, value);
            if (value.Length > 0) cb.SelectedItem = value;
            cb.Text = value;
        }
        _settingsBuilding = was;
        if (ChipsPanel != null) UpdateChips();   // model dropdown in the top bar
    }

    void SyncEnhanceCombos()
    {
        _settingsBuilding = true;
        if (_enhanceCombo.Text != _s.EnhanceModel) _enhanceCombo.Text = _s.EnhanceModel;
        if (_pageEnhanceCombo != null && _pageEnhanceCombo.Text != _s.EnhanceModel) _pageEnhanceCombo.Text = _s.EnhanceModel;
        _settingsBuilding = false;
    }

    async Task PickOutputDir()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        _s.OutputDirectory = folder.Path;
        _outDirBox.Text = folder.Path;
        SaveSettings();
    }

    // ---------------- control helpers ----------------

    static void Header(Panel p, string text) => p.Children.Add(new TextBlock { Text = text, Style = Res("PanelHeader") });

    static void Sub(Panel p, string text) => p.Children.Add(new TextBlock { Text = text, Style = Res("SubText"), Margin = new Thickness(0, -2, 0, 4) });

    static void Row(Panel p, string label, FrameworkElement control, bool sub = false)
    {
        var g = new Grid { Padding = new Thickness(0, 4, 0, 4), ColumnSpacing = 10, MinHeight = 36 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = label, Style = Res(sub ? "SubText" : "RowLabel"), VerticalAlignment = VerticalAlignment.Center });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        p.Children.Add(g);
    }

    /// <summary>Segmented control made of ToggleButtons (WinUI has no built-in one).</summary>
    FrameworkElement Seg(string[] options, int selected, Action<int> onChange)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var b = new ToggleButton
            {
                Content = options[i], IsChecked = i == selected, FontSize = 12, Padding = new Thickness(9, 3, 9, 4), MinWidth = 0,
                CornerRadius = new CornerRadius(i == 0 ? 4 : 0, i == options.Length - 1 ? 4 : 0, i == options.Length - 1 ? 4 : 0, i == 0 ? 4 : 0),
            };
            b.Click += (_, _) =>
            {
                foreach (var o in buttons) o.IsChecked = o == b;
                onChange(idx);
            };
            buttons.Add(b);
            sp.Children.Add(b);
        }
        return sp;
    }

    ComboBox Combo(string[] items, int selected, Action<int> onChange)
    {
        var cb = new ComboBox { MinWidth = 170, FontSize = 13 };
        foreach (var i in items) cb.Items.Add(i);
        cb.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        cb.SelectionChanged += (_, _) => { if (!_settingsBuilding && cb.SelectedIndex >= 0) onChange(cb.SelectedIndex); };
        return cb;
    }

    ToggleSwitch Toggle(bool on, Action<bool> onChange)
    {
        var t = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, Width = 44, Margin = new Thickness(0, 0, -6, 0) };
        t.Toggled += (_, _) => { if (!_settingsBuilding) onChange(t.IsOn); };
        return t;
    }

    NumberBox Number(int value, int min, int max, Action<int> onChange)
    {
        var n = new NumberBox { Value = value, Minimum = min, Maximum = max, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        n.ValueChanged += (_, e) => { if (!_settingsBuilding && !double.IsNaN(e.NewValue)) onChange((int)e.NewValue); };
        return n;
    }

    ComboBox ModelChooser(string value, Action<string> onChange)
    {
        var cb = new ComboBox { IsEditable = true, MinWidth = 190 };
        foreach (var id in _chatModels) cb.Items.Add(id);
        if (value.Length > 0 && !_chatModels.Contains(value)) cb.Items.Insert(0, value);
        if (value.Length > 0) cb.SelectedItem = value;
        cb.Loaded += (_, _) => { if (string.IsNullOrEmpty(cb.Text)) cb.Text = value; };
        cb.TextSubmitted += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Text)) onChange(e.Text.Trim()); };
        cb.SelectionChanged += (_, _) => { if (!_settingsBuilding && cb.SelectedItem is string s) onChange(s); };
        return cb;
    }

    // =====================================================================
    // Settings page (gear)
    // =====================================================================

    void BuildSettingsPage()
    {
        var p = SettingsPagePanel;
        while (p.Children.Count > 1) p.Children.RemoveAt(1);

        BuildProxyCard(p);

        // LLMs
        var llm = new StackPanel();
        _pageEnhanceCombo = ModelChooser(_s.EnhanceModel, v => { _s.EnhanceModel = v; SyncEnhanceCombos(); SaveSettings(); });
        Row(llm, L.T("프롬프트 인핸스 모델"), _pageEnhanceCombo);
        _pageCompactCombo = ModelChooser(_s.CompactModel ?? "", v => { _s.CompactModel = v; SaveSettings(); });
        _pageCompactCombo.PlaceholderText = L.T("(인핸스 모델과 같음)");
        Row(llm, L.T("/compact 요약 모델"), _pageCompactCombo);
        var sys = new TextBox { Text = _s.SystemPrompt ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, PlaceholderText = L.T("모든 이미지 요청 앞에 붙는 지시문 (선택)") };
        sys.LostFocus += (_, _) => { _s.SystemPrompt = string.IsNullOrWhiteSpace(sys.Text) ? null : sys.Text; SaveSettings(); };
        llm.Children.Add(new TextBlock { Text = L.T("기본 지시문 (system)"), Style = Res("RowLabel"), Margin = new Thickness(0, 6, 0, 4) });
        llm.Children.Add(sys);
        Card(p, "LLM", llm);

        // Context
        var ctx = new StackPanel();
        Row(ctx, L.T("컨텍스트 한도 (토큰)"), Number(_s.ContextTokenLimit, 8192, 1048576, v => { _s.ContextTokenLimit = v; UpdateContext(); SaveSettings(); }));
        ctx.Children.Add(new TextBlock { Text = L.T("상단 컨텍스트 막대와 자동 압축 기준에 사용됩니다. 이미지 1장 ≈ 1,100–2,000 토큰으로 추정합니다."), Style = Res("SubText") });
        Card(p, L.T("컨텍스트"), ctx);

        // Updates
        var upd = new StackPanel();
        var updStatus = new TextBlock { Style = Res("SubText"), Text = _updater.IsInstalled ? "" : L.T("개발 빌드 (설치 파일로 설치하면 자동 업데이트 사용 가능)") };
        Row(upd, L.T("현재 버전"), new TextBlock { Text = _updater.CurrentVersion, Style = Res("RowLabel") });
        Row(upd, L.T("시작할 때 업데이트 확인"), Toggle(_s.AutoCheckUpdates, v => { _s.AutoCheckUpdates = v; SaveSettings(); }));
        var checkBtn = new Button { Content = L.T("지금 확인") };
        checkBtn.Click += async (_, _) => { checkBtn.IsEnabled = false; updStatus.Text = L.T("확인 중…"); updStatus.Text = await CheckForUpdateAsync(silent: false); checkBtn.IsEnabled = true; };
        Row(upd, L.T("GitHub 릴리즈에서 새 버전 확인"), checkBtn);
        upd.Children.Add(updStatus);
        Card(p, L.T("업데이트"), upd);

        // App
        var app = new StackPanel();
        var langCodes = new[] { "system", "ko", "en" };
        Row(app, "언어 / Language", Combo(new[] { L.T("시스템 언어 따르기"), "한국어", "English" }, Math.Max(0, Array.IndexOf(langCodes, _s.Language)), i =>
        {
            _s.Language = langCodes[i];
            SaveSettings();
            var restart = new Button { Content = L.T("지금 다시 시작") };
            restart.Click += (_, _) => RestartApp();
            ShowBanner(L.T("언어 설정이 바뀌었습니다"), L.T("앱을 다시 시작하면 적용됩니다."), InfoBarSeverity.Informational);
            Banner.ActionButton = restart;
        }));
        Row(app, L.T("테마"), Combo(new[] { L.T("시스템"), L.T("라이트"), L.T("다크") }, Array.IndexOf(new[] { "system", "light", "dark" }, _s.Theme),
            i => { _s.Theme = new[] { "system", "light", "dark" }[i]; ApplyTheme(); SaveSettings(); }));
        var purge = new Button { Content = L.T("휴지통 비우기") };
        purge.Click += async (_, _) =>
        {
            if (await Confirm(L.T("휴지통 비우기"), L.T("휴지통의 모든 대화와 이미지를 영구 삭제합니다. 되돌릴 수 없습니다."), L.T("비우기")))
            {
                _store.PurgeTrash(all: true);
                RefreshConversationList();
            }
        };
        Row(app, L.T("휴지통 (30일 후 자동 삭제)"), purge);
        var openData = new Button { Content = L.T("데이터 폴더 열기") };
        openData.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", _paths.Root); } catch { } };
        Row(app, L.T("대화 DB · 원본 이미지"), openData);
        Card(p, L.T("앱"), app);

        // About / developer
        var about = new StackPanel { Spacing = 2 };
        Row(about, L.T("버전"), new TextBlock { Text = "V." + _updater.CurrentVersion, Style = Res("RowLabel") });
        Row(about, L.T("개발자"), new TextBlock { Text = "TJ.CHOI", Style = Res("RowLabel") });
        Row(about, "GitHub", new HyperlinkButton { Content = "github.com/designloves2", NavigateUri = new Uri("https://github.com/designloves2"), Padding = new Thickness(0) });
        Row(about, L.T("이메일"), new HyperlinkButton { Content = "tj.choi.79@gmail.com", NavigateUri = new Uri("mailto:tj.choi.79@gmail.com"), Padding = new Thickness(0) });
        Row(about, L.T("저장소"), new HyperlinkButton { Content = "github.com/designloves2/Vertex-AI-Studio", NavigateUri = new Uri(AppUpdater.RepoUrl), Padding = new Thickness(0) });
        Row(about, L.T("라이선스"), new HyperlinkButton { Content = "GPL-3.0", NavigateUri = new Uri(AppUpdater.RepoUrl + "/blob/main/LICENSE"), Padding = new Thickness(0) });
        about.Children.Add(new TextBlock
        {
            Style = Res("SubText"), Margin = new Thickness(0, 6, 0, 0),
            Text = L.T("내장 프록시: Vertex OpenAI Proxy (MIT 라이선스) · 설치·업데이트: Velopack (MIT)"),
        });
        Card(p, L.T("정보"), about);
    }

    static void Card(Panel p, string title, UIElement content)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(content);
        p.Children.Add(new Border { Style = Res("CardStyle"), Padding = new Thickness(20, 14, 20, 14), Child = sp, Margin = new Thickness(0, 6, 0, 0) });
    }

    void ApplyProxyUrl(string url, bool force = false)
    {
        url = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        if (!force && url == _s.ProxyUrl) return;
        _s.ProxyUrl = url;
        RecreateClient();
    }

    /// <summary>New HttpClient for the current proxy URL / timeout (embedded or external).</summary>
    void RecreateClient()
    {
        var old = _client;
        _client = new ProxyClient(_s.EffectiveProxyUrl, _s.TimeoutSeconds);
        if (_cts == null) old.Dispose();
        SaveSettings();
    }
}
