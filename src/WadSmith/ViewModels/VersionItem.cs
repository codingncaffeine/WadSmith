using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;

namespace WadSmith.App.ViewModels;

/// <summary>One kept copy of the map in the Versions panel.</summary>
internal sealed partial class VersionItem(MapVersion version, Bitmap? picture, Func<VersionItem, Task> goBack)
{
    public MapVersion Version { get; } = version;

    public Bitmap? Picture { get; } = picture;

    public string Reason => Version.Reason.Length > 0 ? Version.Reason : "Kept";

    /// <summary>"Today 19:37", "Yesterday 08:12", "3 Oct 14:00", or with the year when it is not this one.</summary>
    public string WhenText => Describe(Version.When.LocalDateTime, DateTime.Now);

    internal static string Describe(DateTime when, DateTime now)
    {
        var time = when.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (when.Date == now.Date) return $"Today {time}";
        if (when.Date == now.Date.AddDays(-1)) return $"Yesterday {time}";
        return when.Year == now.Year
            ? when.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)
            : when.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private Task GoBackAsync() => goBack(this);
}
