using CommunityToolkit.Mvvm.ComponentModel;
using VideoMap.App.Models;

namespace VideoMap.App.ViewModels;

public partial class MediaLibraryItemViewModel : ObservableObject
{
    private string _displayName = string.Empty;
    private string _fullPath = string.Empty;
    private MediaType _mediaType = MediaType.None;
    private bool _isMissing;
    private int _usageCount;

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    public MediaType MediaType
    {
        get => _mediaType;
        set => SetProperty(ref _mediaType, value);
    }

    public bool IsMissing
    {
        get => _isMissing;
        set => SetProperty(ref _isMissing, value);
    }

    public int UsageCount
    {
        get => _usageCount;
        set
        {
            if (SetProperty(ref _usageCount, value))
            {
                OnPropertyChanged(nameof(UsageLabel));
            }
        }
    }

    public string UsageLabel => UsageCount == 1 ? "1 uso" : $"{UsageCount} usi";
}
