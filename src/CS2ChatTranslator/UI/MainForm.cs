using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.UI;

public partial class MainForm : Form
{
    private const int MaxMessages = 500;

    private AppConfig _config = new();
    private ConsoleLogTailer? _tailer;
    private readonly TranslationService _translator = new();
    private readonly List<ChatMessage> _messages = new();
    private int _chatFound;
    private int _translated;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 500 };

    public MainForm()
    {
        InitializeComponent();
        Load += (_, _) => OnLoadAsync();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    private void OnLoadAsync()
    {
        _config = ConfigStore.Load();
        UpdateStatus();
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
            MessageBox.Show(this, $"Kann console.log nicht öffnen:\n{ex.Message}",
                "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnTailerError(object? sender, Exception ex)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => _statusLabel.Text = $"Lesefehler: {ex.Message}");
    }

    private void OnLineRead(object? sender, string line)
    {
        if (!ChatLineParser.TryParse(line, out var msg) || msg is null) return;
        if (!IsHandleCreated) return;
        BeginInvoke(() => HandleNewMessage(msg));
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

        if (IsHandleCreated) Render();
    }

    private void OnOpenSettings()
    {
        using var settings = new SettingsForm(_config);
        if (settings.ShowDialog(this) != DialogResult.OK) return;

        var pathChanged = !string.Equals(
            settings.Result.ConsoleLogPath, _config.ConsoleLogPath, StringComparison.OrdinalIgnoreCase);

        _config = settings.Result;
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

        _chatBox.SuspendLayout();
        _chatBox.Clear();

        foreach (var m in _messages)
        {
            var (tag, color) = m.Type switch
            {
                ChatType.CT => ("[CT]",  Color.CornflowerBlue),
                ChatType.T  => ("[T]",   Color.Gold),
                _           => ("[ALL]", Color.LightGray)
            };

            AppendColored(tag + " ", color, FontStyle.Bold);
            AppendColored(m.Player, Color.White, FontStyle.Bold);
            if (m.IsDead) AppendColored(" [TOT]", Color.IndianRed, FontStyle.Bold);
            if (!string.IsNullOrEmpty(m.Callout))
                AppendColored(" @" + m.Callout, Color.MediumPurple, FontStyle.Italic);
            AppendColored(": ", Color.White, FontStyle.Bold);
            AppendColored(m.Original + "\n", Color.Gainsboro, FontStyle.Regular);

            AppendColored("      → ", Color.DimGray, FontStyle.Regular);
            if (m.Translation is null)
            {
                AppendColored("[Übersetze…]\n", Color.DimGray, FontStyle.Italic);
            }
            else if (m.TranslationFailed)
            {
                AppendColored(m.Translation, Color.IndianRed, FontStyle.Italic);
                AppendColored("  (Übersetzung fehlgeschlagen)\n", Color.IndianRed, FontStyle.Italic);
            }
            else
            {
                AppendColored(m.Translation + "\n", Color.PaleGreen, FontStyle.Regular);
            }
        }

        _chatBox.ResumeLayout();

        if (atBottom) ScrollToBottom();
    }

    private void AppendColored(string text, Color color, FontStyle style)
    {
        _chatBox.SelectionStart = _chatBox.TextLength;
        _chatBox.SelectionLength = 0;
        _chatBox.SelectionColor = color;
        _chatBox.SelectionFont = new Font(_chatBox.Font, style);
        _chatBox.AppendText(text);
    }

    private bool IsScrolledToBottom()
    {
        var lastVisibleCharIndex = _chatBox.GetCharIndexFromPosition(
            new Point(0, _chatBox.ClientSize.Height - 1));
        return lastVisibleCharIndex >= _chatBox.TextLength - 1;
    }

    private void ScrollToBottom()
    {
        _chatBox.SelectionStart = _chatBox.TextLength;
        _chatBox.SelectionLength = 0;
        _chatBox.ScrollToCaret();
    }
}
