using System.Collections.ObjectModel;
using System.Linq;

namespace VideoMap.App.Models;

public class ProjectModel
{
    public string Name { get; set; } = "Senza nome";
    public double CanvasWidth { get; set; } = 1920;
    public double CanvasHeight { get; set; } = 1080;
    public ObservableCollection<PolygonModel> Polygons { get; set; } = new();
    public ObservableCollection<SceneModel> Scenes { get; set; } = new();
    public ObservableCollection<OutputSurfaceModel> Outputs { get; set; } = new();

    public static ProjectModel CreateDefault()
    {
        var project = new ProjectModel();
        project.EnsureDefaultOutput();
        project.EnsureDefaultScene();
        return project;
    }

    public void Normalize()
    {
        Polygons ??= new ObservableCollection<PolygonModel>();
        Scenes ??= new ObservableCollection<SceneModel>();
        Outputs ??= new ObservableCollection<OutputSurfaceModel>();

        EnsureDefaultOutput();
        EnsureDefaultScene();
        var ordered = Polygons.OrderBy(p => p.Order).ToList();
        Polygons = new ObservableCollection<PolygonModel>(ordered);

        for (var i = 0; i < Polygons.Count; i++)
        {
            var polygon = Polygons[i];
            polygon.Order = i;
            polygon.Normalize();
        }

        foreach (var scene in Scenes)
        {
            scene.Normalize();
        }

        foreach (var output in Outputs)
        {
            output.Normalize();
        }
    }

    private void EnsureDefaultOutput()
    {
        if (Outputs.Count > 0)
        {
            return;
        }

        Outputs.Add(new OutputSurfaceModel
        {
            Name = "Output 1",
            Width = CanvasWidth,
            Height = CanvasHeight,
        });
    }

    private void EnsureDefaultScene()
    {
        if (Scenes.Count > 0)
        {
            return;
        }

        Scenes.Add(new SceneModel
        {
            Name = "Scene 1",
            DurationSeconds = 5,
        });
    }
}
