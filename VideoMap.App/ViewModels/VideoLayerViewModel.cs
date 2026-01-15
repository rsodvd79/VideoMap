using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using SkiaSharp;
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
    private WriteableBitmap? _videoBitmap;
    private WriteableBitmap? _warpedVideoBitmap;
    private IntPtr _videoBuffer = IntPtr.Zero;
    private int _videoBufferSize;
    private int _videoWidth;
    private int _videoHeight;
    private int _videoPitch;
    private int _framePending;
    private long _frameCounter;
    private long _lastFrameLogTicks;
    private int _formatLogCount;
    private int _displayLogCount;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;

    public VideoLayerViewModel(LibVLC libVlc, PolygonModel polygon)
    {
        _libVlc = libVlc;
        _polygon = polygon;
        _mediaPlayer = new MediaPlayer(_libVlc);
        _lockCb = OnVideoLock;
        _unlockCb = OnVideoUnlock;
        _displayCb = OnVideoDisplay;
        _formatCb = OnVideoFormat;
        _cleanupCb = OnVideoCleanup;
        _mediaPlayer.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _mediaPlayer.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
        _polygon.PropertyChanged += OnPolygonPropertyChanged;
        UpdateFromPolygon();
    }

    public PolygonModel Polygon => _polygon;

    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public Geometry? ClipGeometry => _polygon.ClipGeometry;

    public Rect Bounds => _polygon.Bounds;

    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        private set
        {
            var old = _videoBitmap;
            if (SetProperty(ref _videoBitmap, value))
            {
                OnPropertyChanged(nameof(HasVideoBitmap));
                OnPropertyChanged(nameof(IsFrameVisible));
                OnPropertyChanged(nameof(IsClipVisible));
                old?.Dispose();
            }
        }
    }

    public bool HasVideoBitmap => VideoBitmap != null;

    public WriteableBitmap? WarpedVideoBitmap
    {
        get => _warpedVideoBitmap;
        private set
        {
            var old = _warpedVideoBitmap;
            if (SetProperty(ref _warpedVideoBitmap, value))
            {
                OnPropertyChanged(nameof(HasWarpedVideo));
                OnPropertyChanged(nameof(IsWarpedVisible));
                OnPropertyChanged(nameof(IsClipVisible));
                old?.Dispose();
            }
        }
    }

    public bool HasWarpedVideo => WarpedVideoBitmap != null;

    public bool HasVideo
    {
        get => _hasVideo;
        private set => SetProperty(ref _hasVideo, value);
    }

    public bool IsVisible => HasVideo && !_isSuppressed && _polygon.IsSceneVisible;

    public bool IsFrameVisible => IsVisible && HasVideoBitmap;

    public bool IsWarpedVisible => IsVisible && HasWarpedVideo;

    public bool IsClipVisible => IsFrameVisible && !HasWarpedVideo;

    public void Dispose()
    {
        _polygon.PropertyChanged -= OnPolygonPropertyChanged;
        StopPlayback();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        ReleaseVideoBuffer();
    }

    public void Play()
    {
        if (_mediaPlayer == null || !HasVideo)
        {
            return;
        }

        var path = _polygon.MediaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!_mediaPlayer.IsPlaying || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
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
            UpdateWarpedVideoFrame();
            return;
        }

        if (e.PropertyName == nameof(PolygonModel.Bounds))
        {
            OnPropertyChanged(nameof(Bounds));
            UpdateWarpedVideoFrame();
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
            OnPropertyChanged(nameof(IsFrameVisible));
            OnPropertyChanged(nameof(IsWarpedVisible));
            OnPropertyChanged(nameof(IsClipVisible));
            UpdateWarpedVideoFrame();
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
        OnPropertyChanged(nameof(IsFrameVisible));
        OnPropertyChanged(nameof(IsWarpedVisible));
        OnPropertyChanged(nameof(IsClipVisible));

        if (!hasVideo || _isSuppressed || !_polygon.IsSceneVisible)
        {
            StopPlayback();
            return;
        }

        if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)
            || _mediaPlayer?.IsPlaying != true)
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
        media.AddOption(":avcodec-hw=none");
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

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var chromaBytes = new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' };
        Marshal.Copy(chromaBytes, 0, chroma, chromaBytes.Length);

        var w = (int)width;
        var h = (int)height;
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        _videoPitch = w * 4;
        pitches = (uint)_videoPitch;
        lines = (uint)h;

        AllocateVideoBuffer(w, h, _videoPitch);
        LogVideo($"format {w}x{h} pitch={_videoPitch} path={_currentPath ?? "(null)"}", ref _formatLogCount, 1);
        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        ReleaseVideoBuffer();
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        if (_videoBuffer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.WriteIntPtr(planes, _videoBuffer);
        return IntPtr.Zero;
    }

    private void OnVideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_videoBuffer == IntPtr.Zero)
        {
            return;
        }

        LogVideoFrame();
        if (Interlocked.Exchange(ref _framePending, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(UpdateVideoFrame, DispatcherPriority.Normal);
    }

    private void AllocateVideoBuffer(int width, int height, int pitch)
    {
        ReleaseVideoBuffer();
        _videoWidth = width;
        _videoHeight = height;
        _videoPitch = pitch;
        _videoBufferSize = pitch * height;
        _videoBuffer = Marshal.AllocHGlobal(_videoBufferSize);
    }

    private void ReleaseVideoBuffer()
    {
        if (_videoBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = IntPtr.Zero;
        }

        _videoBufferSize = 0;
        _videoWidth = 0;
        _videoHeight = 0;
        _videoPitch = 0;
        VideoBitmap = null;
    }

    private void UpdateVideoFrame()
    {
        try
        {
            if (_videoBuffer == IntPtr.Zero || _videoWidth <= 0 || _videoHeight <= 0)
            {
                LogVideo("frame skipped (bitmap/buffer missing)", ref _displayLogCount, 1);
                return;
            }

            var frameBitmap = new WriteableBitmap(
                new PixelSize(_videoWidth, _videoHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using var fb = frameBitmap.Lock();
            var destStride = fb.RowBytes;
            var srcStride = _videoPitch;
            var rows = Math.Min(_videoHeight, fb.Size.Height);

            unsafe
            {
                var src = (byte*)_videoBuffer.ToPointer();
                var dest = (byte*)fb.Address.ToPointer();
                var pixelsPerRow = Math.Min(_videoWidth, fb.Size.Width);

                for (var y = 0; y < rows; y++)
                {
                    var destRow = dest + y * destStride;
                    Buffer.MemoryCopy(src + y * srcStride, destRow, destStride, srcStride);

                    for (var x = 0; x < pixelsPerRow; x++)
                    {
                        destRow[x * 4 + 3] = 255;
                    }
                }
            }

            VideoBitmap = frameBitmap;
            UpdateWarpedVideoFrame();
        }
        finally
        {
            Interlocked.Exchange(ref _framePending, 0);
        }
    }

    private void UpdateWarpedVideoFrame()
    {
        var points = _polygon.RenderPoints;
        if (points.Count != 4)
        {
            WarpedVideoBitmap = null;
            return;
        }

        var bounds = _polygon.Bounds;
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        if (width <= 0 || height <= 0)
        {
            WarpedVideoBitmap = null;
            return;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        if (_videoBuffer != IntPtr.Zero && _videoWidth > 0 && _videoHeight > 0)
        {
            var sourceInfo = new SKImageInfo(_videoWidth, _videoHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pixmap = new SKPixmap(sourceInfo, _videoBuffer, _videoPitch);
            using var image = SKImage.FromPixels(pixmap);
            if (image == null)
            {
                WarpedVideoBitmap = null;
                return;
            }

            DrawWarpedImage(bitmap, points, bounds, image);
            WarpedVideoBitmap = bitmap;
            return;
        }

        if (VideoBitmap == null)
        {
            WarpedVideoBitmap = null;
            return;
        }

        using (var sourceFrame = VideoBitmap.Lock())
        {
            var sourceInfo = new SKImageInfo(sourceFrame.Size.Width, sourceFrame.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var pixmap = new SKPixmap(sourceInfo, sourceFrame.Address, sourceFrame.RowBytes);
            using var image = SKImage.FromPixels(pixmap);
            if (image == null)
            {
                WarpedVideoBitmap = null;
                return;
            }

            DrawWarpedImage(bitmap, points, bounds, image);
            WarpedVideoBitmap = bitmap;
        }
    }

    private static void DrawWarpedImage(WriteableBitmap target, System.Collections.Generic.IReadOnlyList<Point> points, Rect bounds, SKImage image)
    {
        using var framebuffer = target.Lock();
        var targetInfo = new SKImageInfo(target.PixelSize.Width, target.PixelSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(targetInfo, framebuffer.Address, framebuffer.RowBytes);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var positions = new[]
        {
            new SKPoint((float)(points[0].X - bounds.X), (float)(points[0].Y - bounds.Y)),
            new SKPoint((float)(points[1].X - bounds.X), (float)(points[1].Y - bounds.Y)),
            new SKPoint((float)(points[2].X - bounds.X), (float)(points[2].Y - bounds.Y)),
            new SKPoint((float)(points[3].X - bounds.X), (float)(points[3].Y - bounds.Y)),
        };

        var texCoords = new[]
        {
            new SKPoint(0, 0),
            new SKPoint(image.Width, 0),
            new SKPoint(image.Width, image.Height),
            new SKPoint(0, image.Height),
        };

        var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            Shader = SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp),
        };

        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texCoords, null, indices);
        canvas.DrawVertices(vertices, SKBlendMode.SrcOver, paint);
    }

    private void LogVideoFrame()
    {
        var frames = Interlocked.Increment(ref _frameCounter);
        if (frames == 1)
        {
            LogVideo($"first display callback { _videoWidth }x{ _videoHeight }", ref _displayLogCount, 1);
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastFrameLogTicks);
        if (now - last < 2000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastFrameLogTicks, now, last) == last)
        {
            LogVideo($"frames={frames}", ref _displayLogCount, 0);
        }
    }

    private static void LogVideo(string message, ref int counter, int limit)
    {
        if (limit > 0)
        {
            var next = Interlocked.Increment(ref counter);
            if (next > limit)
            {
                return;
            }
        }

        Console.WriteLine($"[VideoLayer] {message}");
    }
}
