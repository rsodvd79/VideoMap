using System;
using System.Collections.Generic;
using System.IO;
using VideoMap.App.Models;

namespace VideoMap.App.Services;

public static class ProjectAssetManager
{
    public static AssetCopyResult ConsolidateAssets(ProjectModel project, string projectFilePath)
    {
        var projectDirectory = GetProjectDirectory(projectFilePath);
        var assetsDirectory = Path.Combine(projectDirectory, "Assets");
        Directory.CreateDirectory(assetsDirectory);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new AssetCopyResult();

        foreach (var polygon in project.Polygons)
        {
            if (string.IsNullOrWhiteSpace(polygon.MediaPath))
            {
                continue;
            }

            if (polygon.IsMediaMissing)
            {
                result.Missing++;
                continue;
            }

            var absolutePath = GetAbsoluteMediaPath(projectDirectory, polygon.MediaPath);
            if (absolutePath == null || !File.Exists(absolutePath))
            {
                result.Missing++;
                continue;
            }

            if (map.TryGetValue(absolutePath, out var mappedPath))
            {
                polygon.MediaPath = mappedPath;
                continue;
            }

            if (IsInsideDirectory(absolutePath, assetsDirectory))
            {
                polygon.MediaPath = absolutePath;
                result.Skipped++;
                map[absolutePath] = absolutePath;
                continue;
            }

            var destinationPath = EnsureUniquePath(assetsDirectory, Path.GetFileName(absolutePath));
            File.Copy(absolutePath, destinationPath, overwrite: false);
            result.Copied++;

            map[absolutePath] = destinationPath;
            polygon.MediaPath = destinationPath;
        }

        return result;
    }

    public static int RelinkMissing(ProjectModel project, string rootFolder)
    {
        if (!Directory.Exists(rootFolder))
        {
            return 0;
        }

        var fileLookup = BuildFileLookup(rootFolder);
        var relinked = 0;

        foreach (var polygon in project.Polygons)
        {
            if (string.IsNullOrWhiteSpace(polygon.MediaPath) || !polygon.IsMediaMissing)
            {
                continue;
            }

            var fileName = Path.GetFileName(polygon.MediaPath);
            if (!fileLookup.TryGetValue(fileName, out var resolvedPath))
            {
                continue;
            }

            polygon.MediaPath = resolvedPath;
            if (polygon.MediaType == MediaType.None)
            {
                polygon.MediaType = MediaClassifier.GetMediaType(resolvedPath);
            }

            relinked++;
        }

        return relinked;
    }

    private static Dictionary<string, string> BuildFileLookup(string rootFolder)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(rootFolder, "*.*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!lookup.ContainsKey(name))
            {
                lookup[name] = file;
            }
        }

        return lookup;
    }

    private static string GetProjectDirectory(string path)
    {
        return Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
    }

    private static string? GetAbsoluteMediaPath(string projectDirectory, string mediaPath)
    {
        if (Path.IsPathRooted(mediaPath))
        {
            return mediaPath;
        }

        return Path.GetFullPath(Path.Combine(projectDirectory, mediaPath));
    }

    private static string EnsureUniquePath(string assetsDirectory, string fileName)
    {
        var targetPath = Path.Combine(assetsDirectory, fileName);
        if (!File.Exists(targetPath))
        {
            return targetPath;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidate = Path.Combine(assetsDirectory, $"{nameWithoutExtension}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AssetCopyResult
{
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Missing { get; set; }
}
