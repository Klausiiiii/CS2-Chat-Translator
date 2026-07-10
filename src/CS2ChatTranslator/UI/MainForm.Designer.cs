#nullable enable
namespace CS2ChatTranslator.UI;

partial class MainForm
{
    private System.ComponentModel.IContainer? components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            _tailer?.Dispose();
            _translator.Dispose();
            _fontRegular?.Dispose();
            _fontBold?.Dispose();
            _fontItalic?.Dispose();
        }
        base.Dispose(disposing);
    }

    private MenuStrip _menu = null!;
    private ToolStripMenuItem _fileMenu = null!;
    private ToolStripMenuItem _settingsItem = null!;
    private ToolStripMenuItem _exitItem = null!;
    private StatusStrip _status = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _versionLabel = null!;
    private RichTextBox _chatBox = null!;

    private Panel _replyPanel = null!;
    private Label _replyTargetLabel = null!;
    private TextBox _replyInput = null!;
    private Button _replySendBtn = null!;
    private Label _replyHintLabel = null!;

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        SuspendLayout();

        _menu = new MenuStrip();
        _fileMenu = new ToolStripMenuItem("&Datei");
        _settingsItem = new ToolStripMenuItem("&Einstellungen…");
        _exitItem = new ToolStripMenuItem("&Beenden");
        _fileMenu.DropDownItems.Add(_settingsItem);
        _fileMenu.DropDownItems.Add(new ToolStripSeparator());
        _fileMenu.DropDownItems.Add(_exitItem);
        _menu.Items.Add(_fileMenu);
        _settingsItem.Click += (s, e) => OnOpenSettings();
        _exitItem.Click += (s, e) => Close();
        MainMenuStrip = _menu;

        _status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Nicht gestartet — bitte Einstellungen öffnen.")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _versionLabel = new ToolStripStatusLabel { ForeColor = Color.Gray };
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_versionLabel);

        _replyPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            BackColor = Color.FromArgb(30, 33, 38),
            Padding = new Padding(8, 6, 8, 6)
        };

        _replyTargetLabel = new Label
        {
            Text = "Antwort: keine Nachricht gewählt — klicke eine Nachricht oben an.",
            ForeColor = Color.Gainsboro,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _replyInput = new TextBox
        {
            Top = 26,
            Left = 8,
            Width = 600,
            Height = 24,
            BackColor = Color.FromArgb(40, 44, 50),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Enabled = false
        };
        _replyInput.KeyDown += OnReplyInputKeyDown;

        _replySendBtn = new Button
        {
            Text = "Senden",
            Top = 26,
            Width = 90,
            Height = 26,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Enabled = false
        };
        _replySendBtn.Click += (_, _) => OnSendReply();

        _replyHintLabel = new Label
        {
            Text = "Tipp: in CS2 einmalig in der Konsole eingeben: bind F8 \"exec cs2_translator_reply\"",
            ForeColor = Color.DimGray,
            AutoSize = false,
            Top = 54,
            Left = 8,
            Height = 16,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };

        _replyPanel.Controls.Add(_replyInput);
        _replyPanel.Controls.Add(_replySendBtn);
        _replyPanel.Controls.Add(_replyHintLabel);
        _replyPanel.Controls.Add(_replyTargetLabel);
        _replyPanel.Resize += (_, _) => LayoutReplyPanel();

        _chatBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(24, 26, 30),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 10f),
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false
        };
        _chatBox.MouseClick += OnChatBoxClick;

        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 620);
        Text = "CS2 Chat Translator";
        Controls.Add(_chatBox);
        Controls.Add(_replyPanel);
        Controls.Add(_status);
        Controls.Add(_menu);

        ResumeLayout(false);
        PerformLayout();
        LayoutReplyPanel();
    }

    private void LayoutReplyPanel()
    {
        const int padding = 8;
        const int btnWidth = 90;
        const int gap = 8;
        var w = _replyPanel.ClientSize.Width;
        _replySendBtn.Left = w - padding - btnWidth;
        _replySendBtn.Width = btnWidth;
        _replyInput.Left = padding;
        _replyInput.Width = Math.Max(100, w - padding - btnWidth - gap - padding);
        _replyTargetLabel.Left = padding;
        _replyTargetLabel.Width = w - padding * 2;
        _replyHintLabel.Left = padding;
        _replyHintLabel.Width = w - padding * 2;
    }
}
