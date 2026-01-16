using System;
using System.IO;
using System.Text.Json;

namespace VideoMap.App.Services;

public sealed class AppSettings
{
    public string? VlcBasePath { get; set; }
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static AppSettings? _cached;

    public static AppSettings Load()
    {
        if (_cached != null)
        {
            return _cached;
        }

        var path = GetSettingsPath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                _cached = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                return _cached;
            }
            catch
            {
            }
        }

        _cached = new AppSettings();
        return _cached;
    }

    public static void Save(AppSettings settings)
    {
        var path = GetSettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(path, json);
        _cached = settings;
    }

    public static string? GetVlcBasePath()
    {
        return Load().VlcBasePath;
    }

    public static void SetVlcBasePath(string? path)
    {
        var settings = Load();
        settings.VlcBasePath = string.IsNullOrWhiteSpace(path) ? null : path;
        Save(settings);
    }

    private static string GetSettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "VideoMap", "settings.json");
    }
}
