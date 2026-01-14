using System;
using System.Collections.Generic;
using System.IO;
using VideoMap.App.Models;

namespace VideoMap.App.Services;

public static class MediaClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".tiff",
        ".webp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".mkv",
        ".avi",
        ".webm",
        ".mpeg",
        ".mpg",
    };

    public static MediaType GetMediaType(string path)
    {
        var extension = Path.GetExtension(path);

        if (ImageExtensions.Contains(extension))
        {
            return MediaType.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaType.Video;
        }

        return MediaType.None;
    }
}
