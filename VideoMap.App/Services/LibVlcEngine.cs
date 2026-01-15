using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;

namespace VideoMap.App.Services;

public static class LibVlcEngine
{
    private static readonly object Sync = new();
    private static bool _attempted;
    private static LibVLC? _libVlc;
    private static string _status = "LibVLC non inizializzato";

    public static bool TryGet(out LibVLC? libVlc, out string status)
    {
        lock (Sync)
        {
            if (_attempted)
            {
                libVlc = _libVlc;
                status = _status;
                return _libVlc != null;
            }

            _attempted = true;

            var candidates = BuildCandidateDirectories();
            var directory = ResolveLibVlcDirectory(candidates);
            if (directory == null)
            {
                _status = BuildNotFoundStatus(candidates);
                libVlc = null;
                status = _status;
                return false;
            }

            try
            {
                Core.Initialize(directory);
                var pluginPath = ResolvePluginPath(directory);
                _libVlc = CreateLibVlc(pluginPath, out var usedPluginPath);
                _status = usedPluginPath == null
                    ? $"LibVLC inizializzato da: {directory}"
                    : $"LibVLC inizializzato da: {directory} (plugin: {usedPluginPath})";
            }
            catch (Exception ex)
            {
                _status = $"LibVLC non disponibile: {ex.Message}";
                _libVlc = null;
            }

            libVlc = _libVlc;
            status = _status;
            return _libVlc != null;
        }
    }

    private static List<string> BuildCandidateDirectories()
    {
        var candidates = new List<string>();

        var envPath = Environment.GetEnvironmentVariable("VLC_LIB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            if (File.Exists(envPath))
            {
                var directory = Path.GetDirectoryName(envPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    candidates.Add(directory);
                }
            }
            else
            {
                candidates.Add(envPath);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/VLC.app/Contents/MacOS/lib");
            candidates.Add("/Applications/VLC.app/Contents/MacOS");
            var userApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications/VLC.app/Contents/MacOS/lib");
            candidates.Add(userApps);
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications/VLC.app/Contents/MacOS"));
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"));
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates.Add("/usr/lib/vlc");
            candidates.Add("/usr/lib/x86_64-linux-gnu/vlc");
        }

        return candidates;
    }

    private static string BuildNotFoundStatus(IEnumerable<string> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0)
        {
            return "VLC non trovato: installa VLC per la preview video";
        }

        var lines = string.Join(Environment.NewLine, list.Select(path => $"- {path}"));
        return $"VLC non trovato: installa VLC per la preview video.{Environment.NewLine}Percorsi cercati:{Environment.NewLine}{lines}";
    }

    private static LibVLC CreateLibVlc(string? pluginPath, out string? usedPluginPath)
    {
        usedPluginPath = null;

        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginPath);
            usedPluginPath = pluginPath;
        }

        return new LibVLC("--vout=vmem");
    }

    private static string? ResolvePluginPath(string? libDirectory)
    {
        var envPath = Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            if (Directory.Exists(envPath))
            {
                return envPath;
            }

            if (File.Exists(envPath))
            {
                return Path.GetDirectoryName(envPath);
            }
        }

        if (string.IsNullOrWhiteSpace(libDirectory))
        {
            return null;
        }

        var candidates = new List<string>();
        var fullLibDir = Path.GetFullPath(libDirectory);
        var sep = Path.DirectorySeparatorChar;
        var macosSuffix = $"{sep}Contents{sep}MacOS";
        var macosLibSuffix = $"{macosSuffix}{sep}lib";

        if (OperatingSystem.IsMacOS())
        {
            if (fullLibDir.EndsWith(macosLibSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var macosDir = Directory.GetParent(fullLibDir);
                if (macosDir != null)
                {
                    candidates.Add(Path.Combine(macosDir.FullName, "plugins"));
                }
            }

            if (fullLibDir.EndsWith(macosSuffix, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(fullLibDir, "plugins"));
            }

            candidates.Add(Path.Combine(fullLibDir, "plugins"));
            candidates.Add("/Applications/VLC.app/Contents/MacOS/plugins");
        }
        else
        {
            candidates.Add(Path.Combine(fullLibDir, "plugins"));
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveLibVlcDirectory(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (TryResolveLibVlcDirectory(candidate, out var directory))
            {
                return directory;
            }
        }

        return null;
    }

    private static bool TryResolveLibVlcDirectory(string path, out string? directory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            directory = null;
            return false;
        }

        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (OperatingSystem.IsMacOS() && ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(directory);
            }

            if (OperatingSystem.IsWindows() && ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(directory);
            }

            if (OperatingSystem.IsLinux() && ext.Equals(".so", StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(path);
                return !string.IsNullOrWhiteSpace(directory);
            }

            directory = null;
            return false;
        }

        if (!Directory.Exists(path))
        {
            directory = null;
            return false;
        }

        var libFolder = Path.Combine(path, "lib");

        if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(Path.Combine(path, "libvlc.dylib")))
            {
                directory = path;
                return true;
            }

            if (File.Exists(Path.Combine(libFolder, "libvlc.dylib")))
            {
                directory = libFolder;
                return true;
            }

            directory = null;
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(Path.Combine(path, "libvlc.dll")))
            {
                directory = path;
                return true;
            }

            if (File.Exists(Path.Combine(libFolder, "libvlc.dll")))
            {
                directory = libFolder;
                return true;
            }

            directory = null;
            return false;
        }

        if (File.Exists(Path.Combine(path, "libvlc.so")))
        {
            directory = path;
            return true;
        }

        if (File.Exists(Path.Combine(libFolder, "libvlc.so")))
        {
            directory = libFolder;
            return true;
        }

        directory = null;
        return false;
    }
}
