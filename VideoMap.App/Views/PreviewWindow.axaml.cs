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

    public PreviewWindow()
        : this(ProjectModel.CreateDefault())
    {
    }

    public PreviewWindow(ProjectModel project)
    {
        InitializeComponent();
        InitializeVideoEngine();
        DataContext = new PreviewWindowViewModel(project, _libVlc);
        Closed += (_, _) => Cleanup();
    }

    public void ResetProject(ProjectModel project)
    {
        (DataContext as IDisposable)?.Dispose();
        DataContext = new PreviewWindowViewModel(project, _libVlc);
    }

    private void InitializeVideoEngine()
    {
        LibVlcEngine.TryGet(out _libVlc, out _);
    }

    private void Cleanup()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
