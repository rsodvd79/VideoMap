using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace VideoMap.App.Models;

public partial class PolygonModel : ObservableObject
{
    private string _name = "Poligono";
    private string? _mediaPath;
    private MediaType _mediaType = MediaType.None;
    private ObservableCollection<PointModel> _points = new();
    private bool _isMediaMissing;
    private Geometry? _clipGeometry;
    private Bitmap? _imageBitmap;
    private IBrush? _imageFill;
    private WriteableBitmap? _warpedBitmap;
    private Rect _bounds;
    private SKImage? _skImage;
    private string? _skImagePath;
    private int _order;
    private bool _isVideoSolo;
    private bool _isVideoMuted;
    private bool _isVideoLoop;
    private bool _isSceneVisible = true;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ObservableCollection<PointModel> Points
    {
        get => _points;
        set
        {
            if (_points == value)
            {
                return;
            }

            _points.CollectionChanged -= OnPointsChanged;
            DetachPointHandlers(_points);
            _points = value ?? new ObservableCollection<PointModel>();
            _points.CollectionChanged += OnPointsChanged;
            AttachPointHandlers(_points);
            SyncRenderPoints();
            OnPropertyChanged();
        }
    }

    private AvaloniaList<Point> _renderPoints = new();

    [JsonIgnore]
    public AvaloniaList<Point> RenderPoints
    {
        get => _renderPoints;
        private set => SetProperty(ref _renderPoints, value);
    }

    public string? MediaPath
    {
        get => _mediaPath;
        set
        {
            if (SetProperty(ref _mediaPath, value))
            {
                UpdateMediaMissing();
                UpdateImage();
            }
        }
    }

    public MediaType MediaType
    {
        get => _mediaType;
        set
        {
            if (SetProperty(ref _mediaType, value))
            {
                UpdateImage();
            }
        }
    }

    public bool IsMediaMissing
    {
        get => _isMediaMissing;
        private set => SetProperty(ref _isMediaMissing, value);
    }

    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    public bool IsVideoSolo
    {
        get => _isVideoSolo;
        set => SetProperty(ref _isVideoSolo, value);
    }

    public bool IsVideoMuted
    {
        get => _isVideoMuted;
        set => SetProperty(ref _isVideoMuted, value);
    }

    public bool IsVideoLoop
    {
        get => _isVideoLoop;
        set => SetProperty(ref _isVideoLoop, value);
    }

    public bool IsSceneVisible
    {
        get => _isSceneVisible;
        set
        {
            if (SetProperty(ref _isSceneVisible, value))
            {
                OnPropertyChanged(nameof(HasImageClip));
                OnPropertyChanged(nameof(IsWarpedVisible));
            }
        }
    }

    public PolygonModel()
    {
        _points.CollectionChanged += OnPointsChanged;
        AttachPointHandlers(_points);
        SyncRenderPoints();
    }

    [JsonIgnore]
    public Geometry? ClipGeometry
    {
        get => _clipGeometry;
        private set
        {
            if (SetProperty(ref _clipGeometry, value))
            {
                OnPropertyChanged(nameof(HasClip));
                OnPropertyChanged(nameof(HasImageClip));
            }
        }
    }

    [JsonIgnore]
    public Bitmap? ImageBitmap
    {
        get => _imageBitmap;
        private set => SetImageBitmap(value);
    }

    [JsonIgnore]
    public IBrush? ImageFill
    {
        get => _imageFill;
        private set => SetProperty(ref _imageFill, value);
    }

    [JsonIgnore]
    public WriteableBitmap? WarpedBitmap
    {
        get => _warpedBitmap;
        private set
        {
            if (ReferenceEquals(_warpedBitmap, value))
            {
                return;
            }

            var old = _warpedBitmap;
            _warpedBitmap = value;
            OnPropertyChanged(nameof(WarpedBitmap));
            OnPropertyChanged(nameof(HasWarpedImage));
            OnPropertyChanged(nameof(IsWarpedVisible));
            OnPropertyChanged(nameof(HasImageClip));
            old?.Dispose();
        }
    }

    [JsonIgnore]
    public bool HasWarpedImage => WarpedBitmap != null;

    [JsonIgnore]
    public bool IsWarpedVisible => HasWarpedImage && IsSceneVisible;

    [JsonIgnore]
    public Rect Bounds
    {
        get => _bounds;
        private set => SetProperty(ref _bounds, value);
    }

    [JsonIgnore]
    public bool HasImage => ImageBitmap != null;

    [JsonIgnore]
    public bool HasClip => ClipGeometry != null;

    [JsonIgnore]
    public bool HasImageClip => HasImage && HasClip && IsSceneVisible && !HasWarpedImage;

    public void Normalize()
    {
        if (Id == Guid.Empty)
        {
            Id = Guid.NewGuid();
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Poligono";
        }

        Points ??= new ObservableCollection<PointModel>();
        UpdateMediaMissing();
        UpdateImage();
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (PointModel point in e.OldItems)
            {
                point.PropertyChanged -= OnPointPropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (PointModel point in e.NewItems)
            {
                point.PropertyChanged += OnPointPropertyChanged;
            }
        }

        SyncRenderPoints();
    }

