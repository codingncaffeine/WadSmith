using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WadSmith.App.ViewModels;

namespace WadSmith.App.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WelcomeViewModel vm)
            {
                vm.PickFolder = PickFolderAsync;
                vm.PickFile = PickFileAsync;
            }
        };
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFileAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = GameFileTypes.Filter }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}

/// <summary>The file picker's filter for game files: WADs and the archives that hold them.</summary>
internal static class GameFileTypes
{
    public static readonly IReadOnlyList<FilePickerFileType> Filter =
    [
        new("Game files (WAD, PK3, ZIP)") { Patterns = ["*.wad", "*.WAD", "*.iwad", "*.IWAD", "*.pk3", "*.PK3", "*.ipk3", "*.IPK3", "*.zip", "*.ZIP"] },
        FilePickerFileTypes.All,
    ];
}
