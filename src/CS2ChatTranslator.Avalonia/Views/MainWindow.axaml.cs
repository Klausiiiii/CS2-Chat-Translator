using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Views;

public partial class MainWindow : Window
{
    private const int MaxMessages = 500;
    private const int SeedMaxMessages = 25;
    private const int SeedStaggerDelayMs = 200;

    private static readonly IBrush BrushAll      = new SolidColorBrush(Color.FromRgb(211, 211, 211));
    private static readonly IBrush BrushCT       = new SolidColorBrush(Color.FromRgb(100, 149, 237));
    private static readonly IBrush BrushT        = new SolidColorBrush(Color.FromRgb(255, 215, 0));
    private static readonly IBrush BrushPlayer   = new SolidColorBrush(Colors.White);
    private static readonly IBrush BrushDead     = new SolidColorBrush(Color.FromRgb(205, 92, 92));
    private static readonly IBrush BrushCallout  = new SolidColorBrush(Color.FromRgb(147, 112, 219));
    private static readonly IBrush BrushOriginal = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private static readonly IBrush BrushArrow    = new SolidColorBrush(Color.FromRgb(105, 105, 105));
    private static readonly IBrush BrushPending  = new SolidColorBrush(Color.FromRgb(105, 105, 105));
    private static readonly IBrush BrushFailed   = new SolidColorBrush(Color.FromRgb(205, 92, 92));
    private static readonly IBrush BrushOk       = new SolidColorBrush(Color.FromRgb(152, 251, 152));

    private static readonly IBrush BrushHint     = new SolidColorBrush(Color.FromRgb(108, 112, 121));
    private static readonly IBrush BrushHintErr  = new SolidColorBrush(Color.FromRgb(205, 92, 92));
    private static readonly IBrush BrushHintOk   = new SolidColorBrush(Color.FromRgb(152, 251, 152));

    private AppConfig _config = new();
    private ConsoleLogTailer? _tailer;
    private readonly TranslationService _translator = new();
    private readonly List<ChatMessage> _messages = new();
    private readonly Dictionary<Guid, MessageInlines> _inlines = new();
    private int _totalChars;
    private int _chatFound;
    private int _translated;
    private readonly DispatcherTimer _statusTimer;

    private SelectableTextBlock _chatBox = null!;
    private TextBlock _statusLabel = null!;
    private ScrollViewer _chatScroller = null!;
    private MenuItem _settingsItem = null!;
    private MenuItem _exitItem = null!;

    private TextBlock _replyTargetLabel = null!;
    private TextBox _replyInput = null!;
    private Button _replySendBtn = null!;
    private TextBlock _replyHintLabel = null!;

    private ChatMessage? _replyTarget;
    private bool _replyOnboardingShown;
    private bool _closed;

    private sealed class MessageInlines
    {
        public required ChatMessage Message;
        public required List<Inline> All;
        public required Run TranslationRun;
        public Run? TranslationFailedSuffix;
        public int CharStart;
        public int CharEnd;
    }

    private static int RunLength(Inline inline) =>
        inline is Run r ? (r.Text?.Length ?? 0) : 0;

    // Manual sum over the concrete List<Inline> — Enumerable.Sum would box the list enumerator
    // on the heap once per message on the UI thread, against this file's zero-alloc intent.
    private static int TotalLength(List<Inline> list)
    {
        var len = 0;
        foreach (var inline in list) len += RunLength(inline);
        return len;
    }

