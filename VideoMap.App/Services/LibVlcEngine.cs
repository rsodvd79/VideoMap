using System;
using System.Collections.Generic;
using System.IO;
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

            var directory = FindLibVlcDirectory();
            if (directory == null)
            {
                _status = "VLC non trovato: installa VLC per la preview video";
                libVlc = null;
                status = _status;
                return false;
            }

            try
            {
                Core.Initialize(directory);
                _libVlc = new LibVLC();
                _status = "LibVLC inizializzato";
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

    private static string? FindLibVlcDirectory()
    {
        var candidates = new List<string>();

        var envPath = Environment.GetEnvironmentVariable("VLC_LIB_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            candidates.Add(envPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/VLC.app/Contents/MacOS/lib");
            var userApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications/VLC.app/Contents/MacOS/lib");
            candidates.Add(userApps);
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

        foreach (var candidate in candidates)
        {
            if (HasLibVlc(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasLibVlc(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(Path.Combine(directory, "libvlc.dylib"));
        }

        if (OperatingSystem.IsWindows())
        {
            return File.Exists(Path.Combine(directory, "libvlc.dll"));
        }

        return File.Exists(Path.Combine(directory, "libvlc.so"));
    }
}
