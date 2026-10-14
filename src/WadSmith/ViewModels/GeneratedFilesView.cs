using CommunityToolkit.Mvvm.ComponentModel;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>
/// The text view of the files WadSmith writes into the project (§3.10): the list, and the chosen file read and coloured on a
/// worker; read only, since WadSmith writes them again from the project.
/// </summary>
internal sealed partial class GeneratedFilesView : ObservableObject
{
    private readonly Project _project;

    public GeneratedFilesView(Project project, IReadOnlyList<string> files)
    {
        _project = project;
        Files = files;
        Selected = files.FirstOrDefault();
    }

    public IReadOnlyList<string> Files { get; }

    /// <summary>Whether the project has none yet (a map with nothing brought in, carried or set has no zmapinfo either).</summary>
    public bool None => Files.Count == 0;

    [ObservableProperty]
    private string? _selected;

    /// <summary>The chosen file's text in coloured pieces; empty while it is read.</summary>
    [ObservableProperty]
    private IReadOnlyList<(string Text, CodeKind Kind)> _pieces = [];

    partial void OnSelectedChanged(string? value) => _ = LoadAsync(value);

    private async Task LoadAsync(string? file)
    {
        if (file is null)
        {
            Pieces = [];
            return;
        }

        var path = Path.Combine(_project.Folder, file);
        var pieces = await Task.Run(() =>
        {
            try
            {
                return GeneratedFiles.Pieces(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (IReadOnlyList<(string, CodeKind)>)[($"{file} could not be read: {ex.Message}", CodeKind.Comment)];
            }
        }).ConfigureAwait(true);
        if (Selected == file) Pieces = pieces;
    }
}