    public MainWindow()
    {
        InitializeComponent();

        _chatBox = this.FindControl<SelectableTextBlock>("ChatBox")!;
        _statusLabel = this.FindControl<TextBlock>("StatusLabel")!;
        _chatScroller = this.FindControl<ScrollViewer>("ChatScroller")!;
        _settingsItem = this.FindControl<MenuItem>("SettingsItem")!;
        _exitItem = this.FindControl<MenuItem>("ExitItem")!;

        _replyTargetLabel = this.FindControl<TextBlock>("ReplyTargetLabel")!;
        _replyInput = this.FindControl<TextBox>("ReplyInput")!;
        _replySendBtn = this.FindControl<Button>("ReplySendBtn")!;
        _replyHintLabel = this.FindControl<TextBlock>("ReplyHintLabel")!;

        _settingsItem.Click += async (_, _) => await OnOpenSettings();
        _exitItem.Click += (_, _) => Close();

        _chatBox.PointerPressed += OnChatBoxPointerPressed;
        _replySendBtn.Click += async (_, _) => await OnSendReply();
        _replyInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                e.Handled = true;
                await OnSendReply();
            }
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Opened += (_, _) => OnLoad();
        Closed += (_, _) =>
        {
            _closed = true;
            _statusTimer.Stop();
            _tailer?.Dispose();
            _translator.Dispose();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnLoad()
    {
        _config = ConfigStore.Load();
        UpdateStatus();
        UpdateReplyTargetLabel();
        _statusTimer.Start();
        StartTailer();
    }

    private void UpdateStatus()
    {
        if (string.IsNullOrWhiteSpace(_config.ConsoleLogPath))
        {
            _statusLabel.Text = "Nicht gestartet — bitte Einstellungen öffnen.";
            return;
        }
        if (!File.Exists(_config.ConsoleLogPath))
        {
            _statusLabel.Text = $"Datei nicht gefunden: {_config.ConsoleLogPath}";
            return;
        }
        var linesRead = _tailer?.LinesRead ?? 0;
        var running = _tailer != null ? "▶" : "⏸";
        _statusLabel.Text =
            $"{running} {Path.GetFileName(_config.ConsoleLogPath)}  •  Lang: {_config.TargetLanguage}  •  " +
            $"📖 {linesRead} Zeilen  •  💬 {_chatFound} Chat  •  🌐 {_translated} übersetzt";
    }

    private void StartTailer()
    {
        if (string.IsNullOrWhiteSpace(_config.ConsoleLogPath) ||
            !File.Exists(_config.ConsoleLogPath))
        {
            return;
        }

        try
        {
            _tailer = new ConsoleLogTailer(
                _config.ConsoleLogPath,
                startFromBeginning: false,
                seedTailBytes: ConsoleLogTailer.DefaultSeedTailBytes);
            _tailer.LineRead += OnLineRead;
            _tailer.SeedRead += OnSeedRead;
            _tailer.ErrorOccurred += OnTailerError;
            _tailer.Start();
        }
        catch (Exception ex)
        {
            ShowDialog("Fehler", $"Kann console.log nicht öffnen:\n{ex.Message}");
        }
    }

    private void OnTailerError(object? sender, Exception ex)
    {
        Dispatcher.UIThread.Post(() => _statusLabel.Text = $"Lesefehler: {ex.Message}");
    }

    private void OnLineRead(object? sender, string line)
    {
        if (!ChatLineParser.TryParse(line, out var msg) || msg is null) return;
        Dispatcher.UIThread.Post(() => HandleNewMessage(msg));
    }

    private async void HandleNewMessage(ChatMessage msg)
    {
        // Outer guard: async void over the UI dispatcher — an unobserved exception from a UI
        // mutation below (e.g. a shutdown race) would crash the process. Drop the one message
        // instead. The inner try/catch still renders the failed-translation state.
        try
        {
            _chatFound++;
            _messages.Add(msg);
            AppendMessage(msg);
            if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);
            await TranslateMessage(msg);
        }
        catch
        {
            // never let a UI-mutation exception escape an async void handler
        }
    }

    private async Task TranslateMessage(ChatMessage msg)
    {
        try
        {
            var result = await _translator.TranslateAsync(msg.Original, _config.TargetLanguage);
            msg.SourceLanguage = result.SourceLanguage;
            msg.Translation = result.Text;
            msg.TranslationFailed = result.Failed;
            if (!result.Failed) _translated++;
        }
        catch
        {
            msg.Translation = msg.Original;
            msg.TranslationFailed = true;
        }
        if (!_closed) UpdateMessageTranslation(msg);
    }

    private void OnSeedRead(object? sender, IReadOnlyList<string> lines)
    {
        var msgs = ChatSeedSelector.LastMessages(lines, SeedMaxMessages);
        if (msgs.Count == 0) return;
        Dispatcher.UIThread.Post(() => HandleSeed(msgs));
    }

