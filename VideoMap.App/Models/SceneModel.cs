using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoMap.App.Models;

public partial class SceneModel : ObservableObject
{
    private string _name = "Scene";
    private double _durationSeconds = 5;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set => SetProperty(ref _durationSeconds, value);
    }

    public ObservableCollection<Guid> ActivePolygonIds { get; set; } = new();

    public void Normalize()
    {
        if (Id == Guid.Empty)
        {
            Id = Guid.NewGuid();
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Scene";
        }

        if (DurationSeconds <= 0)
        {
            DurationSeconds = 5;
        }

        ActivePolygonIds ??= new ObservableCollection<Guid>();
    }
}
