using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace VideoMap.App.Services;

public static class LibVlcEngine
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 8;
    private const string VlcEnvFlag = "--vlc-env";

    private static readonly object Sync = new();
    private static bool _attempted;
    private static LibVLC? _libVlc;
    private static string _status = "LibVLC non inizializzato";
    private static string? _userBasePath;
    private static bool _resolverConfigured;
    private static string? _resolverPath;
    private static IntPtr _resolverHandle;
    private static string? _resolverCorePath;
    private static IntPtr _resolverCoreHandle;
    private static IntPtr _preloadedLibHandle;
    private static IntPtr _preloadedCoreHandle;

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

            var pluginPath = ResolvePluginPath(directory);
            try
            {
                EnsureEnvironmentPaths(directory, pluginPath);
                var libPath = GetLibVlcPath(directory);
                PreloadLibVlcLibraries(libPath);
                EnsureLibVlcResolver(libPath);
                SetLibVlcOverride(libPath);
                Core.Initialize(directory);
                _libVlc = CreateLibVlc(pluginPath, out var usedPluginPath);
                _status = usedPluginPath == null
                    ? $"LibVLC inizializzato da: {directory}"
                    : $"LibVLC inizializzato da: {directory} (plugin: {usedPluginPath})";
            }
            catch (Exception ex)
            {
                var lastError = GetLastLibVlcError();
                var pluginInfo = string.IsNullOrWhiteSpace(pluginPath) ? "n/a" : pluginPath;
                var errorInfo = string.IsNullOrWhiteSpace(lastError) ? string.Empty : $"{Environment.NewLine}Dettagli LibVLC: {lastError}";
                _status = $"LibVLC non disponibile: {ex.Message} (lib: {directory}, plugin: {pluginInfo}){errorInfo}";
                _libVlc = null;
            }

            Console.WriteLine($"[LibVLC] {_status}");
            libVlc = _libVlc;
            status = _status;
            return _libVlc != null;
        }
    }

    public static void ConfigureUserBasePath(string? basePath)
    {
        lock (Sync)
        {
            _userBasePath = string.IsNullOrWhiteSpace(basePath) ? null : basePath;
            _attempted = false;
            _libVlc?.Dispose();
            _libVlc = null;
            _status = "LibVLC non inizializzato";
        }
    }

    public static bool TryRelaunchWithEnvironment(string? basePath, IReadOnlyList<string>? args = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var commandLine = args ?? Environment.GetCommandLineArgs();
        if (commandLine.Any(arg => string.Equals(arg, VlcEnvFlag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!TryResolveLibVlcPaths(basePath, out var libDirectory, out var pluginPath, out var dataPath))
        {
            return false;
        }

        if (IsEnvironmentConfigured(libDirectory, pluginPath, dataPath))
        {
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
        };

        foreach (var arg in commandLine.Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add(VlcEnvFlag);
        startInfo.Environment["VLC_LIB_PATH"] = libDirectory;

        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            startInfo.Environment["VLC_PLUGIN_PATH"] = pluginPath;
        }

        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            startInfo.Environment["VLC_DATA_PATH"] = dataPath;
        }

        startInfo.Environment["DYLD_LIBRARY_PATH"] = BuildEnvPath("DYLD_LIBRARY_PATH", libDirectory);
        startInfo.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = BuildEnvPath("DYLD_FALLBACK_LIBRARY_PATH", libDirectory);

        Process.Start(startInfo);
        return true;
    }

    private static List<string> BuildCandidateDirectories()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_userBasePath))
        {
            AddUserBasePathCandidates(candidates, _userBasePath);
        }

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

    private static void EnsureEnvironmentPaths(string libDirectory, string? pluginPath)
    {
        Environment.SetEnvironmentVariable("VLC_LIB_PATH", libDirectory);
        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            var macosDir = libDirectory;
            if (macosDir.EndsWith($"{Path.DirectorySeparatorChar}lib", StringComparison.OrdinalIgnoreCase))
            {
                macosDir = Path.GetDirectoryName(macosDir) ?? macosDir;
            }

            var dataPath = Path.Combine(macosDir, "share");
            if (Directory.Exists(dataPath))
            {
                Environment.SetEnvironmentVariable("VLC_DATA_PATH", dataPath);
            }

            PrependEnvPath("DYLD_LIBRARY_PATH", libDirectory);
            PrependEnvPath("DYLD_FALLBACK_LIBRARY_PATH", libDirectory);
        }
    }

    private static void PrependEnvPath(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var current = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(current))
        {
            Environment.SetEnvironmentVariable(key, value);
            return;
        }

        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(key, $"{value}{Path.PathSeparator}{current}");
    }

    private static string BuildEnvPath(string key, string value)
    {
        var current = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(current))
        {
            return value;
        }

        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
        {
            return current;
        }

        return $"{value}{Path.PathSeparator}{current}";
    }

    private static void SetLibVlcOverride(string? libPath)
    {
        if (string.IsNullOrWhiteSpace(libPath))
        {
            return;
        }

        Environment.SetEnvironmentVariable("VLC_LIB_PATH", Path.GetDirectoryName(libPath));
        Environment.SetEnvironmentVariable("LIBVLC_PATH", libPath);
        Environment.SetEnvironmentVariable("LIBVLC_DLL", libPath);
        Environment.SetEnvironmentVariable("LIBVLC_LIB", libPath);
    }

    private static void PreloadLibVlcLibraries(string? libPath)
    {
        if (string.IsNullOrWhiteSpace(libPath))
        {
            return;
        }

        var corePath = GetLibVlcCorePath(libPath);
        if (OperatingSystem.IsMacOS())
        {
            if (!string.IsNullOrWhiteSpace(corePath) && _preloadedCoreHandle == IntPtr.Zero)
            {
                _preloadedCoreHandle = TryDlopenGlobal(corePath);
            }

            if (_preloadedLibHandle == IntPtr.Zero)
            {
                _preloadedLibHandle = TryDlopenGlobal(libPath);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(corePath) && _preloadedCoreHandle == IntPtr.Zero)
            {
                NativeLibrary.TryLoad(corePath, out _preloadedCoreHandle);
            }

            if (_preloadedLibHandle == IntPtr.Zero)
            {
                NativeLibrary.TryLoad(libPath, out _preloadedLibHandle);
            }
        }
    }

    private static void EnsureLibVlcResolver(string? libPath)
    {
        if (_resolverConfigured || string.IsNullOrWhiteSpace(libPath))
        {
            return;
        }

        _resolverConfigured = true;
        _resolverPath = libPath;
        _resolverCorePath = GetLibVlcCorePath(libPath);
        if (_resolverHandle == IntPtr.Zero && _preloadedLibHandle != IntPtr.Zero)
        {
            _resolverHandle = _preloadedLibHandle;
        }

        if (_resolverCoreHandle == IntPtr.Zero && _preloadedCoreHandle != IntPtr.Zero)
        {
            _resolverCoreHandle = _preloadedCoreHandle;
        }
        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, ResolveLibVlc);
    }

    private static IntPtr ResolveLibVlc(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.IsNullOrWhiteSpace(_resolverPath))
        {
            return IntPtr.Zero;
        }

        if (!IsLibVlcName(libraryName))
        {
            return IntPtr.Zero;
        }

        if (libraryName.StartsWith("libvlccore", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_resolverCorePath))
            {
                return IntPtr.Zero;
            }

            if (_resolverCoreHandle != IntPtr.Zero)
            {
                return _resolverCoreHandle;
            }

            if (_resolverCoreHandle == IntPtr.Zero)
            {
                _resolverCoreHandle = NativeLibrary.Load(_resolverCorePath);
            }

            return _resolverCoreHandle;
        }

        if (_resolverHandle != IntPtr.Zero)
        {
            return _resolverHandle;
        }

        if (_resolverHandle == IntPtr.Zero)
        {
            _resolverHandle = NativeLibrary.Load(_resolverPath);
        }

        return _resolverHandle;
    }

    private static bool IsLibVlcName(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        if (libraryName.Equals("libvlc", StringComparison.OrdinalIgnoreCase)
            || libraryName.Equals("libvlc.dylib", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (libraryName.Equals("libvlccore", StringComparison.OrdinalIgnoreCase)
            || libraryName.Equals("libvlccore.dylib", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? GetLibVlcCorePath(string libPath)
    {
        var directory = Path.GetDirectoryName(libPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, "libvlccore.dylib");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        candidate = Path.Combine(directory, "libvlccore.9.dylib");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return null;
    }

    private static IntPtr TryDlopenGlobal(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return IntPtr.Zero;
        }

        var handle = dlopen(path, RtldNow | RtldGlobal);
        if (handle == IntPtr.Zero)
        {
            var errorPtr = dlerror();
            var error = errorPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(errorPtr) : null;
            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.WriteLine($"[LibVLC] dlopen failed for {path}: {error}");
            }
        }

        return handle;
    }

    private static string? GetLastLibVlcError()
    {
        try
        {
            var handle = _resolverHandle != IntPtr.Zero ? _resolverHandle : _preloadedLibHandle;
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            if (!NativeLibrary.TryGetExport(handle, "libvlc_errmsg", out var proc))
            {
                return null;
            }

            var del = Marshal.GetDelegateForFunctionPointer<LibVlcErrmsgDelegate>(proc);
            var ptr = del();
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LibVlcErrmsgDelegate();

    [DllImport("libdl.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("libdl.dylib")]
    private static extern IntPtr dlerror();

    private static string? GetLibVlcPath(string directory)
    {
        if (OperatingSystem.IsMacOS())
        {
            var candidate = Path.Combine(directory, "libvlc.dylib");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(directory, "libvlc.5.dylib");
            return File.Exists(candidate) ? candidate : null;
        }

        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(directory, "libvlc.dll");
            return File.Exists(candidate) ? candidate : null;
        }

        var linux = Path.Combine(directory, "libvlc.so");
        return File.Exists(linux) ? linux : null;
    }

    private static void AddUserBasePathCandidates(List<string> candidates, string basePath)
    {
        var trimmed = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        candidates.Add(trimmed);

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var contents = Path.Combine(trimmed, "Contents");
        var macos = Path.Combine(contents, "MacOS");
        candidates.Add(contents);
        candidates.Add(macos);
        candidates.Add(Path.Combine(macos, "lib"));
        candidates.Add(Path.Combine(trimmed, "lib"));

        if (trimmed.EndsWith($"{Path.DirectorySeparatorChar}Contents", StringComparison.OrdinalIgnoreCase))
        {
            var fromContents = Path.Combine(trimmed, "MacOS");
            candidates.Add(fromContents);
            candidates.Add(Path.Combine(fromContents, "lib"));
        }
    }

    private static string BuildNotFoundStatus(IEnumerable<string> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0)
        {
            return "VLC non trovato: installa VLC per la preview video";
        }

        var lines = string.Join(Environment.NewLine, list.Select(path => $"- {path}"));
        var configHint = string.IsNullOrWhiteSpace(_userBasePath)
            ? string.Empty
            : $"{Environment.NewLine}Percorso configurato: {_userBasePath}";
        return $"VLC non trovato: installa VLC per la preview video.{configHint}{Environment.NewLine}Percorsi cercati:{Environment.NewLine}{lines}";
    }

    private static LibVLC CreateLibVlc(string? pluginPath, out string? usedPluginPath)
    {
        usedPluginPath = null;

        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginPath);
            usedPluginPath = pluginPath;
        }

        return new LibVLC(
            "--quiet",
            "--no-stats",
            "--no-video-title-show");
    }

    private static bool TryResolveLibVlcPaths(string? basePath, out string libDirectory, out string? pluginPath, out string? dataPath)
    {
        libDirectory = string.Empty;
        pluginPath = null;
        dataPath = null;

        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        var candidates = new List<string>();
        AddUserBasePathCandidates(candidates, basePath);
        var directory = ResolveLibVlcDirectory(candidates);
        if (directory == null)
        {
            return false;
        }

        libDirectory = directory;
        pluginPath = ResolvePluginPath(directory);
        dataPath = ResolveDataPath(directory);
        return true;
    }

    private static bool IsEnvironmentConfigured(string libDirectory, string? pluginPath, string? dataPath)
    {
        if (!IsSamePath(Environment.GetEnvironmentVariable("VLC_LIB_PATH"), libDirectory))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pluginPath)
            && !IsSamePath(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH"), pluginPath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(dataPath)
            && !IsSamePath(Environment.GetEnvironmentVariable("VLC_DATA_PATH"), dataPath))
        {
            return false;
        }

        return true;
    }

    private static bool IsSamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveDataPath(string libDirectory)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var macosDir = libDirectory;
        if (macosDir.EndsWith($"{Path.DirectorySeparatorChar}lib", StringComparison.OrdinalIgnoreCase))
        {
            macosDir = Path.GetDirectoryName(macosDir) ?? macosDir;
        }

        var dataPath = Path.Combine(macosDir, "share");
        return Directory.Exists(dataPath) ? dataPath : null;
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
            var macosFromApp = Path.Combine(path, "Contents", "MacOS");
            if (Directory.Exists(macosFromApp))
            {
                var macosLib = Path.Combine(macosFromApp, "lib");
                if (File.Exists(Path.Combine(macosFromApp, "libvlc.dylib")))
                {
                    directory = macosFromApp;
                    return true;
                }

                if (File.Exists(Path.Combine(macosLib, "libvlc.dylib")))
                {
                    directory = macosLib;
                    return true;
                }
            }

            if (path.EndsWith($"{Path.DirectorySeparatorChar}Contents", StringComparison.OrdinalIgnoreCase))
            {
                var macosDir = Path.Combine(path, "MacOS");
                var macosLib = Path.Combine(macosDir, "lib");
                if (File.Exists(Path.Combine(macosDir, "libvlc.dylib")))
                {
                    directory = macosDir;
                    return true;
                }

                if (File.Exists(Path.Combine(macosLib, "libvlc.dylib")))
                {
                    directory = macosLib;
                    return true;
                }
            }

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
