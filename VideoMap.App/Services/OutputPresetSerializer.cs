using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoMap.App.Models;

namespace VideoMap.App.Services;

public static class OutputPresetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Export(ProjectModel project, string path)
    {
        var preset = BuildPreset(project, path);
        var json = JsonSerializer.Serialize(preset, Options);
        File.WriteAllText(path, json);
    }

    private static OutputPresetModel BuildPreset(ProjectModel project, string presetPath)
    {
        var directory = Path.GetDirectoryName(presetPath) ?? Directory.GetCurrentDirectory();
        var polygons = project.Polygons.Select(p => new OutputPresetPolygon
        {
            Id = p.Id,
            Name = p.Name,
            Points = p.Points.Select(pt => new PointModel(pt.X, pt.Y)).ToList(),
            MediaPath = MakeRelativeIfPossible(directory, p.MediaPath),
            MediaType = p.MediaType,
            Order = p.Order,
        }).ToList();

        var outputs = project.Outputs.Select(o => new OutputPresetOutput
        {
            Id = o.Id,
            Name = o.Name,
            Width = o.Width,
            Height = o.Height,
            PolygonIds = o.PolygonIds.ToList(),
        }).ToList();

        return new OutputPresetModel
        {
            ProjectName = project.Name,
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            Polygons = polygons,
            Outputs = outputs,
        };
    }

    private static string? MakeRelativeIfPossible(string directory, string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return mediaPath;
        }

        if (!Path.IsPathRooted(mediaPath))
        {
            return mediaPath;
        }

        return Path.GetRelativePath(directory, mediaPath);
    }
}

public sealed class OutputPresetModel
{
    public string ProjectName { get; set; } = string.Empty;
    public double CanvasWidth { get; set; }
    public double CanvasHeight { get; set; }
    public List<OutputPresetPolygon> Polygons { get; set; } = new();
    public List<OutputPresetOutput> Outputs { get; set; } = new();
}

public sealed class OutputPresetPolygon
{
    public System.Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<PointModel> Points { get; set; } = new();
    public string? MediaPath { get; set; }
    public MediaType MediaType { get; set; }
    public int Order { get; set; }
}

public sealed class OutputPresetOutput
{
    public System.Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Width { get; set; }
    public double Height { get; set; }
    public List<System.Guid> PolygonIds { get; set; } = new();
}
