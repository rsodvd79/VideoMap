using System;
using System.ComponentModel;
using Avalonia.Media;
using LibVLCSharp.Shared;
using VideoMap.App.Models;
using AppMediaType = VideoMap.App.Models.MediaType;

namespace VideoMap.App.ViewModels;

public partial class VideoLayerViewModel : ViewModelBase, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly PolygonModel _polygon;
    private MediaPlayer? _mediaPlayer;
    private string? _currentPath;
    private bool _hasVideo;
    private bool _isSuppressed;

    public VideoLayerViewModel(LibVLC libVlc, PolygonModel polygon)
    {
        _libVlc = libVlc;
        _polygon = polygon;
        _mediaPlayer = new MediaPlayer(_libVlc);
        _polygon.PropertyChanged += OnPolygonPropertyChanged;
        UpdateFromPolygon();
    }

    public PolygonModel Polygon => _polygon;

    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public Geometry? ClipGeometry => _polygon.ClipGeometry;

    public bool HasVideo
    {
        get => _hasVideo;
        private set => SetProperty(ref _hasVideo, value);
    }

    public bool IsVisible => HasVideo && !_isSuppressed && _polygon.IsSceneVisible;

    public void Dispose()
    {
        _polygon.PropertyChanged -= OnPolygonPropertyChanged;
        StopPlayback();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
    }

    public void Play()
    {
        if (!IsVisible || _mediaPlayer == null)
        {
            return;
        }

        var path = _polygon.MediaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!_mediaPlayer.IsPlaying)
        {
            StartPlayback(path);
            UpdateMute();
        }
    }

    public void Pause()
    {
        _mediaPlayer?.Pause();
    }

    public void Stop()
    {
        StopPlayback();
    }

    public void Seek(double position)
    {
        if (_mediaPlayer == null || !IsVisible)
        {
            return;
        }

        _mediaPlayer.Position = (float)position;
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_isSuppressed == suppressed)
        {
            return;
        }

        _isSuppressed = suppressed;
        OnPropertyChanged(nameof(IsVisible));

        if (_isSuppressed)
        {
            StopPlayback();
        }
        else
        {
            UpdateFromPolygon();
        }
    }

    private void OnPolygonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolygonModel.ClipGeometry))
        {
            OnPropertyChanged(nameof(ClipGeometry));
            return;
        }

        if (e.PropertyName == nameof(PolygonModel.MediaPath)
            || e.PropertyName == nameof(PolygonModel.MediaType)
            || e.PropertyName == nameof(PolygonModel.IsMediaMissing))
        {
            UpdateFromPolygon();
            return;
        }

        if (e.PropertyName == nameof(PolygonModel.IsSceneVisible))
        {
            OnPropertyChanged(nameof(IsVisible));
            if (!_polygon.IsSceneVisible)
            {
                StopPlayback();
            }
            else
            {
                UpdateFromPolygon();
            }
            return;
        }

        if (e.PropertyName == nameof(PolygonModel.IsVideoMuted))
        {
            UpdateMute();
            return;
        }

        if (e.PropertyName == nameof(PolygonModel.IsVideoLoop))
        {
            RestartPlaybackIfNeeded();
        }
    }

    private void UpdateFromPolygon()
    {
        var path = _polygon.MediaPath;
        var hasVideo = _polygon.MediaType == AppMediaType.Video
            && !string.IsNullOrWhiteSpace(path)
            && !_polygon.IsMediaMissing;

        HasVideo = hasVideo;
        OnPropertyChanged(nameof(IsVisible));

        if (!hasVideo || _isSuppressed)
        {
            StopPlayback();
            return;
        }

        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            StartPlayback(path!);
        }

        UpdateMute();
    }

    private void StartPlayback(string path)
    {
        if (_mediaPlayer == null)
        {
            return;
        }

        _currentPath = path;
        using var media = new Media(_libVlc, path, FromType.FromPath);
        if (_polygon.IsVideoLoop)
        {
            media.AddOption(":input-repeat=65535");
        }
        _mediaPlayer.Play(media);
    }

    private void StopPlayback()
    {
        if (_mediaPlayer?.IsPlaying == true)
        {
            _mediaPlayer.Stop();
        }

        _currentPath = null;
    }

    private void UpdateMute()
    {
        if (_mediaPlayer == null)
        {
            return;
        }

        _mediaPlayer.Mute = _polygon.IsVideoMuted;
    }

    private void RestartPlaybackIfNeeded()
    {
        if (!HasVideo || _isSuppressed || _mediaPlayer == null)
        {
            return;
        }

        var path = _polygon.MediaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        StopPlayback();
        StartPlayback(path);
    }
}
