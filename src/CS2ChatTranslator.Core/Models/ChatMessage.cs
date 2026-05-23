using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CS2ChatTranslator.Models;

public sealed class ChatMessage : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public ChatType Type { get; init; }
    public string Player { get; init; } = "";
    public string Original { get; init; } = "";
    public string? Callout { get; init; }
    public bool IsDead { get; init; }

    private string? _translation;
    public string? Translation
    {
        get => _translation;
        set { if (_translation != value) { _translation = value; OnChanged(); } }
    }

    private bool _translationFailed;
    public bool TranslationFailed
    {
        get => _translationFailed;
        set { if (_translationFailed != value) { _translationFailed = value; OnChanged(); } }
    }

    public string? SourceLanguage { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
