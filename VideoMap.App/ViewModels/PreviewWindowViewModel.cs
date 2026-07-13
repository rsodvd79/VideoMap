using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using LibVLCSharp.Shared;
using VideoMap.App.Models;
using AppMediaType = VideoMap.App.Models.MediaType;

namespace VideoMap.App.ViewModels;

public partial class PreviewWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectModel _project;
    private readonly LibVLC? _libVlc;
    private readonly string? _libVlcStatus;
    private OutputSurfaceModel? _selectedOutput;
    private string _videoStatus = "Nessun video assegnato";

    public PreviewWindowViewModel()
        : this(ProjectModel.CreateDefault(), null, null)
    {
    }

    public PreviewWindowViewModel(ProjectModel project, LibVLC? libVlc, string? libVlcStatus, OutputSurfaceModel? initialOutput = null)
    {
        _project = project;
        _libVlc = libVlc;
        _libVlcStatus = libVlcStatus;
        _project.Polygons.CollectionChanged += OnPolygonsChanged;
        _project.Outputs.CollectionChanged += OnOutputsChanged;

        foreach (var polygon in _project.Polygons)
        {
            AttachPolygon(polygon);
        }

        SelectedOutput = initialOutput ?? _project.Outputs.FirstOrDefault();
        UpdateVideoStatus();
        UpdateVideoSoloState();
    }

    public ObservableCollection<OutputSurfaceModel> Outputs => _project.Outputs;

    public OutputSurfaceModel? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (ReferenceEquals(_selectedOutput, value))
            {
                return;
            }

            var previous = _selectedOutput;
            if (SetProperty(ref _selectedOutput, value))
            {
                if (previous != null)
                {
                    previous.PolygonIds.CollectionChanged -= OnOutputPolygonIdsChanged;
                }

                if (_selectedOutput != null)
                {
                    _selectedOutput.PolygonIds.CollectionChanged += OnOutputPolygonIdsChanged;
                }

                OnPropertyChanged(nameof(SurfaceWidth));
                OnPropertyChanged(nameof(SurfaceHeight));
                ReapplyOutputVisibility();
            }
        }
    }

    public double SurfaceWidth => _selectedOutput != null && _selectedOutput.Width > 0 ? _selectedOutput.Width : _project.CanvasWidth;

    public double SurfaceHeight => _selectedOutput != null && _selectedOutput.Height > 0 ? _selectedOutput.Height : _project.CanvasHeight;

    public ProjectModel Project => _project;

    public ObservableCollection<PolygonModel> Polygons => _project.Polygons;

    public ObservableCollection<VideoLayerViewModel> VideoLayers { get; } = new();

    public bool HasPolygons => _project.Polygons.Count > 0;
    public bool HasNoPolygons => !HasPolygons;

    public bool HasVideos => VideoLayers.Any(layer => layer.HasVideo);
    public bool HasNoVideos => !HasVideos;

    public string VideoStatus
    {
        get => _videoStatus;
        private set => SetProperty(ref _videoStatus, value);
    }

    private void OnPolygonsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (PolygonModel polygon in e.OldItems)
            {
                DetachPolygon(polygon);
            }
        }

        if (e.NewItems != null)
        {
            foreach (PolygonModel polygon in e.NewItems)
            {
                AttachPolygon(polygon);
            }
        }

        OnPropertyChanged(nameof(HasPolygons));
        OnPropertyChanged(nameof(HasNoPolygons));
        if (e.Action == NotifyCollectionChangedAction.Move || e.Action == NotifyCollectionChangedAction.Reset)
        {
            SyncVideoLayerOrder();
        }
        ReapplyOutputVisibility();
        UpdateVideoStatus();
        UpdateVideoSoloState();
    }

    public void Dispose()
    {
        _project.Polygons.CollectionChanged -= OnPolygonsChanged;
        _project.Outputs.CollectionChanged -= OnOutputsChanged;

        if (_selectedOutput != null)
        {
            _selectedOutput.PolygonIds.CollectionChanged -= OnOutputPolygonIdsChanged;
        }

        foreach (var polygon in _project.Polygons)
        {
            DetachPolygon(polygon);
        }

        foreach (var layer in VideoLayers.ToList())
        {
            DetachVideoLayer(layer);
        }
    }

    private void OnOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_selectedOutput != null && !_project.Outputs.Contains(_selectedOutput))
        {
            SelectedOutput = _project.Outputs.FirstOrDefault();
        }
    }

    private void OnOutputPolygonIdsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReapplyOutputVisibility();
    }

    private void ReapplyOutputVisibility()
    {
        var ids = _selectedOutput == null
            ? null
            : new System.Collections.Generic.HashSet<Guid>(_selectedOutput.PolygonIds);

        foreach (var polygon in _project.Polygons)
        {
            polygon.IsOutputVisible = ids == null || ids.Contains(polygon.Id);
        }
    }

    private void AttachPolygon(PolygonModel polygon)
    {
        polygon.PropertyChanged += OnPolygonPropertyChanged;
        AddVideoLayer(polygon);
    }

    private void DetachPolygon(PolygonModel polygon)
    {
        polygon.PropertyChanged -= OnPolygonPropertyChanged;
        RemoveVideoLayer(polygon);
    }

    private void OnPolygonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolygonModel.MediaPath)
            || e.PropertyName == nameof(PolygonModel.MediaType)
            || e.PropertyName == nameof(PolygonModel.IsMediaMissing))
        {
            UpdateVideoStatus();
            return;
        }

        if (e.PropertyName == nameof(PolygonModel.IsVideoSolo))
        {
            UpdateVideoSoloState();
        }
    }

    private void UpdateVideoStatus()
    {
        if (_libVlc == null)
        {
            VideoStatus = string.IsNullOrWhiteSpace(_libVlcStatus)
                ? "LibVLC non disponibile: installa VLC per la preview video"
                : _libVlcStatus;
            OnPropertyChanged(nameof(HasVideos));
            OnPropertyChanged(nameof(HasNoVideos));
            return;
        }

        var activeVideos = VideoLayers.Count(layer => layer.IsVisible);
        var missingVideos = _project.Polygons.Count(p =>
            p.MediaType == AppMediaType.Video
            && p.IsMediaMissing
            && !string.IsNullOrWhiteSpace(p.MediaPath));

        if (activeVideos == 0)
        {
            VideoStatus = missingVideos > 0
                ? $"Video mancanti: {missingVideos}"
                : "Nessun video assegnato";
        }
        else
        {
            var suffix = missingVideos > 0 ? $" (mancanti: {missingVideos})" : string.Empty;
            VideoStatus = $"Video attivi: {activeVideos}{suffix}";
        }

        OnPropertyChanged(nameof(HasVideos));
        OnPropertyChanged(nameof(HasNoVideos));
    }

    private void AddVideoLayer(PolygonModel polygon)
    {
        if (_libVlc == null)
        {
            return;
        }

        var layer = new VideoLayerViewModel(_libVlc, polygon);
        layer.PropertyChanged += OnVideoLayerPropertyChanged;
        VideoLayers.Add(layer);
        UpdateVideoStatus();
    }

    private void RemoveVideoLayer(PolygonModel polygon)
    {
        var layer = VideoLayers.FirstOrDefault(candidate => candidate.Polygon == polygon);
        if (layer == null)
        {
            return;
        }

        DetachVideoLayer(layer);
        UpdateVideoStatus();
    }

    private void DetachVideoLayer(VideoLayerViewModel layer)
    {
        layer.PropertyChanged -= OnVideoLayerPropertyChanged;
        layer.Dispose();
        VideoLayers.Remove(layer);
    }

    private void OnVideoLayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoLayerViewModel.HasVideo)
            || e.PropertyName == nameof(VideoLayerViewModel.IsVisible))
        {
            UpdateVideoStatus();
        }
    }

    private void SyncVideoLayerOrder()
    {
        if (VideoLayers.Count == 0)
        {
            return;
        }

        var ordered = _project.Polygons
            .Select(polygon => VideoLayers.FirstOrDefault(layer => layer.Polygon == polygon))
            .Where(layer => layer != null)
            .Cast<VideoLayerViewModel>()
            .ToList();

        if (ordered.Count != VideoLayers.Count)
        {
            return;
        }

        VideoLayers.Clear();
        foreach (var layer in ordered)
        {
            VideoLayers.Add(layer);
        }
    }

    private void UpdateVideoSoloState()
    {
        var soloActive = _project.Polygons.Any(p => p.IsVideoSolo);
        foreach (var layer in VideoLayers)
        {
            var suppress = soloActive && !layer.Polygon.IsVideoSolo;
            layer.SetSuppressed(suppress);
        }
    }
}
