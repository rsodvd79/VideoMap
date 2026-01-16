using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using VideoMap.App.Models;
using AppMediaType = VideoMap.App.Models.MediaType;

namespace VideoMap.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private ProjectModel _project = ProjectModel.CreateDefault();
    private PolygonModel? _selectedPolygon;
    private SceneModel? _selectedScene;
    private OutputSurfaceModel? _selectedOutput;
    private string _statusMessage = "Progetto non salvato";
    private bool _isDrawingPolygon;
    private string? _projectPath;
    private int _polygonSequence = 1;
    private LibVLC? _libVlc;
    private PolygonModel? _selectedPolygonObserver;
    private double _playbackPosition;
    private bool _isScenePlaying;
    private double _sceneProgress;
    private string _sceneStatus = "Timeline ferma";
    private DispatcherTimer? _sceneTimer;
    private int _sceneIndex;
    private DateTime _sceneStart;
    private double _sceneDuration;
    private string? _vlcBasePath;
    private string _libVlcStatus = "LibVLC non inizializzato";

    public MainWindowViewModel()
    {
        NewProjectCommand = new RelayCommand(CreateNewProject);
        StartPolygonCommand = new RelayCommand<Rect>(StartPolygon, _ => !IsDrawingPolygon);
        CompletePolygonCommand = new RelayCommand(CompletePolygon, () => IsDrawingPolygon);
        RemovePolygonCommand = new RelayCommand(RemoveSelectedPolygon, () => HasSelection && !IsDrawingPolygon);
        MovePolygonUpCommand = new RelayCommand(MovePolygonUp, () => CanMovePolygon(-1));
        MovePolygonDownCommand = new RelayCommand(MovePolygonDown, () => CanMovePolygon(1));
        PlayAllCommand = new RelayCommand(PlayAll);
        PauseAllCommand = new RelayCommand(PauseAll);
        StopAllCommand = new RelayCommand(StopAll);
        AddSceneCommand = new RelayCommand(AddScene);
        RemoveSceneCommand = new RelayCommand(RemoveScene, () => SelectedScene != null);
        ApplySceneCommand = new RelayCommand(ApplySelectedScene, () => SelectedScene != null);
        PlaySceneCommand = new RelayCommand(PlaySceneTimeline, () => SelectedScene != null);
        StopSceneCommand = new RelayCommand(StopSceneTimeline, () => IsScenePlaying);
        AddOutputCommand = new RelayCommand(AddOutput);
        RemoveOutputCommand = new RelayCommand(RemoveOutput, () => SelectedOutput != null);

        AttachProject(Project);
        SelectedScene = Project.Scenes.FirstOrDefault();
        SelectedOutput = Project.Outputs.FirstOrDefault();
    }

    public ProjectModel Project
    {
        get => _project;
        private set
        {
            if (SetProperty(ref _project, value))
            {
                OnPropertyChanged(nameof(Polygons));
            }
        }
    }

    public ObservableCollection<PolygonModel> Polygons => Project.Polygons;

    public ObservableCollection<VideoLayerViewModel> VideoLayers { get; } = new();

    public ObservableCollection<MediaLibraryItemViewModel> MediaLibrary { get; } = new();
    public ObservableCollection<ScenePolygonAssignmentViewModel> SceneAssignments { get; } = new();
    public ObservableCollection<OutputPolygonAssignmentViewModel> OutputAssignments { get; } = new();

    public ObservableCollection<SceneModel> Scenes => Project.Scenes;
    public ObservableCollection<OutputSurfaceModel> Outputs => Project.Outputs;

    public PolygonModel? SelectedPolygon
    {
        get => _selectedPolygon;
        set
        {
            if (SetProperty(ref _selectedPolygon, value))
            {
                AttachSelectedPolygon(_selectedPolygon);
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
                OnPropertyChanged(nameof(HasNoSelectionAndNoOutput));
                OnPropertyChanged(nameof(IsSelectedPolygonVideo));
                MovePolygonUpCommand.NotifyCanExecuteChanged();
                MovePolygonDownCommand.NotifyCanExecuteChanged();
                RemovePolygonCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedPolygon != null;
    public bool HasNoSelection => !HasSelection;
    public bool HasPolygons => Project.Polygons.Count > 0;
    public bool HasNoPolygons => !HasPolygons;
    public bool IsSelectedPolygonVideo => SelectedPolygon?.MediaType == AppMediaType.Video;
    public bool HasMediaLibrary => MediaLibrary.Count > 0;
    public bool HasNoSelectionAndNoOutput => HasNoSelection && HasNoOutputSelection;

    public string? VlcBasePath
    {
        get => _vlcBasePath;
        set => SetProperty(ref _vlcBasePath, value);
    }

    public string LibVlcStatus
    {
        get => _libVlcStatus;
        set => SetProperty(ref _libVlcStatus, value);
    }

    public SceneModel? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (SetProperty(ref _selectedScene, value))
            {
                OnPropertyChanged(nameof(HasSceneSelection));
                OnPropertyChanged(nameof(HasNoSceneSelection));
                RemoveSceneCommand.NotifyCanExecuteChanged();
                ApplySceneCommand.NotifyCanExecuteChanged();
                PlaySceneCommand.NotifyCanExecuteChanged();
                RebuildSceneAssignments();
                if (_selectedScene != null)
                {
                    ApplyScene(_selectedScene);
                }
            }
        }
    }

    public bool HasSceneSelection => SelectedScene != null;
    public bool HasNoSceneSelection => !HasSceneSelection;

    public OutputSurfaceModel? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (SetProperty(ref _selectedOutput, value))
            {
                OnPropertyChanged(nameof(HasOutputSelection));
                OnPropertyChanged(nameof(HasNoOutputSelection));
                OnPropertyChanged(nameof(HasNoSelectionAndNoOutput));
                RemoveOutputCommand.NotifyCanExecuteChanged();
                RebuildOutputAssignments();
            }
        }
    }

    public bool HasOutputSelection => SelectedOutput != null;
    public bool HasNoOutputSelection => !HasOutputSelection;

    public double PlaybackPosition
    {
        get => _playbackPosition;
        set => SetProperty(ref _playbackPosition, value);
    }

    public bool IsScenePlaying
    {
        get => _isScenePlaying;
        private set
        {
            if (SetProperty(ref _isScenePlaying, value))
            {
                StopSceneCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public double SceneProgress
    {
        get => _sceneProgress;
        private set => SetProperty(ref _sceneProgress, value);
    }

    public string SceneStatus
    {
        get => _sceneStatus;
        private set => SetProperty(ref _sceneStatus, value);
    }

    public bool IsDrawingPolygon
    {
        get => _isDrawingPolygon;
        private set
        {
            if (SetProperty(ref _isDrawingPolygon, value))
            {
                StartPolygonCommand.NotifyCanExecuteChanged();
                CompletePolygonCommand.NotifyCanExecuteChanged();
                RemovePolygonCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? ProjectPath
    {
        get => _projectPath;
        private set => SetProperty(ref _projectPath, value);
    }

    public IRelayCommand NewProjectCommand { get; }
    public IRelayCommand StartPolygonCommand { get; }
    public IRelayCommand CompletePolygonCommand { get; }
    public IRelayCommand RemovePolygonCommand { get; }
    public IRelayCommand MovePolygonUpCommand { get; }
    public IRelayCommand MovePolygonDownCommand { get; }
    public IRelayCommand PlayAllCommand { get; }
    public IRelayCommand PauseAllCommand { get; }
    public IRelayCommand StopAllCommand { get; }
    public IRelayCommand AddSceneCommand { get; }
    public IRelayCommand RemoveSceneCommand { get; }
    public IRelayCommand ApplySceneCommand { get; }
    public IRelayCommand PlaySceneCommand { get; }
    public IRelayCommand StopSceneCommand { get; }
    public IRelayCommand AddOutputCommand { get; }
    public IRelayCommand RemoveOutputCommand { get; }

    public void AddPointAt(double x, double y)
    {
        if (!IsDrawingPolygon || SelectedPolygon == null)
        {
            return;
        }

        SelectedPolygon.Points.Add(new PointModel(x, y));
        StatusMessage = $"Vertice aggiunto ({SelectedPolygon.Points.Count})";
    }

    public void SetProject(ProjectModel project, string? path)
    {
        DetachProject(Project);
        Project = project;
        AttachProject(Project);
        ProjectPath = path;
        SelectedPolygon = null;
        SelectedScene = Project.Scenes.FirstOrDefault();
        SelectedOutput = Project.Outputs.FirstOrDefault();
        IsDrawingPolygon = false;
        _polygonSequence = Project.Polygons.Count + 1;
        StopSceneTimeline();
        StatusMessage = path == null
            ? "Progetto caricato"
            : $"Caricato {System.IO.Path.GetFileName(path)}";
    }

    public void MarkProjectSaved(string path)
    {
        ProjectPath = path;
        StatusMessage = $"Salvato {System.IO.Path.GetFileName(path)}";
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    private void CreateNewProject()
    {
        DetachProject(Project);
        Project = ProjectModel.CreateDefault();
        AttachProject(Project);
        ProjectPath = null;
        SelectedPolygon = null;
        SelectedScene = Project.Scenes.FirstOrDefault();
        SelectedOutput = Project.Outputs.FirstOrDefault();
        IsDrawingPolygon = false;
        _polygonSequence = 1;
        StopSceneTimeline();
        StatusMessage = "Nuovo progetto";
    }

    private void StartPolygon(Rect bounds)
    {
        var width = bounds.Width > 0 ? bounds.Width : Project.CanvasWidth;
        var height = bounds.Height > 0 ? bounds.Height : Project.CanvasHeight;
        if (width <= 0 || height <= 0)
        {
            width = 1920;
            height = 1080;
        }

        var size = Math.Min(width, height) * 0.25;
        if (size <= 0)
        {
            size = 200;
        }

        var half = size / 2;
        var centerX = width / 2;
        var centerY = height / 2;

        var polygon = new PolygonModel
        {
            Name = $"Poligono {_polygonSequence++}",
            Order = Project.Polygons.Count,
        };

        polygon.Points.Add(new PointModel(centerX - half, centerY - half));
        polygon.Points.Add(new PointModel(centerX + half, centerY - half));
        polygon.Points.Add(new PointModel(centerX + half, centerY + half));
        polygon.Points.Add(new PointModel(centerX - half, centerY + half));

        Project.Polygons.Add(polygon);
        UpdatePolygonOrder();
        SelectedPolygon = polygon;
        IsDrawingPolygon = false;
        StatusMessage = "Poligono creato al centro (quadrato)";
    }

    private void CompletePolygon()
    {
        if (SelectedPolygon == null)
        {
            IsDrawingPolygon = false;
            return;
        }

        if (SelectedPolygon.Points.Count < 3)
        {
            Project.Polygons.Remove(SelectedPolygon);
            SelectedPolygon = null;
            StatusMessage = "Poligono annullato (servono almeno 3 vertici)";
        }
        else
        {
            StatusMessage = "Poligono completato";
        }

        IsDrawingPolygon = false;
    }

    private void RemoveSelectedPolygon()
    {
        if (SelectedPolygon == null)
        {
            return;
        }

        Project.Polygons.Remove(SelectedPolygon);
        SelectedPolygon = null;
        UpdatePolygonOrder();
        StatusMessage = "Poligono rimosso";
    }

    private void AttachProject(ProjectModel project)
    {
        project.Polygons.CollectionChanged += OnPolygonsChanged;
        project.Scenes.CollectionChanged += OnScenesChanged;
        project.Outputs.CollectionChanged += OnOutputsChanged;
        foreach (var polygon in project.Polygons)
        {
            AttachPolygon(polygon);
        }

        foreach (var scene in project.Scenes)
        {
            scene.Normalize();
        }

        foreach (var output in project.Outputs)
        {
            output.Normalize();
        }

        RefreshMediaLibrary();
        UpdatePolygonOrder();
        SyncScenePolygonIds();
        SyncOutputPolygonIds();
        RebuildSceneAssignments();
        RebuildOutputAssignments();
        OnPropertyChanged(nameof(HasPolygons));
        OnPropertyChanged(nameof(HasNoPolygons));
    }

    private void DetachProject(ProjectModel project)
    {
        project.Polygons.CollectionChanged -= OnPolygonsChanged;
        project.Scenes.CollectionChanged -= OnScenesChanged;
        project.Outputs.CollectionChanged -= OnOutputsChanged;
        foreach (var polygon in project.Polygons)
        {
            DetachPolygon(polygon);
        }

        foreach (var assignment in SceneAssignments)
        {
            assignment.Dispose();
        }

        foreach (var assignment in OutputAssignments)
        {
            assignment.Dispose();
        }

        SceneAssignments.Clear();
        OutputAssignments.Clear();
    }

    private void OnPolygonsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (PolygonModel polygon in e.OldItems)
            {
                DetachPolygon(polygon);
                RemovePolygonFromScenes(polygon.Id);
                RemovePolygonFromOutputs(polygon.Id);
            }
        }

        if (e.NewItems != null)
        {
            foreach (PolygonModel polygon in e.NewItems)
            {
                AttachPolygon(polygon);
                AddPolygonToScenes(polygon.Id);
                AddPolygonToOutputs(polygon.Id);
            }
        }

        UpdatePolygonOrder();
        RefreshMediaLibrary();
        UpdateVideoSoloState();
        RebuildSceneAssignments();
        RebuildOutputAssignments();
        OnPropertyChanged(nameof(HasPolygons));
        OnPropertyChanged(nameof(HasNoPolygons));
    }

    private void OnScenesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Scenes));
        if (SelectedScene == null && Scenes.Count > 0)
        {
            SelectedScene = Scenes[0];
        }

        if (IsScenePlaying && (_sceneIndex >= Scenes.Count || Scenes.Count == 0))
        {
            StopSceneTimeline();
        }

        RebuildSceneAssignments();
    }

    private void OnOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Outputs));
        if (SelectedOutput == null && Outputs.Count > 0)
        {
            SelectedOutput = Outputs[0];
        }
        RebuildOutputAssignments();
    }

    public void InitializeVideoEngine(LibVLC? libVlc)
    {
        _libVlc = libVlc;
        RebuildVideoLayers();
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
            RefreshMediaLibrary();
        }

        if (e.PropertyName == nameof(PolygonModel.IsVideoSolo))
        {
            UpdateVideoSoloState();
        }

        if (e.PropertyName == nameof(PolygonModel.MediaType) && sender == SelectedPolygon)
        {
            OnPropertyChanged(nameof(IsSelectedPolygonVideo));
        }
    }

    private void AttachSelectedPolygon(PolygonModel? polygon)
    {
        if (_selectedPolygonObserver != null)
        {
            _selectedPolygonObserver.PropertyChanged -= OnSelectedPolygonPropertyChanged;
        }

        _selectedPolygonObserver = polygon;

        if (_selectedPolygonObserver != null)
        {
            _selectedPolygonObserver.PropertyChanged += OnSelectedPolygonPropertyChanged;
        }
    }

    private void OnSelectedPolygonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolygonModel.MediaType))
        {
            OnPropertyChanged(nameof(IsSelectedPolygonVideo));
        }
    }

    private void UpdatePolygonOrder()
    {
        for (var i = 0; i < Project.Polygons.Count; i++)
        {
            Project.Polygons[i].Order = i;
        }

        SyncVideoLayerOrder();
        MovePolygonUpCommand.NotifyCanExecuteChanged();
        MovePolygonDownCommand.NotifyCanExecuteChanged();
    }

    private void AddScene()
    {
        var scene = new SceneModel
        {
            Name = $"Scene {Scenes.Count + 1}",
            DurationSeconds = 5,
        };

        foreach (var polygon in Project.Polygons)
        {
            scene.ActivePolygonIds.Add(polygon.Id);
        }

        Scenes.Add(scene);
        SelectedScene = scene;
        SceneStatus = "Scene aggiunta";
    }

    private void RemoveScene()
    {
        if (SelectedScene == null)
        {
            return;
        }

        var index = Scenes.IndexOf(SelectedScene);
        Scenes.Remove(SelectedScene);
        SelectedScene = Scenes.Count > 0
            ? Scenes[Math.Clamp(index, 0, Scenes.Count - 1)]
            : null;
        SceneStatus = "Scene rimossa";
    }

    private void ApplySelectedScene()
    {
        if (SelectedScene == null)
        {
            return;
        }

        ApplyScene(SelectedScene);
    }

    private void ApplyScene(SceneModel scene)
    {
        var activeIds = scene.ActivePolygonIds.ToHashSet();

        foreach (var polygon in Project.Polygons)
        {
            polygon.IsSceneVisible = activeIds.Count == 0 || activeIds.Contains(polygon.Id);
        }

        SceneStatus = $"Scene attiva: {scene.Name}";
    }

    private void PlaySceneTimeline()
    {
        if (SelectedScene == null || Scenes.Count == 0)
        {
            return;
        }

        _sceneIndex = Scenes.IndexOf(SelectedScene);
        if (_sceneIndex < 0)
        {
            _sceneIndex = 0;
        }

        StartScenePlayback(Scenes[_sceneIndex]);
        IsScenePlaying = true;
        SceneStatus = "Timeline in riproduzione";
    }

    private void StopSceneTimeline()
    {
        _sceneTimer?.Stop();
        IsScenePlaying = false;
        SceneProgress = 0;
        SceneStatus = "Timeline ferma";
    }

    private void StartScenePlayback(SceneModel scene)
    {
        ApplyScene(scene);
        _sceneDuration = Math.Max(0.1, scene.DurationSeconds);
        _sceneStart = DateTime.UtcNow;
        SceneProgress = 0;
        EnsureSceneTimer();
        _sceneTimer!.Start();
    }

    private void EnsureSceneTimer()
    {
        if (_sceneTimer != null)
        {
            return;
        }

        _sceneTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _sceneTimer.Tick += (_, _) => OnSceneTick();
    }

    private void OnSceneTick()
    {
        var elapsed = (DateTime.UtcNow - _sceneStart).TotalSeconds;
        SceneProgress = Math.Clamp(elapsed / _sceneDuration, 0, 1);

        if (elapsed < _sceneDuration)
        {
            return;
        }

        _sceneIndex++;
        if (_sceneIndex >= Scenes.Count)
        {
            StopSceneTimeline();
            return;
        }

        StartScenePlayback(Scenes[_sceneIndex]);
    }

    private void AddOutput()
    {
        var output = new OutputSurfaceModel
        {
            Name = $"Output {Outputs.Count + 1}",
            Width = Project.CanvasWidth,
            Height = Project.CanvasHeight,
        };

        foreach (var polygon in Project.Polygons)
        {
            output.PolygonIds.Add(polygon.Id);
        }

        Outputs.Add(output);
        SelectedOutput = output;
    }

    private void RemoveOutput()
    {
        if (SelectedOutput == null)
        {
            return;
        }

        var index = Outputs.IndexOf(SelectedOutput);
        Outputs.Remove(SelectedOutput);
        SelectedOutput = Outputs.Count > 0
            ? Outputs[Math.Clamp(index, 0, Outputs.Count - 1)]
            : null;
    }

    public void PlayAll()
    {
        foreach (var layer in VideoLayers)
        {
            layer.Play();
        }

        StatusMessage = "Playback avviato";
    }

    public void PauseAll()
    {
        foreach (var layer in VideoLayers)
        {
            layer.Pause();
        }

        StatusMessage = "Playback in pausa";
    }

    public void StopAll()
    {
        foreach (var layer in VideoLayers)
        {
            layer.Stop();
        }

        PlaybackPosition = 0;
        StatusMessage = "Playback fermato";
    }

    public void SeekAll(double position)
    {
        var clamped = Math.Clamp(position, 0, 1);
        PlaybackPosition = clamped;
        foreach (var layer in VideoLayers)
        {
            layer.Seek(clamped);
        }
    }

    private bool CanMovePolygon(int direction)
    {
        if (SelectedPolygon == null)
        {
            return false;
        }

        var index = Project.Polygons.IndexOf(SelectedPolygon);
        var target = index + direction;
        return target >= 0 && target < Project.Polygons.Count;
    }

    private void MovePolygonUp()
    {
        MovePolygon(-1);
    }

    private void MovePolygonDown()
    {
        MovePolygon(1);
    }

    private void MovePolygon(int direction)
    {
        if (SelectedPolygon == null)
        {
            return;
        }

        var index = Project.Polygons.IndexOf(SelectedPolygon);
        var target = index + direction;
        if (target < 0 || target >= Project.Polygons.Count)
        {
            return;
        }

        Project.Polygons.Move(index, target);
        UpdatePolygonOrder();
    }

    private void RefreshMediaLibrary()
    {
        MediaLibrary.Clear();

        var groups = Project.Polygons
            .Where(p => !string.IsNullOrWhiteSpace(p.MediaPath))
            .GroupBy(p => p.MediaPath ?? string.Empty);

        foreach (var group in groups)
        {
            var polygon = group.First();
            var item = new MediaLibraryItemViewModel
            {
                FullPath = polygon.MediaPath ?? string.Empty,
                DisplayName = System.IO.Path.GetFileName(polygon.MediaPath ?? string.Empty),
                MediaType = polygon.MediaType,
                IsMissing = group.Any(p => p.IsMediaMissing),
                UsageCount = group.Count(),
            };

            MediaLibrary.Add(item);
        }

        OnPropertyChanged(nameof(HasMediaLibrary));
    }

    private void RebuildVideoLayers()
    {
        foreach (var layer in VideoLayers.ToList())
        {
            layer.Dispose();
        }

        VideoLayers.Clear();

        if (_libVlc == null)
        {
            return;
        }

        foreach (var polygon in Project.Polygons)
        {
            AddVideoLayer(polygon);
        }

        UpdateVideoSoloState();
    }

    private void AddVideoLayer(PolygonModel polygon)
    {
        if (_libVlc == null || VideoLayers.Any(layer => layer.Polygon == polygon))
        {
            return;
        }

        var layer = new VideoLayerViewModel(_libVlc, polygon);
        VideoLayers.Add(layer);
    }

    private void RemoveVideoLayer(PolygonModel polygon)
    {
        var layer = VideoLayers.FirstOrDefault(candidate => candidate.Polygon == polygon);
        if (layer == null)
        {
            return;
        }

        layer.Dispose();
        VideoLayers.Remove(layer);
    }

    private void SyncVideoLayerOrder()
    {
        if (VideoLayers.Count == 0)
        {
            return;
        }

        var ordered = Project.Polygons
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
        var soloActive = Project.Polygons.Any(p => p.IsVideoSolo);
        foreach (var layer in VideoLayers)
        {
            var suppress = soloActive && !layer.Polygon.IsVideoSolo;
            layer.SetSuppressed(suppress);
        }
    }

    private void RebuildSceneAssignments()
    {
        foreach (var assignment in SceneAssignments)
        {
            assignment.Dispose();
        }

        SceneAssignments.Clear();

        if (SelectedScene == null)
        {
            return;
        }

        foreach (var polygon in Project.Polygons)
        {
            SceneAssignments.Add(new ScenePolygonAssignmentViewModel(SelectedScene, polygon, () => ApplyScene(SelectedScene)));
        }
    }

    private void RebuildOutputAssignments()
    {
        foreach (var assignment in OutputAssignments)
        {
            assignment.Dispose();
        }

        OutputAssignments.Clear();

        if (SelectedOutput == null)
        {
            return;
        }

        foreach (var polygon in Project.Polygons)
        {
            OutputAssignments.Add(new OutputPolygonAssignmentViewModel(SelectedOutput, polygon, null));
        }
    }

    private void AddPolygonToScenes(Guid polygonId)
    {
        foreach (var scene in Scenes)
        {
            if (!scene.ActivePolygonIds.Contains(polygonId))
            {
                scene.ActivePolygonIds.Add(polygonId);
            }
        }
    }

    private void RemovePolygonFromScenes(Guid polygonId)
    {
        foreach (var scene in Scenes)
        {
            scene.ActivePolygonIds.Remove(polygonId);
        }
    }

    private void AddPolygonToOutputs(Guid polygonId)
    {
        foreach (var output in Outputs)
        {
            if (!output.PolygonIds.Contains(polygonId))
            {
                output.PolygonIds.Add(polygonId);
            }
        }
    }

    private void RemovePolygonFromOutputs(Guid polygonId)
    {
        foreach (var output in Outputs)
        {
            output.PolygonIds.Remove(polygonId);
        }
    }

    private void SyncScenePolygonIds()
    {
        var ids = Project.Polygons.Select(p => p.Id).ToHashSet();

        foreach (var scene in Scenes)
        {
            var toRemove = scene.ActivePolygonIds.Where(id => !ids.Contains(id)).ToList();
            foreach (var id in toRemove)
            {
                scene.ActivePolygonIds.Remove(id);
            }

            foreach (var id in ids)
            {
                if (!scene.ActivePolygonIds.Contains(id))
                {
                    scene.ActivePolygonIds.Add(id);
                }
            }
        }
    }

    private void SyncOutputPolygonIds()
    {
        var ids = Project.Polygons.Select(p => p.Id).ToHashSet();

        foreach (var output in Outputs)
        {
            var toRemove = output.PolygonIds.Where(id => !ids.Contains(id)).ToList();
            foreach (var id in toRemove)
            {
                output.PolygonIds.Remove(id);
            }

            foreach (var id in ids)
            {
                if (!output.PolygonIds.Contains(id))
                {
                    output.PolygonIds.Add(id);
                }
            }
        }
    }
}
