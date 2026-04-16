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
        }
        base.Dispose(disposing);
    }

    private MenuStrip _menu = null!;
    private ToolStripMenuItem _fileMenu = null!;
    private ToolStripMenuItem _settingsItem = null!;
    private ToolStripMenuItem _exitItem = null!;
    private StatusStrip _status = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private RichTextBox _chatBox = null!;

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
        _statusLabel = new ToolStripStatusLabel("Nicht gestartet — bitte Einstellungen öffnen.");
        _status.Items.Add(_statusLabel);

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

        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 560);
        Text = "CS2 Chat Translator";
        Controls.Add(_chatBox);
        Controls.Add(_status);
        Controls.Add(_menu);

        ResumeLayout(false);
        PerformLayout();
    }
}
