using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoMap.App.Models;

namespace VideoMap.App.Services;

public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Save(ProjectModel project, string path)
    {
        var directory = GetProjectDirectory(path);
        var serializable = CreateSerializableProject(project, directory);
        var json = JsonSerializer.Serialize(serializable, Options);
        File.WriteAllText(path, json);
    }

    public static ProjectModel Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ProjectModel>(json, Options);

        if (project == null)
        {
            throw new InvalidDataException("Project file is empty or invalid.");
        }

        project.Normalize();
        ResolveMediaPaths(project, GetProjectDirectory(path));
        return project;
    }

    private static ProjectModel CreateSerializableProject(ProjectModel project, string directory)
    {
        var clone = new ProjectModel
        {
            Name = project.Name,
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            Polygons = new(),
            Scenes = new(),
            Outputs = new(),
        };

        foreach (var polygon in project.Polygons)
        {
            var mediaPath = polygon.MediaPath;
            if (!string.IsNullOrWhiteSpace(mediaPath) && Path.IsPathRooted(mediaPath))
            {
                mediaPath = Path.GetRelativePath(directory, mediaPath);
            }

            var clonedPolygon = new PolygonModel
            {
                Id = polygon.Id,
                Name = polygon.Name,
                MediaPath = mediaPath,
                MediaType = polygon.MediaType,
            };

            foreach (var point in polygon.Points)
            {
                clonedPolygon.Points.Add(new PointModel(point.X, point.Y));
            }

            clone.Polygons.Add(clonedPolygon);
        }

        foreach (var scene in project.Scenes)
        {
            var clonedScene = new SceneModel
            {
                Id = scene.Id,
                Name = scene.Name,
                DurationSeconds = scene.DurationSeconds,
                ActivePolygonIds = new(),
            };

            foreach (var polygonId in scene.ActivePolygonIds)
            {
                clonedScene.ActivePolygonIds.Add(polygonId);
            }

            clone.Scenes.Add(clonedScene);
        }

        foreach (var output in project.Outputs)
        {
            var clonedOutput = new OutputSurfaceModel
            {
                Id = output.Id,
                Name = output.Name,
                Width = output.Width,
                Height = output.Height,
                PolygonIds = new(),
            };

            foreach (var polygonId in output.PolygonIds)
            {
                clonedOutput.PolygonIds.Add(polygonId);
            }

            clone.Outputs.Add(clonedOutput);
        }

        return clone;
    }

    private static void ResolveMediaPaths(ProjectModel project, string directory)
    {
        foreach (var polygon in project.Polygons)
        {
            if (string.IsNullOrWhiteSpace(polygon.MediaPath))
            {
                continue;
            }

            var mediaPath = polygon.MediaPath;
            if (!Path.IsPathRooted(mediaPath))
            {
                mediaPath = Path.GetFullPath(Path.Combine(directory, mediaPath));
            }

            polygon.MediaPath = mediaPath;

            if (polygon.MediaType == MediaType.None)
            {
                polygon.MediaType = MediaClassifier.GetMediaType(mediaPath);
            }
        }
    }

    private static string GetProjectDirectory(string path)
    {
        return Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
    }
}
