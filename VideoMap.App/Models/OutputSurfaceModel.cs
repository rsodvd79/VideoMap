using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoMap.App.Models;

public partial class OutputSurfaceModel : ObservableObject
{
    private string _name = "Output";
    private double _width = 1920;
    private double _height = 1080;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public ObservableCollection<Guid> PolygonIds { get; set; } = new();

    public void Normalize()
    {
        if (Id == Guid.Empty)
        {
            Id = Guid.NewGuid();
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Output";
        }

        if (Width <= 0)
        {
            Width = 1920;
        }

        if (Height <= 0)
        {
            Height = 1080;
        }

        PolygonIds ??= new ObservableCollection<Guid>();
    }
}
