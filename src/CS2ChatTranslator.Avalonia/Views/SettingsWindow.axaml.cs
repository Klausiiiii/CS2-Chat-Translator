using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CS2ChatTranslator.Models;
using CS2ChatTranslator.Services;

namespace CS2ChatTranslator.Views;

public partial class SettingsWindow : Window
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

    private ComboBox _langBox = null!;
    private TextBox _pathBox = null!;
    private Button _browseBtn = null!;
    private Button _okBtn = null!;
    private Button _cancelBtn = null!;

    public SettingsWindow() : this(new AppConfig()) { }

    public SettingsWindow(AppConfig current)
    {
        InitializeComponent();

        _langBox = this.FindControl<ComboBox>("LangBox")!;
        _pathBox = this.FindControl<TextBox>("PathBox")!;
        _browseBtn = this.FindControl<Button>("BrowseBtn")!;
        _okBtn = this.FindControl<Button>("OkBtn")!;
        _cancelBtn = this.FindControl<Button>("CancelBtn")!;

        foreach (var (display, _) in Languages)
        {
            _langBox.Items.Add(display);
        }

        var currentIdx = Array.FindIndex(Languages, l =>
            string.Equals(l.Code, current.TargetLanguage, StringComparison.OrdinalIgnoreCase));
        _langBox.SelectedIndex = currentIdx < 0 ? 1 : currentIdx;

        _pathBox.Text = current.ConsoleLogPath;

        _browseBtn.Click += async (_, _) => await OnBrowse();
        _okBtn.Click += (_, _) => OnOk();
        _cancelBtn.Click += (_, _) => Close(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task OnBrowse()
    {
        IStorageFolder? startFolder = null;
        var start = GuessInitialDirectory();
        if (start is not null)
        {
            try { startFolder = await StorageProvider.TryGetFolderFromPathAsync(start); }
            catch { /* ignore */ }
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "console.log auswählen",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("Log-Dateien") { Patterns = ["*.log"] },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0)
        {
            _pathBox.Text = files[0].Path.LocalPath;
        }
    }

    private string? GuessInitialDirectory()
    {
        var found = SteamPaths.FindExistingCsgoDirectory();
        if (found is not null) return found;

        if (!string.IsNullOrWhiteSpace(_pathBox.Text))
        {
            var dir = Path.GetDirectoryName(_pathBox.Text);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        }
        return null;
    }

    private void OnOk()
    {
        if (_langBox.SelectedIndex < 0)
        {
            ShowWarning("Bitte eine Zielsprache auswählen.");
            return;
        }

        var path = (_pathBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowWarning("Bitte eine gültige console.log auswählen.");
            return;
        }

        var result = new AppConfig
        {
            TargetLanguage = Languages[_langBox.SelectedIndex].Code,
            ConsoleLogPath = path
        };
        Close(result);
    }

    private void ShowWarning(string message)
    {
        var dlg = new Window
        {
            Title = "Einstellungen",
            Width = 360,
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
