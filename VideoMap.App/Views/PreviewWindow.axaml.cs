using System;
using Avalonia.Controls;
using LibVLCSharp.Shared;
using VideoMap.App.Models;
using VideoMap.App.Services;
using VideoMap.App.ViewModels;

namespace VideoMap.App.Views;

public partial class PreviewWindow : Window
{
    private LibVLC? _libVlc;
    private string? _libVlcStatus;

    public PreviewWindow()
        : this(ProjectModel.CreateDefault())
    {
    }

    public PreviewWindow(ProjectModel project, OutputSurfaceModel? initialOutput = null)
    {
        InitializeComponent();
        InitializeVideoEngine();
        DataContext = new PreviewWindowViewModel(project, _libVlc, _libVlcStatus, initialOutput);
        Closed += (_, _) => Cleanup();
    }

    public void ResetProject(ProjectModel project, OutputSurfaceModel? initialOutput = null)
    {
        (DataContext as IDisposable)?.Dispose();
        DataContext = new PreviewWindowViewModel(project, _libVlc, _libVlcStatus, initialOutput);
    }

    private void InitializeVideoEngine()
    {
        LibVlcEngine.TryGet(out _libVlc, out _libVlcStatus);
    }

    private void Cleanup()
    {
        (DataContext as IDisposable)?.Dispose();
    }

}
