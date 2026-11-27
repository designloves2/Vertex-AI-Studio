using System.Diagnostics;
using ImageStudio.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace ImageStudio.App;

public sealed partial class MainWindow
{
    readonly List<(byte[] Bytes, string Mime)> _attachments = new();
    ImageRef? _continueFrom;
    CancellationTokenSource? _cts;
    readonly Dictionary<string, bool> _showOriginal = new();
    List<ImageRef> _previewList = new();
    int _previewIdx;

    static Style Res(string key) => (Style)Application.Current.Resources[key];
    static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    // =====================================================================
    // Rendering
    // =====================================================================

    void RenderMessages()
    {
        LayoutColumn();
        MessagesPanel.Children.Clear();
        foreach (var m in _msgs) MessagesPanel.Children.Add(RenderMessage(m));
        EmptyState.Visibility = _msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateContext();
        ScrollToEnd();
    }

    void ReplaceMessage(Message m)
    {
        var i = _msgs.IndexOf(m);
        if (i >= 0 && i < MessagesPanel.Children.Count) MessagesPanel.Children[i] = RenderMessage(m);
        else RenderMessages();
    }

    /// <summary>Usable width of the message column (viewport minus the ScrollViewer padding).</summary>
    double ChatWidth()
    {
        // 16 = room for the vertical scrollbar so the column never sits under it.
        var w = MessagesScroll.ActualWidth - MessagesScroll.Padding.Left - MessagesScroll.Padding.Right - 16;
        return w > 100 ? Math.Min(w, MessagesPanel.MaxWidth) : 640;
    }

    /// <summary>
    /// Message column: fixed width (≤ 1100), placed in the middle of the scroll area with an explicit left margin.
    /// Alignment-based centring drifted right when maximised, so the offset is computed.
    /// </summary>
    void LayoutColumn()
    {
        var avail = MessagesScroll.ActualWidth - MessagesScroll.Padding.Left - MessagesScroll.Padding.Right - 16;
        if (avail <= 100) return;
        var w = Math.Min(avail, MessagesPanel.MaxWidth);
        MessagesPanel.Width = w;
        MessagesPanel.Margin = new Thickness(Math.Max(0, (avail - w) / 2), 0, 0, 0);
    }

    double BubbleMaxWidth() => Math.Min(640, ChatWidth() * 0.85);