    private async void HandleSeed(IReadOnlyList<ChatMessage> msgs)
    {
        try
        {
            foreach (var m in msgs)
            {
                _chatFound++;
                _messages.Add(m);
                AppendMessage(m);
            }
            if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);

            // Stagger translation starts so the keyless endpoint isn't hit with a burst.
            foreach (var m in msgs)
            {
                _ = TranslateMessage(m);
                await Task.Delay(SeedStaggerDelayMs);
            }
        }
        catch
        {
            // never let a UI-mutation exception escape an async void handler
        }
    }

    private void AppendMessage(ChatMessage m)
    {
        var inlines = _chatBox.Inlines;
        if (inlines is null) return;

        var atBottom = IsScrolledToBottom();
        var added = new List<Inline>(8);

        var (tag, tagBrush) = m.Type switch
        {
            ChatType.CT => ("[CT]", BrushCT),
            ChatType.T  => ("[T]",  BrushT),
            _           => ("[ALL]", BrushAll)
        };

        added.Add(Styled(tag + " ", tagBrush, bold: true));
        added.Add(Styled(m.Player, BrushPlayer, bold: true));
        if (m.IsDead) added.Add(Styled(" [TOT]", BrushDead, bold: true));
        if (!string.IsNullOrEmpty(m.Callout))
            added.Add(Styled(" @" + m.Callout, BrushCallout, italic: true));
        added.Add(Styled(": ", BrushPlayer, bold: true));
        added.Add(Styled(m.Original + "\n", BrushOriginal));

        added.Add(Styled("      → ", BrushArrow));
        var translationRun = Styled("[Übersetze…]\n", BrushPending, italic: true);
        added.Add(translationRun);

        foreach (var inline in added) inlines.Add(inline);

        var charLen = TotalLength(added);
        var entry = new MessageInlines
        {
            Message = m,
            All = added,
            TranslationRun = translationRun,
            CharStart = _totalChars,
            CharEnd = _totalChars + charLen
        };
        _totalChars += charLen;
        _inlines[m.Id] = entry;

        if (atBottom)
            Dispatcher.UIThread.Post(() => _chatScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void UpdateMessageTranslation(ChatMessage m)
    {
        if (!_inlines.TryGetValue(m.Id, out var entry)) return;
        var inlines = _chatBox.Inlines;
        if (inlines is null) return;

        var atBottom = IsScrolledToBottom();
        var oldLen = entry.CharEnd - entry.CharStart;

        if (entry.TranslationFailedSuffix is not null)
        {
            inlines.Remove(entry.TranslationFailedSuffix);
            entry.All.Remove(entry.TranslationFailedSuffix);
            entry.TranslationFailedSuffix = null;
        }

        if (m.Translation is null)
        {
            entry.TranslationRun.Text = "[Übersetze…]\n";
            entry.TranslationRun.Foreground = BrushPending;
            entry.TranslationRun.FontStyle = FontStyle.Italic;
        }
        else if (m.TranslationFailed)
        {
            entry.TranslationRun.Text = m.Translation;
            entry.TranslationRun.Foreground = BrushFailed;
            entry.TranslationRun.FontStyle = FontStyle.Italic;

            var suffix = Styled("  (Übersetzung fehlgeschlagen)\n", BrushFailed, italic: true);
            var insertAt = inlines.IndexOf(entry.TranslationRun) + 1;
            inlines.Insert(insertAt, suffix);
            var entryIdx = entry.All.IndexOf(entry.TranslationRun) + 1;
            entry.All.Insert(entryIdx, suffix);
            entry.TranslationFailedSuffix = suffix;
        }
        else
        {
            entry.TranslationRun.Text = m.Translation + "\n";
            entry.TranslationRun.Foreground = BrushOk;
            entry.TranslationRun.FontStyle = FontStyle.Normal;
        }

        var newLen = TotalLength(entry.All);
        var delta = newLen - oldLen;
        entry.CharEnd += delta;
        if (delta != 0) ShiftCharsAfter(entry, delta);

        if (atBottom)
            Dispatcher.UIThread.Post(() => _chatScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ShiftCharsAfter(MessageInlines anchor, int delta)
    {
        foreach (var e in _inlines.Values)
        {
            if (e == anchor) continue;
            if (e.CharStart >= anchor.CharEnd - delta)
            {
                e.CharStart += delta;
                e.CharEnd += delta;
            }
        }
        _totalChars += delta;
    }

    private void TrimOldest(int count)
    {
        if (count <= 0) return;
        var inlines = _chatBox.Inlines;
        if (inlines is null) return;

        var toRemove = _messages.Take(count).ToList();
        _messages.RemoveRange(0, count);
        var cutoff = 0;
        foreach (var m in toRemove)
        {
            if (!_inlines.TryGetValue(m.Id, out var entry)) continue;
            cutoff = Math.Max(cutoff, entry.CharEnd);
            foreach (var inline in entry.All) inlines.Remove(inline);
            _inlines.Remove(m.Id);
        }
        if (cutoff > 0)
        {
            foreach (var e in _inlines.Values)
            {
                e.CharStart -= cutoff;
                e.CharEnd -= cutoff;
            }
            _totalChars -= cutoff;
        }
    }

    private async Task OnOpenSettings()
    {
        var dlg = new SettingsWindow(_config);
        var result = await dlg.ShowDialog<AppConfig?>(this);
        if (result is null) return;

        var pathChanged = !string.Equals(
            result.ConsoleLogPath, _config.ConsoleLogPath, StringComparison.OrdinalIgnoreCase);

        _config = result;
        ConfigStore.Save(_config);
        UpdateStatus();
        UpdateReplyTargetLabel();

        if (pathChanged)
        {
            _tailer?.Dispose();
            _tailer = null;
            StartTailer();
        }
    }

    private static Run Styled(string text, IBrush brush, bool bold = false, bool italic = false)
    {
        var run = new Run(text) { Foreground = brush };
        if (bold) run.FontWeight = FontWeight.Bold;
        if (italic) run.FontStyle = FontStyle.Italic;
        return run;
    }

    private bool IsScrolledToBottom()
    {
        var sv = _chatScroller;
        return sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 2;
    }

    private void OnChatBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        MessageInlines? entry = null;
        if (e.Source is Run hitRun)
        {
            entry = _inlines.Values.FirstOrDefault(v => v.All.Contains(hitRun));
        }
        if (entry is null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var pos = _chatBox.SelectionStart;
                if (pos < 0) pos = _chatBox.SelectionEnd;
                if (pos < 0) return;
                var hit = _inlines.Values.FirstOrDefault(v => pos >= v.CharStart && pos < v.CharEnd);
                if (hit is null) return;
                SetReplyTarget(hit.Message);
            }, DispatcherPriority.Input);
            return;
        }
        SetReplyTarget(entry.Message);
    }

    private void SetReplyTarget(ChatMessage m)
    {
        _replyTarget = m;
        UpdateReplyTargetLabel();
        _replyInput.IsEnabled = true;
        _replySendBtn.IsEnabled = true;
        _replyInput.Focus();
    }

    private void UpdateReplyTargetLabel()
    {
        if (_replyTarget is null)
        {
            _replyTargetLabel.Text = "Antwort: keine Nachricht gewählt — klicke eine Nachricht oben an.";
            return;
        }
        var src = _replyTarget.SourceLanguage ?? "?";
        var target = _config.TargetLanguage;
        var scope = _replyTarget.Type switch
        {
            ChatType.CT => "Team",
            ChatType.T  => "Team",
            _           => "Alle"
        };
        _replyTargetLabel.Text =
            $"Antwort an {_replyTarget.Player} ({scope})  •  {target} → {src}";
    }

    private async Task OnSendReply()
    {
        if (_replyTarget is null) return;
        var text = (_replyInput.Text ?? "").Trim();
        if (text.Length == 0) return;

        var srcLang = _replyTarget.SourceLanguage;
        if (string.IsNullOrEmpty(srcLang))
        {
            SetHint("Quellsprache der gewählten Nachricht ist (noch) unbekannt.", BrushHintErr);
            return;
        }

        _replySendBtn.IsEnabled = false;
        SetHint("Übersetze und schreibe cfg…", BrushHint);

        TranslationService.TranslationResult result;
        try
        {
            result = await _translator.TranslateAsync(text, srcLang);
        }
        catch (Exception ex)
        {
            SetHint($"Übersetzungsfehler: {ex.Message}", BrushHintErr);
            _replySendBtn.IsEnabled = true;
            return;
        }

        if (result.Failed)
        {
            SetHint("Übersetzung fehlgeschlagen.", BrushHintErr);
            _replySendBtn.IsEnabled = true;
            return;
        }

        string cfgPath;
        try
        {
            cfgPath = ChatInjectionService.ResolveCfgPath(_config.ConsoleLogPath);
        }
        catch (Exception ex)
        {
            SetHint($"cfg-Pfad nicht ermittelbar: {ex.Message}", BrushHintErr);
            _replySendBtn.IsEnabled = true;
            return;
        }

        var firstTime = !File.Exists(cfgPath);

        try
        {
            ChatInjectionService.WriteSayCommand(cfgPath, result.Text, _replyTarget.Type);
        }
        catch (Exception ex)
        {
            SetHint($"cfg-Schreibfehler: {ex.Message}", BrushHintErr);
            _replySendBtn.IsEnabled = true;
            return;
        }

        _replyInput.Text = "";
        SetHint("✓ Bereit. Drücke F8 in CS2 (oder den gebundenen Key).", BrushHintOk);
        _replySendBtn.IsEnabled = true;

        if (firstTime && !_replyOnboardingShown)
        {
            _replyOnboardingShown = true;
            ShowDialog("Reply einrichten",
                $"cs2_translator_reply.cfg wurde geschrieben nach:\n  {cfgPath}\n\n" +
                "Damit das Senden funktioniert, einmalig in der CS2-Konsole eingeben:\n\n" +
                "  bind F8 \"exec cs2_translator_reply\"\n\n" +
                "Danach: in diesem Tool tippen → Senden → in CS2 F8 drücken.");
        }
    }

    private void SetHint(string text, IBrush brush)
    {
        _replyHintLabel.Text = text;
        _replyHintLabel.Foreground = brush;
    }

    private void ShowDialog(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Text = message,
                Margin = new Avalonia.Thickness(16),
                TextWrapping = TextWrapping.Wrap
            }
        };
        _ = dlg.ShowDialog(this);
    }
}
