// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;

namespace DiscForge.App;

/// <summary>Serializable settings model (public members for the JSON reflection serializer).</summary>
internal sealed class SettingsModel
{
    public bool FirstRunComplete { get; set; }
    public bool RetroSkin { get; set; }
    public List<string> Recent { get; set; } = new();
}

/// <summary>
/// Small persisted-settings store in %APPDATA%\DiscForge\settings.json:
/// first-run flag and the recent-files list. Raises <see cref="Changed"/> so the
/// menu can rebuild its Recent submenu live.
/// </summary>
internal static class Settings
{
    private const int MaxRecent = 8;

    public static event Action? Changed;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscForge");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly SettingsModel _model = Load();

    private static SettingsModel Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt or unreadable -> defaults */ }
        return new SettingsModel();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
        Changed?.Invoke();
    }

    public static bool FirstRunComplete => _model.FirstRunComplete;

    public static bool RetroSkin
    {
        get => _model.RetroSkin;
        set { _model.RetroSkin = value; Save(); }
    }


    public static void MarkFirstRunComplete()
    {
        if (_model.FirstRunComplete) return;
        _model.FirstRunComplete = true;
        Save();
    }

    public static IReadOnlyList<string> Recent => _model.Recent;

    public static void AddRecent(string path)
    {
        _model.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _model.Recent.Insert(0, path);
        while (_model.Recent.Count > MaxRecent) _model.Recent.RemoveAt(_model.Recent.Count - 1);
        Save();
    }

    public static void ClearRecent()
    {
        if (_model.Recent.Count == 0) return;
        _model.Recent.Clear();
        Save();
    }
}