    /// <summary>Keeps bubbles inside the column when the window is resized (maximize, monitor change).</summary>
    void MessagesScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1) return;
        LayoutColumn();
        foreach (var child in MessagesPanel.Children)
        {
            if (child is Border { Tag: "bubble" } b) b.MaxWidth = BubbleMaxWidth();
            else if (child is StackPanel { Tag: "assistant" } a) a.MaxWidth = Math.Min(980, ChatWidth());
        }
    }

    void ScrollToEnd() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, true));

    UIElement RenderMessage(Message m) => m.Role switch
    {
        Roles.User => RenderUser(m),
        Roles.Summary => RenderSummary(m),
        _ => RenderAssistant(m),
    };

    UIElement RenderUser(Message m)
    {
        var stack = new StackPanel { Spacing = 6 };
        bool original = _showOriginal.GetValueOrDefault(m.Id, true);
        stack.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });

        if (m.Images.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var img in m.Images) row.Children.Add(Thumb(img, 56, m.Images));
            stack.Children.Add(row);
        }
        if (m.ContinueFromImageId != null && FindImage(m.ContinueFromImageId) is ImageRef cont)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Opacity = 0.85 };
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) });
            row.Children.Add(Thumb(cont, 28, new List<ImageRef> { cont }));
            row.Children.Add(new TextBlock { Text = "이 이미지로 계속", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(row);
        }
        if (!string.IsNullOrWhiteSpace(m.Enhanced))
        {
            var sep = new Border { Height = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(64, 255, 255, 255)), Margin = new Thickness(0, 2, 0, 2) };
            stack.Children.Add(sep);
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.9, IsTextSelectionEnabled = true, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
            t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"✨ 강화됨 ({_s.EnhanceModel}): ", FontWeight = FontWeights.SemiBold });
            var full = m.Enhanced!;
            t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = original && full.Length > 160 ? full[..160] + "…" : full });
            stack.Children.Add(t);
            var toggle = new HyperlinkButton
            {
                Content = original ? "전체 보기" : "접기", Padding = new Thickness(0), FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            };
            toggle.Click += (_, _) => { _showOriginal[m.Id] = !original; ReplaceMessage(m); };
            stack.Children.Add(toggle);
        }

        var bubble = new Border
        {
            Background = ThemeBrush("UserBubbleBrush"), CornerRadius = new CornerRadius(14, 14, 4, 14),
            Padding = new Thickness(14, 10, 14, 10), Child = stack, HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = BubbleMaxWidth(), Tag = "bubble",
        };
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("프롬프트 복사", "", () => SetClipboardText(m.Text)));
        if (!string.IsNullOrWhiteSpace(m.Enhanced)) menu.Items.Add(MenuItem("강화된 프롬프트 복사", "", () => SetClipboardText(m.Enhanced!)));
        menu.Items.Add(MenuItem("입력창으로 가져오기", "", () => { PromptBox.Text = m.Text; PromptBox.Focus(FocusState.Programmatic); }));
        bubble.ContextFlyout = menu;
        return bubble;
    }

    UIElement RenderSummary(Message m)
    {
        var stack = new StackPanel { Spacing = 6 };
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        head.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "대화 압축됨", FontWeight = FontWeights.SemiBold, Foreground = ThemeBrush("WarnText") });
        head.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " — " + (m.SettingsLabel ?? "") + " · 이전 내용은 화면에 남아 있지만 요청에는 요약만 보냅니다" });
        stack.Children.Add(head);
        var exp = new Expander
        {
            Header = "요약 보기", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
        };
        stack.Children.Add(exp);
        if (m.Images.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var img in m.Images) row.Children.Add(Thumb(img, 48, m.Images));
            stack.Children.Add(row);
        }
        return new Border
        {
            Style = Res("CardStyle"), Padding = new Thickness(14, 10, 14, 10), HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 720, Child = stack, BorderBrush = ThemeBrush("WarnText"), BorderThickness = new Thickness(1),
        };
    }

    UIElement RenderAssistant(Message m)
    {
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = Math.Min(980, ChatWidth()), Tag = "assistant" };
        var meta = $"{ModelCatalog.DisplayName(m.Model ?? "")}";
        if (m.Status == MessageStatus.Pending) meta += " · 생성 중…";
        else if (m.DurationMs is long ms) meta += ms == 0 ? " · 캐시된 결과" : $" · {ms / 1000.0:0.0}s";
        meta += $" · 사고 수준 {ThinkingLabel(_s.Generation.Thinking)}";
        stack.Children.Add(new TextBlock { Text = meta, Style = Res("SubText") });

        if (m.Status == MessageStatus.Pending)
        {
            var ph = new Grid
            {
                Width = 220, Height = 300, CornerRadius = new CornerRadius(10),
                Background = ThemeBrush("CardBackgroundFillColorSecondaryBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            };
            var inner = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            inner.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28 });
            inner.Children.Add(new TextBlock { Text = BusyText.Text is { Length: > 0 } b ? b : "생성 중…", Style = Res("SubText"), HorizontalAlignment = HorizontalAlignment.Center });
            var cancel = new Button { Content = "■ 취소", HorizontalAlignment = HorizontalAlignment.Center };
            cancel.Click += Cancel_Click;
            inner.Children.Add(cancel);
            ph.Children.Add(inner);
            stack.Children.Add(ph);
            if (!string.IsNullOrEmpty(m.Text)) stack.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap });
            return stack;
        }

        if (m.Status is MessageStatus.Error or MessageStatus.Cancelled)
        {
            var bar = new InfoBar
            {
                IsOpen = true, IsClosable = false,
                Severity = m.Status == MessageStatus.Error ? InfoBarSeverity.Error : InfoBarSeverity.Informational,
                Title = m.Status == MessageStatus.Error ? "생성 실패" : "취소됨",
                Message = m.Error ?? "",
            };
            var retry = new Button { Content = "↻ 다시 시도" };
            retry.Click += async (_, _) => await RetryAsync(m);
            bar.ActionButton = retry;
            stack.Children.Add(bar);
            return stack;
        }

        if (!string.IsNullOrWhiteSpace(m.Thoughts))
            stack.Children.Add(new Expander
            {
                Header = "생각 보기", HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 720,
                Content = new TextBlock { Text = m.Thoughts, TextWrapping = TextWrapping.Wrap, Style = Res("SubText"), IsTextSelectionEnabled = true },
            });

        if (!string.IsNullOrWhiteSpace(m.Notes))
            stack.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Warning, MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Left,
                Title = "이 모델에 적용되지 않은 옵션", Message = m.Notes,
            });

        foreach (var img in m.Images)
        {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(ResultImage(img, m));
            block.Children.Add(ImageActions(img, m));
            stack.Children.Add(block);
        }
        if (!string.IsNullOrWhiteSpace(m.Text))
            stack.Children.Add(new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left });
        return stack;
    }

    static string ThinkingLabel(ThinkingLevel t) => t switch { ThinkingLevel.Low => "낮음", ThinkingLevel.High => "높음", _ => "중간" };

    UIElement ResultImage(ImageRef img, Message m)
    {
        double h = 320, w = img.Width > 0 && img.Height > 0 ? h * img.Width / img.Height : 320;
        var maxW = Math.Min(640, ChatWidth());
        if (w > maxW) { h = h * maxW / w; w = maxW; }
        var grid = new Grid { Width = w, Height = h, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(new Image { Source = LoadBitmap(img.Path, (int)(h * 2)), Stretch = Stretch.UniformToFill });
        var label = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = $"{m.SettingsLabel} · {img.Width}×{img.Height}", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
        grid.Children.Add(label);
        grid.Tapped += (_, _) => OpenPreview(AllImages(), img);
        return grid;
    }

    StackPanel ImageActions(ImageRef img, Message m)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(ActionButton("", "이걸로 계속", () => SetContinue(img)));
        row.Children.Add(ActionButton("", "저장", async () => await SaveAs(img)));
        row.Children.Add(ActionButton("", "복사", async () => await CopyImage(img)));
        row.Children.Add(ActionButton("", "폴더", () => RevealInExplorer(img.ExportPath ?? img.Path)));
        row.Children.Add(ActionButton("", "다시 생성", async () => await RegenerateAsync(m)));
        return row;
    }

    static Button ActionButton(string glyph, string text, Action click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var b = new Button { Content = sp, Padding = new Thickness(9, 4, 9, 5) };
        b.Click += (_, _) => click();
        return b;
    }

    FrameworkElement Thumb(ImageRef img, double size, List<ImageRef> set)
    {
        var g = new Grid { Width = size, Height = size, CornerRadius = new CornerRadius(6) };
        g.Children.Add(new Image { Source = LoadBitmap(img.Path, (int)size * 2), Stretch = Stretch.UniformToFill });
        g.Tapped += (_, e) => { e.Handled = true; OpenPreview(set, img); };
        return g;
    }

    static BitmapImage LoadBitmap(string path, int decodeHeight)
    {
        var bmp = new BitmapImage { DecodePixelHeight = decodeHeight, DecodePixelType = DecodePixelType.Logical };
        try { bmp.UriSource = new Uri(path); } catch { }
        return bmp;
    }

    ImageRef? FindImage(string id) => _msgs.SelectMany(m => m.Images).FirstOrDefault(i => i.Id == id);

    List<ImageRef> AllImages() => _msgs.Where(m => m.Role == Roles.Assistant).SelectMany(m => m.Images).ToList();

    // =====================================================================
    // Context meter + chips
    // =====================================================================

    void UpdateContext()
    {
        var tokens = StudioService.EstimateTokens(_msgs, _s.HistoryImageCount);
        var pct = Math.Min(100, tokens * 100.0 / Math.Max(1, _s.ContextTokenLimit));
        ContextBar.Value = pct;
        ContextText.Text = $"컨텍스트 {pct:0}% · ~{tokens / 1000.0:0.#}K 토큰";
        CompactButton.IsEnabled = HistoryBuilder.ActiveTurns(_msgs).Turns.Count >= 2 && _cts == null;
    }

    /// <summary>
    /// Top bar: model / ratio / resolution / format / enhance as dropdowns, so they can be changed with the
    /// settings panel folded. Every change goes through the same settings object and refreshes the panel,
    /// and panel changes call UpdateChips — both stay in sync.
    /// </summary>
    void UpdateChips()
    {
        ChipsPanel.Children.Clear();
        var g = _s.Generation;

        var models = _imageModels.ToList();
        if (!models.Contains(g.Model)) models.Insert(0, g.Model);
        ChipMenu("모델", ModelCatalog.DisplayName(g.Model),
            models.Select(m => (ModelCatalog.DisplayName(m) == m ? m : $"{ModelCatalog.DisplayName(m)}  ({m})", m == g.Model, (Action)(() => g.Model = m))));

        ChipMenu("비율", g.AspectRatio == "auto" ? $"자동 ({GenerationSettings.AutoAspectDefault})" : g.AspectRatio,
            GenerationSettings.AspectRatios.Select(r => (r == "auto" ? $"자동 ({GenerationSettings.AutoAspectDefault})" : r, r == g.AspectRatio, (Action)(() => g.AspectRatio = r))));

        ChipMenu("해상도", SizeLabel(g.Size),
            new[] { ImageSize.K1, ImageSize.K2, ImageSize.K4 }.Select(z => (SizeLabel(z), z == g.Size, (Action)(() => g.Size = z))));

        ChipMenu("형식", g.Format == OutputFormat.Jpeg ? "JPEG" : "PNG",
            new[] { (OutputFormat.Png, "PNG"), (OutputFormat.Jpeg, "JPEG") }.Select(f => (f.Item2, f.Item1 == g.Format, (Action)(() => g.Format = f.Item1))));

        ChipMenu("✨ 인핸스", _s.EnhanceEnabled ? "ON" : "OFF",
            new[] { (true, "켜기"), (false, "끄기") }.Select(o => (o.Item2, o.Item1 == _s.EnhanceEnabled, (Action)(() => _s.EnhanceEnabled = o.Item1))));

        if (g.GoogleSearch) ChipStatic("🔍 검색", "ON");
    }

    static string SizeLabel(ImageSize z) => z switch { ImageSize.K1 => "1K", ImageSize.K4 => "4K", _ => "2K" };

    void ChipMenu(string label, string value, IEnumerable<(string Text, bool Checked, Action Apply)> items)
    {
        var t = new TextBlock { FontSize = 12 };
        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + " ", Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value, FontWeight = FontWeights.SemiBold });
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var (text, isChecked, apply) in items)
        {
            var mi = new RadioMenuFlyoutItem { Text = text, IsChecked = isChecked, GroupName = label };
            mi.Click += (_, _) => { apply(); OnChipChanged(); };
            menu.Items.Add(mi);
        }
        ChipsPanel.Children.Add(new DropDownButton
        {
            Content = t, Flyout = menu, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 3, 8, 4), MinHeight = 0,
        });
    }

    void ChipStatic(string label, string value)
    {
        var t = new TextBlock { FontSize = 12 };
        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + " ", Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value, FontWeight = FontWeights.SemiBold });
        ChipsPanel.Children.Add(new Border { Style = Res("ChipBorder"), Child = t, VerticalAlignment = VerticalAlignment.Center });
    }

    /// <summary>A top-bar change: save, rebuild the settings panel (keeping its scroll) and the chips.</summary>
    void OnChipChanged()
    {
        SaveSettings();
        var offset = SetExpanded.VerticalOffset;
        BuildSettingsPanel();
        SetExpanded.UpdateLayout();
        SetExpanded.ChangeView(null, offset, null, true);
        _settingsBuilding = true;
        QuickEnhance.IsOn = _s.EnhanceEnabled;
        _settingsBuilding = false;
        UpdateChips();
        UpdateContext();
    }

    void UpdateKeyHint() => KeyHint.Text = _s.SendKey == SendKey.Enter
        ? "Enter 전송 · Ctrl+Enter 줄바꿈"
        : "Ctrl+Enter 전송 · Enter 줄바꿈";

    void QuickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingsBuilding) return;
        _s.EnhanceEnabled = QuickEnhance.IsOn;
        _s.Generation.GoogleSearch = QuickSearch.IsOn;
        SyncSettingsPanel();
        UpdateChips();
        SaveSettings();
    }

    // =====================================================================
    // Input
    // =====================================================================

    static bool IsDown(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        bool ctrl = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift);
        bool send = _s.SendKey == SendKey.Enter ? (!ctrl && !shift) : ctrl;
        if (send)
        {
            e.Handled = true;
            _ = SendAsync();
        }
        else if (ctrl)
        {
            // Ctrl+Enter as newline (TextBox ignores it by default).
            e.Handled = true;
            var pos = PromptBox.SelectionStart;
            PromptBox.Text = PromptBox.Text.Remove(pos, PromptBox.SelectionLength).Insert(pos, "\r");
            PromptBox.SelectionStart = pos + 1;
        }
    }

    async void PromptBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            await AddStorageItems(await content.GetStorageItemsAsync());
        }
        else if (content.Contains(StandardDataFormats.Bitmap))
        {
            e.Handled = true;
            var r = await content.GetBitmapAsync();
            using var s = await r.OpenReadAsync();
            var png = await ImageUtil.ReencodeAsync(s, Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId);
            AddAttachment(png, "image/png");
        }
    }

    void Chat_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "참조 이미지로 첨부";
        }
    }

    async void Chat_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            await AddStorageItems(await e.DataView.GetStorageItemsAsync());
    }

    async Task AddStorageItems(IReadOnlyList<IStorageItem> items)
    {
        foreach (var it in items.OfType<StorageFile>())
        {
            if (!ImageUtil.ImageExtensions.Contains(Path.GetExtension(it.Path).ToLowerInvariant())) continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(it.Path);
                var (b, mime) = await ImageUtil.NormalizeAsync(bytes);
                AddAttachment(b, mime);
            }
            catch (Exception ex) { ShowBanner("첨부 실패", ex.Message, InfoBarSeverity.Error); }
        }
    }

    async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var ext in ImageUtil.ImageExtensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files != null) await AddStorageItems(files.Cast<IStorageItem>().ToList());
    }

    void AddAttachment(byte[] bytes, string mime)
    {
        if (ModelCatalog.IsImagen(_s.Generation.Model))
        {
            ShowBanner("첨부 불가", "Imagen은 단발 생성 모델이라 참조 이미지를 쓸 수 없습니다.", InfoBarSeverity.Informational);
            return;
        }
        _attachments.Add((bytes, mime));
        RenderAttachments();
    }

    void RenderAttachments()
    {
        AttachPanel.Children.Clear();
        for (int i = 0; i < _attachments.Count; i++)
        {
            int idx = i;
            var g = new Grid { Width = 52, Height = 52 };
            var img = new Image { Stretch = Stretch.UniformToFill };
            var bmp = new BitmapImage { DecodePixelHeight = 104 };
            var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            _ = LoadAttachmentAsync(ms, bmp, _attachments[i].Bytes);
            img.Source = bmp;
            g.Children.Add(new Border { CornerRadius = new CornerRadius(6), Child = img, Margin = new Thickness(0, 4, 4, 0) });
            var x = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 8 }, Width = 18, Height = 18, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            };
            x.Click += (_, _) => { _attachments.RemoveAt(idx); RenderAttachments(); };
            g.Children.Add(x);
            AttachPanel.Children.Add(g);
        }
        AttachPanel.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static async Task LoadAttachmentAsync(Windows.Storage.Streams.InMemoryRandomAccessStream ms, BitmapImage bmp, byte[] bytes)
    {
        await ms.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        ms.Seek(0);
        await bmp.SetSourceAsync(ms);
    }

    void SetContinue(ImageRef img)
    {
        _continueFrom = img;
        ContinueThumb.Source = LoadBitmap(img.Path, 56);
        ContinueBar.Visibility = Visibility.Visible;
        PromptBox.Focus(FocusState.Programmatic);
    }

    void ClearContinue()
    {
        _continueFrom = null;
        ContinueBar.Visibility = Visibility.Collapsed;
    }

    void ClearContinue_Click(object sender, RoutedEventArgs e) => ClearContinue();

    // =====================================================================
    // Send / generate / compact
    // =====================================================================

    void SetBusy(string? text)
    {
        BusyText.Text = text ?? "";
        BusyOverlay.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        PromptBox.IsReadOnly = text != null;
        AttachButton.IsEnabled = text == null;
        UpdateContext();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    async Task SendAsync()
    {
        if (_cts != null) return;
        var text = PromptBox.Text.Trim();
        if (text.Equals(StudioService.CompactCommand, StringComparison.OrdinalIgnoreCase))
        {
            PromptBox.Text = "";
            await CompactAsync();
            return;
        }
        if (text.Length == 0 && _attachments.Count == 0) return;

        if (_conv == null)
        {
            _conv = new Conversation { Model = _s.Generation.Model };
            _msgs = new();
        }
        var req = new StudioService.SendRequest(_conv, text, _attachments.ToList(), _continueFrom?.Id);
        var (user, asst) = _svc.BeginTurn(req, _msgs);
        PromptBox.Text = "";
        _attachments.Clear();
        RenderAttachments();
        ClearContinue();
        _s.LastConversationId = _conv.Id;
        RefreshConversationList();
        await RunTurnAsync(user, asst, bypassCache: false);
    }

    async Task RunTurnAsync(Message user, Message asst, bool bypassCache)
    {
        if (_conv == null) return;
        _cts = new CancellationTokenSource();
        SetBusy("요청 준비 중…");
        RenderMessages();
        try
        {
            await _svc.CompleteTurnAsync(_conv, _msgs, user, asst, bypassCache,
                onPhase: p => DispatcherQueue.TryEnqueue(() => { SetBusy(p); ReplaceMessage(asst); }),
                ct: _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ProxyException ex) when (ex.ProxyDown) { await CheckProxyAsync(false); }
        catch (Exception) { /* shown inline on the message */ }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(null);
            RenderMessages();
            RefreshConversationList();
            SaveSettings();
        }
        await MaybeAutoCompactAsync();
    }

    async Task RetryAsync(Message failed)
    {
        if (_cts != null || _conv == null) return;
        var i = _msgs.IndexOf(failed);
        var user = i > 0 ? _msgs[i - 1] : null;
        if (user?.Role != Roles.User) return;
        failed.Status = MessageStatus.Pending;
        failed.Error = null;
        failed.Model = _s.Generation.Model;
        failed.SettingsLabel = ImageRequest.Label(_s.Generation);
        await RunTurnAsync(user, failed, bypassCache: true);
    }

    async Task RegenerateAsync(Message result)
    {
        if (_cts != null || _conv == null) return;
        var i = _msgs.IndexOf(result);
        var user = i > 0 ? _msgs[i - 1] : null;
        if (user?.Role != Roles.User) return;
        var attachments = user.Images.Select(img => (File.ReadAllBytes(img.Path), img.Mime)).ToList();
        var req = new StudioService.SendRequest(_conv, user.Text, attachments, user.ContinueFromImageId, BypassCache: true);
        var (nu, na) = _svc.BeginTurn(req, _msgs);
        nu.Enhanced = user.Enhanced;   // same request → reuse the enhanced prompt
        _store.SaveMessage(nu);
        await RunTurnAsync(nu, na, bypassCache: true);
    }

    async void Compact_Click(object sender, RoutedEventArgs e) => await CompactAsync();

    async Task CompactAsync(bool auto = false)
    {
        if (_cts != null || _conv == null) return;
        if (HistoryBuilder.ActiveTurns(_msgs).Turns.Count < 2)
        {
            ShowBanner("압축할 내용이 없습니다", "대화가 2턴 이상일 때 압축할 수 있습니다.", InfoBarSeverity.Informational);
            return;
        }
        _cts = new CancellationTokenSource();
        SetBusy(auto ? "컨텍스트가 커져 자동 압축 중…" : "대화 압축 중…");
        try
        {
            await _svc.CompactAsync(_conv, _msgs, keepImages: 2, ct: _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowBanner("압축 실패", ex is ProxyException pe ? pe.UserMessage : ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(null);
            RenderMessages();
        }
    }

    async Task MaybeAutoCompactAsync()
    {
        if (_s.AutoCompactPercent <= 0 || _conv == null) return;
        var pct = StudioService.EstimateTokens(_msgs, _s.HistoryImageCount) * 100 / Math.Max(1, _s.ContextTokenLimit);
        if (pct >= _s.AutoCompactPercent) await CompactAsync(auto: true);
    }

    // =====================================================================
    // Image actions
    // =====================================================================

    async Task SaveAs(ImageRef img)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(img.ExportPath ?? img.Sha1) };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG", new List<string> { ".jpg" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        var bytes = await File.ReadAllBytesAsync(img.Path);
        var wantJpeg = file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase);
        var srcJpeg = img.Mime == "image/jpeg";
        if (wantJpeg != srcJpeg) bytes = ImageUtil.Convert(bytes, wantJpeg ? OutputFormat.Jpeg : OutputFormat.Png) ?? bytes;
        await File.WriteAllBytesAsync(file.Path, bytes);
    }

    async Task CopyImage(ImageRef img)
    {
        var file = await StorageFile.GetFileFromPathAsync(img.Path);
        var dp = new DataPackage();
        dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
        dp.SetStorageItems(new[] { file });
        Clipboard.SetContent(dp);
    }

    static void SetClipboardText(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // =====================================================================
    // Preview overlay
    // =====================================================================

    void OpenPreview(List<ImageRef> set, ImageRef img)
    {
        _previewList = set.Count > 0 ? set : new List<ImageRef> { img };
        _previewIdx = Math.Max(0, _previewList.FindIndex(i => i.Id == img.Id));
        HookPreviewMouse();
        PreviewOverlay.Visibility = Visibility.Visible;
        ShowPreview();
    }

    void ShowPreview()
    {
        var img = _previewList[_previewIdx];
        PreviewImage.Source = new BitmapImage(new Uri(img.Path));
        PreviewInfo.Text = $"{_previewIdx + 1} / {_previewList.Count} · {img.Width}×{img.Height} · {img.Mime} · 휠: 확대/축소 · 드래그: 이동 · 더블클릭: 화면 맞춤";
    }

    // ---- preview mouse: wheel = zoom at cursor, left-drag = pan, double-click = fit ----
    bool _previewMouseHooked;
    bool _panning;
    Windows.Foundation.Point _panStart;
    double _panH, _panV;

    void HookPreviewMouse()
    {
        if (_previewMouseHooked) return;
        _previewMouseHooked = true;
        // handledEventsToo: ScrollViewer consumes the wheel itself (it would scroll, not zoom).
        PreviewScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(PreviewScroll_Wheel), true);
        PreviewScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PreviewScroll_Pressed), true);
        PreviewScroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(PreviewScroll_Moved), true);
        PreviewScroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PreviewScroll_Released), true);
        PreviewScroll.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _panning = false), true);
    }

    /// <summary>Zoom so the long edge of the image fits the viewport, centred.</summary>
    void FitPreview(bool animate = false)
    {
        if (PreviewImage.Width is double.NaN or <= 0 || PreviewScroll.ActualWidth <= 0) return;
        var fit = (float)Math.Min(PreviewScroll.ActualWidth / PreviewImage.Width, PreviewScroll.ActualHeight / PreviewImage.Height);
        fit = Math.Clamp(fit, PreviewScroll.MinZoomFactor, PreviewScroll.MaxZoomFactor);
        PreviewScroll.ChangeView(0, 0, fit, !animate);
    }

    void PreviewImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0) return;
        // Lay the image out at its pixel size (in DIPs) and let the ScrollViewer zoom do the scaling.
        var scale = Content.XamlRoot?.RasterizationScale ?? 1;
        PreviewImage.Width = bmp.PixelWidth / scale;
        PreviewImage.Height = bmp.PixelHeight / scale;
        PreviewScroll.UpdateLayout();
        FitPreview();
    }

    void PreviewScroll_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        FitPreview(animate: true);
    }

    void PreviewScroll_Wheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var pt = e.GetCurrentPoint(PreviewScroll);
        var delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;
        var oldZoom = PreviewScroll.ZoomFactor;
        var newZoom = Math.Clamp(oldZoom * (float)Math.Pow(1.2, delta / 120.0), PreviewScroll.MinZoomFactor, PreviewScroll.MaxZoomFactor);
        // Keep the image point under the cursor where it is.
        var cx = (PreviewScroll.HorizontalOffset + pt.Position.X) / oldZoom;
        var cy = (PreviewScroll.VerticalOffset + pt.Position.Y) / oldZoom;
        PreviewScroll.ChangeView(Math.Max(0, cx * newZoom - pt.Position.X), Math.Max(0, cy * newZoom - pt.Position.Y), newZoom, true);
    }

    void PreviewScroll_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PreviewScroll);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _panning = true;
        _panStart = pt.Position;
        _panH = PreviewScroll.HorizontalOffset;
        _panV = PreviewScroll.VerticalOffset;
        PreviewScroll.CapturePointer(e.Pointer);
    }

    void PreviewScroll_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetCurrentPoint(PreviewScroll).Position;
        PreviewScroll.ChangeView(_panH - (p.X - _panStart.X), _panV - (p.Y - _panStart.Y), null, true);
        e.Handled = true;
    }

    void PreviewScroll_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        PreviewScroll.ReleasePointerCapture(e.Pointer);
    }

    void PreviewPrev_Click(object sender, RoutedEventArgs e) { if (_previewIdx > 0) { _previewIdx--; ShowPreview(); } }
    void PreviewNext_Click(object sender, RoutedEventArgs e) { if (_previewIdx < _previewList.Count - 1) { _previewIdx++; ShowPreview(); } }
    void PreviewClose_Click(object sender, RoutedEventArgs e) => PreviewOverlay.Visibility = Visibility.Collapsed;
    void PreviewBackground_Tapped(object sender, TappedRoutedEventArgs e) => PreviewOverlay.Visibility = Visibility.Collapsed;
    void PreviewImage_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
}
