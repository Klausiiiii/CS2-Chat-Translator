using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Views;

public partial class MainWindow : Window
{
    private const int MaxMessages = 500;

    private AppConfig _config = new();
    private ConsoleLogTailer? _tailer;
    private readonly TranslationService _translator = new();
    private readonly List<ChatMessage> _messages = new();
    private int _chatFound;
    private int _translated;
    private readonly DispatcherTimer _statusTimer;

    private SelectableTextBlock _chatBox = null!;
    private TextBlock _statusLabel = null!;
    private ScrollViewer _chatScroller = null!;
    private MenuItem _settingsItem = null!;
    private MenuItem _exitItem = null!;

    public MainWindow()
    {
        InitializeComponent();

        _chatBox = this.FindControl<SelectableTextBlock>("ChatBox")!;
        _statusLabel = this.FindControl<TextBlock>("StatusLabel")!;
        _chatScroller = this.FindControl<ScrollViewer>("ChatScroller")!;
        _settingsItem = this.FindControl<MenuItem>("SettingsItem")!;
        _exitItem = this.FindControl<MenuItem>("ExitItem")!;

        _settingsItem.Click += async (_, _) => await OnOpenSettings();
        _exitItem.Click += (_, _) => Close();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Opened += (_, _) => OnLoad();
        Closed += (_, _) =>
        {
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
            _tailer = new ConsoleLogTailer(_config.ConsoleLogPath, startFromBeginning: true);
            _tailer.LineRead += OnLineRead;
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
        _chatFound++;
        _messages.Add(msg);
        if (_messages.Count > MaxMessages)
        {
            _messages.RemoveRange(0, _messages.Count - MaxMessages);
        }
        Render();

        try
        {
            var result = await _translator.TranslateAsync(msg.Original, _config.TargetLanguage);
            msg.Translation = result.Text;
            msg.TranslationFailed = result.Failed;
            if (!result.Failed) _translated++;
        }
        catch
        {
            msg.Translation = msg.Original;
            msg.TranslationFailed = true;
        }

        Render();
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

        if (pathChanged)
        {
            _tailer?.Dispose();
            _tailer = null;
            StartTailer();
        }
    }

    private void Render()
    {
        var atBottom = IsScrolledToBottom();

        var inlines = _chatBox.Inlines;
        if (inlines is null) return;
        inlines.Clear();

        foreach (var m in _messages)
        {
            var (tag, color) = m.Type switch
            {
                ChatType.CT => ("[CT]", Color.FromRgb(100, 149, 237)),
                ChatType.T  => ("[T]",  Color.FromRgb(255, 215, 0)),
                _           => ("[ALL]", Color.FromRgb(211, 211, 211))
            };

            inlines.Add(Styled(tag + " ", color, bold: true));
            inlines.Add(Styled(m.Player, Colors.White, bold: true));
            if (m.IsDead) inlines.Add(Styled(" [TOT]", Color.FromRgb(205, 92, 92), bold: true));
            if (!string.IsNullOrEmpty(m.Callout))
                inlines.Add(Styled(" @" + m.Callout, Color.FromRgb(147, 112, 219), italic: true));
            inlines.Add(Styled(": ", Colors.White, bold: true));
            inlines.Add(Styled(m.Original + "\n", Color.FromRgb(220, 220, 220)));

            inlines.Add(Styled("      → ", Color.FromRgb(105, 105, 105)));
            if (m.Translation is null)
            {
                inlines.Add(Styled("[Übersetze…]\n", Color.FromRgb(105, 105, 105), italic: true));
            }
            else if (m.TranslationFailed)
            {
                inlines.Add(Styled(m.Translation, Color.FromRgb(205, 92, 92), italic: true));
                inlines.Add(Styled("  (Übersetzung fehlgeschlagen)\n", Color.FromRgb(205, 92, 92), italic: true));
            }
            else
            {
                inlines.Add(Styled(m.Translation + "\n", Color.FromRgb(152, 251, 152)));
            }
        }

        if (atBottom)
        {
            Dispatcher.UIThread.Post(() => _chatScroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private static Run Styled(string text, Color color, bool bold = false, bool italic = false)
    {
        var run = new Run(text) { Foreground = new SolidColorBrush(color) };
        if (bold) run.FontWeight = FontWeight.Bold;
        if (italic) run.FontStyle = FontStyle.Italic;
        return run;
    }

    private bool IsScrolledToBottom()
    {
        var sv = _chatScroller;
        return sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 2;
    }

    private void ShowDialog(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
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