    private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SyncRenderPoints();
    }

    private void SyncRenderPoints()
    {
        var updated = new AvaloniaList<Point>();

        foreach (var point in Points)
        {
            updated.Add(new Point(point.X, point.Y));
        }

        RenderPoints = updated;
        UpdateBounds();
        UpdateClipGeometry();
        UpdateWarpedImage();
    }

    private void UpdateBounds()
    {
        if (RenderPoints.Count == 0)
        {
            Bounds = default;
            return;
        }

        var minX = RenderPoints[0].X;
        var maxX = RenderPoints[0].X;
        var minY = RenderPoints[0].Y;
        var maxY = RenderPoints[0].Y;

        foreach (var point in RenderPoints)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        var width = Math.Max(0, maxX - minX);
        var height = Math.Max(0, maxY - minY);
        Bounds = new Rect(minX, minY, width, height);
    }

    private void UpdateClipGeometry()
    {
        if (RenderPoints.Count < 3)
        {
            ClipGeometry = null;
            return;
        }

        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = RenderPoints[0],
            IsClosed = true,
            IsFilled = true,
        };

        for (var i = 1; i < RenderPoints.Count; i++)
        {
            figure.Segments!.Add(new LineSegment
            {
                Point = RenderPoints[i],
            });
        }

        geometry.Figures!.Add(figure);
        ClipGeometry = geometry;
    }

    private void AttachPointHandlers(ObservableCollection<PointModel> points)
    {
        foreach (var point in points)
        {
            point.PropertyChanged += OnPointPropertyChanged;
        }
    }

    private void DetachPointHandlers(ObservableCollection<PointModel> points)
    {
        foreach (var point in points)
        {
            point.PropertyChanged -= OnPointPropertyChanged;
        }
    }

    private void UpdateMediaMissing()
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
        {
            IsMediaMissing = false;
            return;
        }

        IsMediaMissing = !File.Exists(MediaPath);
    }

    private void UpdateImage()
    {
        if (MediaType != MediaType.Image || string.IsNullOrWhiteSpace(MediaPath) || IsMediaMissing)
        {
            ImageBitmap = null;
            UpdateWarpedImage();
            return;
        }

        try
        {
            ImageBitmap = new Bitmap(MediaPath);
        }
        catch
        {
            ImageBitmap = null;
        }

        UpdateWarpedImage();
    }

    private void SetImageBitmap(Bitmap? bitmap)
    {
        if (ReferenceEquals(_imageBitmap, bitmap))
        {
            return;
        }

        var old = _imageBitmap;
        _imageBitmap = bitmap;
        UpdateImageFill();
        OnPropertyChanged(nameof(ImageBitmap));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasImageClip));
        OnPropertyChanged(nameof(IsWarpedVisible));
        old?.Dispose();
    }

    private void UpdateImageFill()
    {
        if (_imageBitmap == null)
        {
            ImageFill = null;
            return;
        }

        ImageFill = new ImageBrush
        {
            Source = _imageBitmap,
            Stretch = Stretch.Fill,
        };
    }

    private void UpdateWarpedImage()
    {
        if (MediaType != MediaType.Image || string.IsNullOrWhiteSpace(MediaPath) || IsMediaMissing)
        {
            WarpedBitmap = null;
            return;
        }

        if (RenderPoints.Count != 4)
        {
            WarpedBitmap = null;
            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            WarpedBitmap = null;
            return;
        }

        var skImage = GetSkImage();
        if (skImage == null)
        {
            WarpedBitmap = null;
            return;
        }

        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height));

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var framebuffer = bitmap.Lock())
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, framebuffer.Address, framebuffer.RowBytes);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var points = RenderPoints;
            var positions = new[]
            {
                new SKPoint((float)(points[0].X - Bounds.X), (float)(points[0].Y - Bounds.Y)),
                new SKPoint((float)(points[1].X - Bounds.X), (float)(points[1].Y - Bounds.Y)),
                new SKPoint((float)(points[2].X - Bounds.X), (float)(points[2].Y - Bounds.Y)),
                new SKPoint((float)(points[3].X - Bounds.X), (float)(points[3].Y - Bounds.Y)),
            };

            var texCoords = new[]
            {
                new SKPoint(0, 0),
                new SKPoint(skImage.Width, 0),
                new SKPoint(skImage.Width, skImage.Height),
                new SKPoint(0, skImage.Height),
            };

            var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };

            using var paint = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
                Shader = SKShader.CreateImage(skImage, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp),
            };

            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texCoords, null, indices);
            canvas.DrawVertices(vertices, SKBlendMode.SrcOver, paint);
        }

        WarpedBitmap = bitmap;
    }

    private SKImage? GetSkImage()
    {
        if (string.Equals(_skImagePath, MediaPath, StringComparison.OrdinalIgnoreCase) && _skImage != null)
        {
            return _skImage;
        }

        _skImage?.Dispose();
        _skImage = null;
        _skImagePath = null;

        if (string.IsNullOrWhiteSpace(MediaPath) || !File.Exists(MediaPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(MediaPath);
            _skImage = SKImage.FromEncodedData(stream);
            _skImagePath = MediaPath;
        }
        catch
        {
            _skImage = null;
            _skImagePath = null;
        }

        return _skImage;
    }
}
