using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LibVLCSharp.Shared;
using VideoMap.App.Models;
using VideoMap.App.Services;
using VideoMap.App.ViewModels;
using AppMediaType = VideoMap.App.Models.MediaType;

namespace VideoMap.App.Views;

public partial class MainWindow : Window
{
    private PreviewWindow? _previewWindow;
    private LibVLC? _libVlc;
    private bool _isDraggingPolygon;
    private bool _isDraggingVertex;
    private PolygonModel? _dragPolygon;
    private PointModel? _dragPoint;
    private Point _dragStart;
    private Point _dragVertexOffset;
    private List<Point>? _dragOriginalPoints;
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            EnsureVideoEngine(viewModel);
            viewModel.InitializeVideoEngine(_libVlc);
        }
    }

    private void OnOpenPreviewClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        if (DesignSurface.Bounds.Width > 0 && DesignSurface.Bounds.Height > 0)
        {
            viewModel.Project.CanvasWidth = DesignSurface.Bounds.Width;
            viewModel.Project.CanvasHeight = DesignSurface.Bounds.Height;
        }

        if (_previewWindow == null)
        {
            _previewWindow = new PreviewWindow(viewModel.Project);
            _previewWindow.Closed += (_, _) => _previewWindow = null;
        }
        else
        {
            _previewWindow.ResetProject(viewModel.Project);
        }

        _previewWindow.Show();
        _previewWindow.Activate();
    }

    private async void OnSaveProjectClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva progetto",
            DefaultExtension = "vmap",
            SuggestedFileName = viewModel.Project.Name,
            FileTypeChoices =
            [
                new FilePickerFileType("VideoMap Project") { Patterns = ["*.vmap"] },
            ],
        });

        var localPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        try
        {
            var copyResult = ProjectAssetManager.ConsolidateAssets(viewModel.Project, localPath);
            ProjectSerializer.Save(viewModel.Project, localPath);
            viewModel.MarkProjectSaved(localPath);
            if (copyResult.Copied > 0 || copyResult.Missing > 0)
            {
                viewModel.SetStatus($"Salvato {System.IO.Path.GetFileName(localPath)} (copiati: {copyResult.Copied}, mancanti: {copyResult.Missing})");
            }
        }
        catch (Exception ex)
        {
            viewModel.SetStatus($"Errore salvataggio: {ex.Message}");
        }
    }

    private async void OnLoadProjectClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Carica progetto",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("VideoMap Project") { Patterns = ["*.vmap"] },
            ],
        });

        var file = files.FirstOrDefault();
        var localPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        try
        {
            var project = ProjectSerializer.Load(localPath);
            viewModel.SetProject(project, localPath);
            var missingCount = project.Polygons.Count(p => p.IsMediaMissing);
            if (missingCount > 0)
            {
                viewModel.SetStatus($"Caricato {System.IO.Path.GetFileName(localPath)} - media mancanti: {missingCount}");
            }
        }
        catch (Exception ex)
        {
            viewModel.SetStatus($"Errore caricamento: {ex.Message}");
        }
    }

    private async void OnImportMediaClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel?.SelectedPolygon == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importa media",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Immagini") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.webp"] },
                new FilePickerFileType("Video") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.mpeg", "*.mpg"] },
            ],
        });

        var file = files.FirstOrDefault();
        var localPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        var mediaType = MediaClassifier.GetMediaType(localPath);
        viewModel.SelectedPolygon.MediaPath = localPath;
        viewModel.SelectedPolygon.MediaType = mediaType;
        viewModel.SetStatus($"Media associato: {System.IO.Path.GetFileName(localPath)}");

        if (mediaType == AppMediaType.Video)
        {
            EnsureVideoEngine(viewModel);
            viewModel.InitializeVideoEngine(_libVlc);
        }
    }

    private async void OnRelinkMediaClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel?.SelectedPolygon == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Ricollega media",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Immagini") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.webp"] },
                new FilePickerFileType("Video") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.mpeg", "*.mpg"] },
            ],
        });

        var file = files.FirstOrDefault();
        var localPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        var mediaType = MediaClassifier.GetMediaType(localPath);
        viewModel.SelectedPolygon.MediaPath = localPath;
        viewModel.SelectedPolygon.MediaType = mediaType;
        viewModel.SetStatus($"Media ricollegato: {System.IO.Path.GetFileName(localPath)}");

        if (mediaType == AppMediaType.Video)
        {
            EnsureVideoEngine(viewModel);
            viewModel.InitializeVideoEngine(_libVlc);
        }
    }

    private async void OnRelinkMissingMediaClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Seleziona cartella per ricollegare media",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        var localPath = folder?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        var relinked = ProjectAssetManager.RelinkMissing(viewModel.Project, localPath);
        viewModel.SetStatus(relinked > 0
            ? $"Media ricollegati: {relinked}"
            : "Nessun media ricollegato");
    }

    private void OnDesignerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null || sender is not Control surface)
        {
            return;
        }

        if (e.Source is Control { DataContext: PointModel or PolygonModel })
        {
            return;
        }

        var properties = e.GetCurrentPoint(surface).Properties;

        if (!viewModel.IsDrawingPolygon)
        {
            if (properties.IsLeftButtonPressed)
            {
                viewModel.SelectedPolygon = null;
            }

            return;
        }

        var point = e.GetPosition(surface);

        if (properties.IsRightButtonPressed)
        {
            viewModel.CompletePolygonCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            viewModel.AddPointAt(point.X, point.Y);
            e.Handled = true;
        }
    }

    private void OnPolygonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null || viewModel.IsDrawingPolygon)
        {
            return;
        }

        if (sender is not Control control || control.DataContext is not PolygonModel polygon)
        {
            return;
        }

        viewModel.SelectedPolygon = polygon;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginPolygonDrag(polygon, e);
        e.Handled = true;
    }

    private void OnVertexPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null || viewModel.IsDrawingPolygon)
        {
            return;
        }

        if (sender is not Control control || control.DataContext is not PointModel point)
        {
            return;
        }

        var polygon = viewModel.SelectedPolygon;
        if (polygon == null)
        {
            return;
        }

        if (!polygon.Points.Contains(point) || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDraggingVertex = true;
        _dragPolygon = polygon;
        _dragPoint = point;
        var start = e.GetPosition(DesignSurface);
        _dragStart = start;
        _dragVertexOffset = new Point(point.X - start.X, point.Y - start.Y);
        e.Pointer.Capture(DesignSurface);
        e.Handled = true;
    }

    private void OnDesignerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPolygon == null)
        {
            return;
        }

        var current = e.GetPosition(DesignSurface);
        var delta = current - _dragStart;

        if (_isDraggingVertex)
        {
            if (_dragPoint != null)
            {
                _dragPoint.X = current.X + _dragVertexOffset.X;
                _dragPoint.Y = current.Y + _dragVertexOffset.Y;
            }

            e.Handled = true;
            return;
        }

        if (_isDraggingPolygon && _dragOriginalPoints != null)
        {
            var count = Math.Min(_dragPolygon.Points.Count, _dragOriginalPoints.Count);
            for (var i = 0; i < count; i++)
            {
                var original = _dragOriginalPoints[i];
                _dragPolygon.Points[i].X = original.X + delta.X;
                _dragPolygon.Points[i].Y = original.Y + delta.Y;
            }

            e.Handled = true;
        }
    }

    private void OnDesignerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingPolygon && !_isDraggingVertex)
        {
            return;
        }

        _isDraggingPolygon = false;
        _isDraggingVertex = false;
        _dragPolygon = null;
        _dragPoint = null;
        _dragOriginalPoints = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void BeginPolygonDrag(PolygonModel polygon, PointerPressedEventArgs e)
    {
        _isDraggingPolygon = true;
        _dragPolygon = polygon;
        _dragStart = e.GetPosition(DesignSurface);
        _dragOriginalPoints = new List<Point>(polygon.Points.Count);

        foreach (var point in polygon.Points)
        {
            _dragOriginalPoints.Add(new Point(point.X, point.Y));
        }

        e.Pointer.Capture(DesignSurface);
    }

    private IStorageProvider? GetStorageProvider()
    {
        return TopLevel.GetTopLevel(this)?.StorageProvider;
    }

    private void OnPlaybackSeekChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SeekAll(e.NewValue);
        }
    }

    private async void OnExportPresetClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Esporta preset",
            DefaultExtension = "vmapout",
            SuggestedFileName = viewModel.Project.Name,
            FileTypeChoices =
            [
                new FilePickerFileType("VideoMap Output Preset") { Patterns = ["*.vmapout"] },
            ],
        });

        var localPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        try
        {
            OutputPresetSerializer.Export(viewModel.Project, localPath);
            viewModel.SetStatus($"Preset esportato: {System.IO.Path.GetFileName(localPath)}");
        }
        catch (Exception ex)
        {
            viewModel.SetStatus($"Errore export preset: {ex.Message}");
        }
    }

    private void EnsureVideoEngine(MainWindowViewModel? viewModel)
    {
        if (_libVlc != null)
        {
            return;
        }

        var ok = LibVlcEngine.TryGet(out _libVlc, out var status);
        if (!ok && viewModel != null)
        {
            viewModel.SetStatus(status);
        }
        if (viewModel != null)
        {
            viewModel.LibVlcStatus = status;
        }
    }

    private async void OnBrowseVlcPathClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        var storage = GetStorageProvider();
        if (storage == null)
        {
            viewModel.SetStatus("Storage provider non disponibile");
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Seleziona cartella VLC (Contents/MacOS)",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        var localPath = folder?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        viewModel.VlcBasePath = localPath;
    }

    private void OnApplyVlcPathClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        ApplyVlcBasePath(viewModel, viewModel.VlcBasePath);
    }

    private void OnResetVlcPathClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        viewModel.VlcBasePath = null;
        ApplyVlcBasePath(viewModel, null);
    }

    private void ApplyVlcBasePath(MainWindowViewModel viewModel, string? path)
    {
        var normalized = NormalizeVlcBasePath(path);
        viewModel.VlcBasePath = normalized;
        AppSettingsService.SetVlcBasePath(normalized);
        LibVlcEngine.ConfigureUserBasePath(normalized);
        _libVlc = null;

        if (LibVlcEngine.TryRelaunchWithEnvironment(normalized))
        {
            viewModel.SetStatus("Riavvio in corso per applicare la configurazione VLC...");
            Close();
            return;
        }

        var ok = LibVlcEngine.TryGet(out _libVlc, out var status);
        viewModel.InitializeVideoEngine(_libVlc);
        viewModel.LibVlcStatus = status;
        viewModel.SetStatus(ok ? $"LibVLC configurato: {status}" : status);
    }

    private static string? NormalizeVlcBasePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        var candidates = new[]
        {
            Path.Combine("/Applications", trimmed),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", trimmed),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return trimmed;
    }

}
