using CS2ChatTranslator.Models;

namespace CS2ChatTranslator.UI;

public sealed class SettingsForm : Form
{
    private static readonly (string Display, string Code)[] Languages =
    [
        ("Deutsch",    "de"),
        ("English",    "en"),
        ("Español",    "es"),
        ("Français",   "fr"),
        ("Italiano",   "it"),
        ("Português",  "pt"),
        ("Polski",     "pl"),
        ("Русский",    "ru"),
        ("Türkçe",     "tr"),
        ("Čeština",    "cs"),
        ("Nederlands", "nl"),
        ("Svenska",    "sv"),
        ("中文 (简体)",  "zh-CN"),
        ("日本語",      "ja"),
        ("한국어",      "ko")
    ];

    private readonly ComboBox _langBox = new();
    private readonly TextBox _pathBox = new();
    private readonly Button _browseBtn = new();
    private readonly Button _okBtn = new();
    private readonly Button _cancelBtn = new();

    public AppConfig Result { get; private set; }

    public SettingsForm(AppConfig current)
    {
        Result = new AppConfig
        {
            TargetLanguage = current.TargetLanguage,
            ConsoleLogPath = current.ConsoleLogPath
        };

        Text = "Einstellungen";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 180);

        BuildUi(current);
    }

    private void BuildUi(AppConfig current)
    {
        var langLabel = new Label
        {
            Text = "Zielsprache:",
            Location = new Point(16, 20),
            AutoSize = true
        };
        Controls.Add(langLabel);

        _langBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _langBox.Items.AddRange(Languages.Select(l => (object)l.Display).ToArray());
        _langBox.Location = new Point(140, 16);
        _langBox.Size = new Size(360, 24);
        var currentIdx = Array.FindIndex(Languages, l =>
            string.Equals(l.Code, current.TargetLanguage, StringComparison.OrdinalIgnoreCase));
        _langBox.SelectedIndex = currentIdx < 0 ? 1 : currentIdx;
        Controls.Add(_langBox);

        var pathLabel = new Label
        {
            Text = "console.log:",
            Location = new Point(16, 60),
            AutoSize = true
        };
        Controls.Add(pathLabel);

        _pathBox.ReadOnly = true;
        _pathBox.Location = new Point(140, 56);
        _pathBox.Size = new Size(280, 24);
        _pathBox.Text = current.ConsoleLogPath;
        Controls.Add(_pathBox);

        _browseBtn.Text = "Durchsuchen…";
        _browseBtn.Location = new Point(426, 54);
        _browseBtn.Size = new Size(74, 26);
        _browseBtn.Click += OnBrowse;
        Controls.Add(_browseBtn);

        var hint = new Label
        {
            Text = "CS2 mit Startparameter -condebug starten, damit die Datei erzeugt wird.",
            Location = new Point(16, 90),
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        Controls.Add(hint);

        _okBtn.Text = "OK";
        _okBtn.Location = new Point(330, 130);
        _okBtn.Size = new Size(80, 28);
        _okBtn.Click += OnOk;
        Controls.Add(_okBtn);
        AcceptButton = _okBtn;

        _cancelBtn.Text = "Abbrechen";
        _cancelBtn.Location = new Point(420, 130);
        _cancelBtn.Size = new Size(80, 28);
        _cancelBtn.DialogResult = DialogResult.Cancel;
        Controls.Add(_cancelBtn);
        CancelButton = _cancelBtn;
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "console.log auswählen",
            Filter = "Log-Dateien (*.log)|*.log|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = GuessInitialDirectory()
        };
        if (!string.IsNullOrWhiteSpace(_pathBox.Text) && File.Exists(_pathBox.Text))
        {
            dlg.FileName = _pathBox.Text;
        }

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.FileName;
        }
    }

    private string GuessInitialDirectory()
    {
        string[] candidates =
        [
            @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo",
            @"C:\Program Files\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo",
            @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo"
        ];
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        if (!string.IsNullOrWhiteSpace(_pathBox.Text))
        {
            var dir = Path.GetDirectoryName(_pathBox.Text);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_langBox.SelectedIndex < 0)
        {
            MessageBox.Show(this, "Bitte eine Zielsprache auswählen.", "Einstellungen",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var path = _pathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "Bitte eine gültige console.log auswählen.", "Einstellungen",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new AppConfig
        {
            TargetLanguage = Languages[_langBox.SelectedIndex].Code,
            ConsoleLogPath = path
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
