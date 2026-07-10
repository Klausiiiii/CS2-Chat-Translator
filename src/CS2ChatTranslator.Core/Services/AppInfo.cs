using System.Reflection;

namespace CS2ChatTranslator.Services;

/// <summary>
/// App metadata read from the assembly. The version is stamped centrally via
/// Directory.Build.props (<c>&lt;Version&gt;</c>), so both UIs show the same number
/// the release carries — no hardcoded copy that can drift from the release tag.
/// </summary>
public static class AppInfo
{
    /// <summary>Display string like <c>v1.2.0</c> for the given assembly's version.</summary>
    public static string DisplayVersion(Assembly assembly) => Format(assembly.GetName().Version);

    // Major.Minor.Build only — the 4th (revision) field is always 0 here and would just be noise.
    internal static string Format(Version? version) =>
        version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
}
