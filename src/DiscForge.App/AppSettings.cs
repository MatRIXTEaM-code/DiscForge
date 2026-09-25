// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.App;

/// <summary>
/// Small persistent preferences, kept beside the settings the launcher already
/// writes.
///
/// The point is remembering where things went. Ripping a stack of CDs means the
/// same output folder every time, and a file dialog that opens wherever Windows
/// last left it — often some unrelated download directory — turns a two-click
/// job into a navigation exercise on every disc.
///
/// Deliberately forgiving: a corrupt or missing file yields defaults rather than
/// an error, because losing a remembered folder is a triviality and refusing to
/// start over it would not be.
/// </summary>
internal static class AppSettings
{
    private sealed class Model
    {
        [JsonPropertyName("lastRipDirectory")]
        public string? LastRipDirectory { get; set; }

        [JsonPropertyName("lastImageDirectory")]
        public string? LastImageDirectory { get; set; }

        [JsonPropertyName("lastExtractDirectory")]
        public string? LastExtractDirectory { get; set; }

        [JsonPropertyName("lastCueDirectory")]
        public string? LastCueDirectory { get; set; }

        [JsonPropertyName("lastLogDirectory")]
        public string? LastLogDirectory { get; set; }

        [JsonPropertyName("lastPackDirectory")]
        public string? LastPackDirectory { get; set; }
    }

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiscForge", "preferences.json");

    private static Model? _cache;

    private static Model Current
    {
        get
        {
            if (_cache is not null) return _cache;
            try
            {
                if (File.Exists(Path))
                    _cache = JsonSerializer.Deserialize<Model>(File.ReadAllText(Path));
            }
            catch
            {
                // Unreadable or malformed: start fresh rather than complain.
            }
            return _cache ??= new Model();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path,
                JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Write($"could not save preferences: {ex.Message}");
        }
    }

    /// <summary>A remembered directory, or null if it no longer exists —
    /// pointing a dialog at a deleted folder is worse than not remembering.</summary>
    private static string? Existing(string? path) =>
        path is not null && Directory.Exists(path) ? path : null;

    public static string? LastRipDirectory
    {
        get => Existing(Current.LastRipDirectory);
        set { Current.LastRipDirectory = value; Save(); }
    }

    public static string? LastImageDirectory
    {
        get => Existing(Current.LastImageDirectory);
        set { Current.LastImageDirectory = value; Save(); }
    }

    public static string? LastExtractDirectory
    {
        get => Existing(Current.LastExtractDirectory);
        set { Current.LastExtractDirectory = value; Save(); }
    }

    public static string? LastCueDirectory
    {
        get => Existing(Current.LastCueDirectory);
        set { Current.LastCueDirectory = value; Save(); }
    }

    public static string? LastLogDirectory
    {
        get => Existing(Current.LastLogDirectory);
        set { Current.LastLogDirectory = value; Save(); }
    }

    public static string? LastPackDirectory
    {
        get => Existing(Current.LastPackDirectory);
        set { Current.LastPackDirectory = value; Save(); }
    }
}