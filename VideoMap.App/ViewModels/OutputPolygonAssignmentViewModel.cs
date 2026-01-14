using System;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoMap.App.Models;

namespace VideoMap.App.ViewModels;

public partial class OutputPolygonAssignmentViewModel : ObservableObject, IDisposable
{
    private readonly OutputSurfaceModel _output;
    private readonly PolygonModel _polygon;
    private readonly Guid _polygonId;
    private readonly Action? _onChanged;
    private bool _isAssigned;
    private string _name;

    public OutputPolygonAssignmentViewModel(OutputSurfaceModel output, PolygonModel polygon, Action? onChanged)
    {
        _output = output;
        _polygon = polygon;
        _polygonId = polygon.Id;
        _onChanged = onChanged;
        _name = polygon.Name;
        _isAssigned = output.PolygonIds.Contains(_polygonId);
        _polygon.PropertyChanged += OnPolygonPropertyChanged;
    }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public bool IsAssigned
    {
        get => _isAssigned;
        set
        {
            if (!SetProperty(ref _isAssigned, value))
            {
                return;
            }

            if (_isAssigned)
            {
                if (!_output.PolygonIds.Contains(_polygonId))
                {
                    _output.PolygonIds.Add(_polygonId);
                }
            }
            else
            {
                _output.PolygonIds.Remove(_polygonId);
            }

            _onChanged?.Invoke();
        }
    }

    private void OnPolygonPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolygonModel.Name))
        {
            Name = _polygon.Name;
        }
    }

    public void Dispose()
    {
        _polygon.PropertyChanged -= OnPolygonPropertyChanged;
    }
}
