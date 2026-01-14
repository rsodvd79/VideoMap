using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoMap.App.Models;

public partial class PointModel : ObservableObject
{
    private double _x;
    private double _y;

    public PointModel()
    {
    }

    public PointModel(double x, double y)
    {
        _x = x;
        _y = y;
    }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }
}
