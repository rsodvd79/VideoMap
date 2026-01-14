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
    private string _videoStatus = "Nessun video assegnato";

    public PreviewWindowViewModel(ProjectModel project, LibVLC? libVlc)
    {
        _project = project;
        _libVlc = libVlc;
        _project.Polygons.CollectionChanged += OnPolygonsChanged;

        foreach (var polygon in _project.Polygons)
        {
            AttachPolygon(polygon);
        }

        UpdateVideoStatus();
        UpdateVideoSoloState();
    }

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
        UpdateVideoStatus();
        UpdateVideoSoloState();
    }

    public void Dispose()
    {
        _project.Polygons.CollectionChanged -= OnPolygonsChanged;

        foreach (var polygon in _project.Polygons)
        {
            DetachPolygon(polygon);
        }

        foreach (var layer in VideoLayers.ToList())
        {
            DetachVideoLayer(layer);
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
            VideoStatus = "LibVLC non disponibile: installa VLC per la preview video";
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
