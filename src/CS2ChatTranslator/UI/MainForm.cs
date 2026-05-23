using System.Runtime.InteropServices;
using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.UI;

public partial class MainForm : Form
{
    private const int MaxMessages = 500;

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);
    private const int WM_SETREDRAW = 0x000B;

    private AppConfig _config = new();
    private ConsoleLogTailer? _tailer;
    private readonly TranslationService _translator = new();
    private readonly List<ChatMessage> _messages = new();
    private readonly Dictionary<Guid, MessageRange> _ranges = new();
    private int _chatFound;
    private int _translated;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 500 };

    private Font? _fontRegular;
    private Font? _fontBold;
    private Font? _fontItalic;

    private ChatMessage? _replyTarget;
    private bool _replyOnboardingShown;

    private sealed class MessageRange
    {
        public int Start;
        public int TranslationStart;
        public int End;
    }

    public MainForm()
    {
        InitializeComponent();
        Load += (_, _) => OnLoadAsync();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    private void OnLoadAsync()
    {
        _fontRegular = new Font(_chatBox.Font, FontStyle.Regular);
        _fontBold    = new Font(_chatBox.Font, FontStyle.Bold);
        _fontItalic  = new Font(_chatBox.Font, FontStyle.Italic);

        _config = ConfigStore.Load();
        UpdateStatus();
        UpdateReplyTargetLabel();
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
            _tailer = new ConsoleLogTailer(_config.ConsoleLogPath, startFromBeginning: false);
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
        AppendMessage(msg);
        if (_messages.Count > MaxMessages) TrimOldest(_messages.Count - MaxMessages);

        try
        {
            var result = await _translator.TranslateAsync(msg.Original, _config.TargetLanguage);
            msg.Translation = result.Text;
            msg.TranslationFailed = result.Failed;
            msg.SourceLanguage = result.SourceLanguage;
            if (!result.Failed) _translated++;
        }
        catch
        {
            msg.Translation = msg.Original;
            msg.TranslationFailed = true;
        }

        if (IsHandleCreated) UpdateMessageTranslation(msg);
    }

    private void AppendMessage(ChatMessage m)
    {
        var atBottom = IsScrolledToBottom();

        SendMessage(_chatBox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var range = new MessageRange { Start = _chatBox.TextLength };
            var (tag, color) = m.Type switch
            {
                ChatType.CT => ("[CT]",  Color.CornflowerBlue),
                ChatType.T  => ("[T]",   Color.Gold),
                _           => ("[ALL]", Color.LightGray)
            };

            AppendColored(tag + " ", color, _fontBold!);
            AppendColored(m.Player, Color.White, _fontBold!);
            if (m.IsDead) AppendColored(" [TOT]", Color.IndianRed, _fontBold!);
            if (!string.IsNullOrEmpty(m.Callout))
                AppendColored(" @" + m.Callout, Color.MediumPurple, _fontItalic!);
            AppendColored(": ", Color.White, _fontBold!);
            AppendColored(m.Original + "\n", Color.Gainsboro, _fontRegular!);

            AppendColored("      → ", Color.DimGray, _fontRegular!);
            range.TranslationStart = _chatBox.TextLength;
            AppendColored("[Übersetze…]\n", Color.DimGray, _fontItalic!);
            range.End = _chatBox.TextLength;

            _ranges[m.Id] = range;
        }
        finally
        {
            SendMessage(_chatBox.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _chatBox.Invalidate();
        }

        if (atBottom) ScrollToBottom();
    }

    private void UpdateMessageTranslation(ChatMessage m)
    {
        if (!_ranges.TryGetValue(m.Id, out var range)) return;

        var atBottom = IsScrolledToBottom();

        SendMessage(_chatBox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var oldLen = range.End - range.TranslationStart;
            _chatBox.Select(range.TranslationStart, oldLen);

            string newText;
            Color color;
            Font font;
            if (m.Translation is null)
            {
                newText = "[Übersetze…]\n";
                color = Color.DimGray;
                font = _fontItalic!;
            }
            else if (m.TranslationFailed)
            {
                newText = m.Translation + "  (Übersetzung fehlgeschlagen)\n";
                color = Color.IndianRed;
                font = _fontItalic!;
            }
            else
            {
                newText = m.Translation + "\n";
                color = Color.PaleGreen;
                font = _fontRegular!;
            }

            _chatBox.SelectionColor = color;
            _chatBox.SelectionFont = font;
            _chatBox.SelectedText = newText;

            var newLen = newText.Length;
            var delta = newLen - oldLen;
            range.End = range.TranslationStart + newLen;

            if (delta != 0) ShiftRangesAfter(range, delta);
        }
        finally
        {
            SendMessage(_chatBox.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            _chatBox.Invalidate();
        }

        if (atBottom) ScrollToBottom();
    }

    private void ShiftRangesAfter(MessageRange anchor, int delta)
    {
        var anchorEnd = anchor.End - delta;
        foreach (var r in _ranges.Values)
        {
            if (r == anchor) continue;
            if (r.Start >= anchorEnd)
            {
                r.Start += delta;
                r.TranslationStart += delta;
                r.End += delta;
            }
        }
    }

    private void TrimOldest(int count)
    {
        if (count <= 0) return;
        var toRemove = _messages.Take(count).ToList();
        var cutEnd = 0;
        foreach (var m in toRemove)
        {
            if (_ranges.TryGetValue(m.Id, out var r))
            {
                cutEnd = Math.Max(cutEnd, r.End);
                _ranges.Remove(m.Id);
            }
        }
        _messages.RemoveRange(0, count);

        if (cutEnd > 0)
        {
            SendMessage(_chatBox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                _chatBox.Select(0, cutEnd);
                _chatBox.SelectedText = "";
                foreach (var r in _ranges.Values)
                {
                    r.Start -= cutEnd;
                    r.TranslationStart -= cutEnd;
                    r.End -= cutEnd;
                }
            }
            finally
            {
                SendMessage(_chatBox.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _chatBox.Invalidate();
            }
        }
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

    private void AppendColored(string text, Color color, Font font)
    {
        _chatBox.SelectionStart = _chatBox.TextLength;
        _chatBox.SelectionLength = 0;
        _chatBox.SelectionColor = color;
        _chatBox.SelectionFont = font;
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

    private void OnChatBoxClick(object? sender, MouseEventArgs e)
    {
        var idx = _chatBox.GetCharIndexFromPosition(e.Location);
        if (idx < 0) return;
        var hit = _messages.FirstOrDefault(m =>
            _ranges.TryGetValue(m.Id, out var r) && idx >= r.Start && idx < r.End);
        if (hit is null) return;

        _replyTarget = hit;
        UpdateReplyTargetLabel();
        _replyInput.Enabled = true;
        _replySendBtn.Enabled = true;
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

    private void OnReplyInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            OnSendReply();
        }
    }

    private async void OnSendReply()
    {
        if (_replyTarget is null) return;
        var text = _replyInput.Text.Trim();
        if (text.Length == 0) return;

        var srcLang = _replyTarget.SourceLanguage;
        if (string.IsNullOrEmpty(srcLang))
        {
            _replyHintLabel.ForeColor = Color.IndianRed;
            _replyHintLabel.Text = "Quellsprache der gewählten Nachricht ist (noch) unbekannt.";
            return;
        }

        _replySendBtn.Enabled = false;
        _replyHintLabel.ForeColor = Color.DimGray;
        _replyHintLabel.Text = "Übersetze und schreibe cfg…";

        TranslationService.TranslationResult result;
        try
        {
            result = await _translator.TranslateAsync(text, srcLang);
        }
        catch (Exception ex)
        {
            _replyHintLabel.ForeColor = Color.IndianRed;
            _replyHintLabel.Text = $"Übersetzungsfehler: {ex.Message}";
            _replySendBtn.Enabled = true;
            return;
        }

        if (result.Failed)
        {
            _replyHintLabel.ForeColor = Color.IndianRed;
            _replyHintLabel.Text = "Übersetzung fehlgeschlagen.";
            _replySendBtn.Enabled = true;
            return;
        }

        string cfgPath;
        try
        {
            cfgPath = ChatInjectionService.ResolveCfgPath(_config.ConsoleLogPath);
        }
        catch (Exception ex)
        {
            _replyHintLabel.ForeColor = Color.IndianRed;
            _replyHintLabel.Text = $"cfg-Pfad nicht ermittelbar: {ex.Message}";
            _replySendBtn.Enabled = true;
            return;
        }

        var firstTime = !File.Exists(cfgPath);

        try
        {
            ChatInjectionService.WriteSayCommand(cfgPath, result.Text, _replyTarget.Type);
        }
        catch (Exception ex)
        {
            _replyHintLabel.ForeColor = Color.IndianRed;
            _replyHintLabel.Text = $"cfg-Schreibfehler: {ex.Message}";
            _replySendBtn.Enabled = true;
            return;
        }

        _replyInput.Clear();
        _replyHintLabel.ForeColor = Color.PaleGreen;
        _replyHintLabel.Text = "✓ Bereit. Drücke F8 in CS2 (oder den gebundenen Key).";
        _replySendBtn.Enabled = true;

        if (firstTime && !_replyOnboardingShown)
        {
            _replyOnboardingShown = true;
            MessageBox.Show(this,
                $"cs2_translator_reply.cfg wurde geschrieben nach:\n  {cfgPath}\n\n" +
                "Damit das Senden funktioniert, einmalig in der CS2-Konsole eingeben:\n\n" +
                "  bind F8 \"exec cs2_translator_reply\"\n\n" +
                "Danach: in diesem Tool tippen → Senden → in CS2 F8 drücken.",
                "Reply einrichten", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
