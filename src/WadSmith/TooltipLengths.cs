using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WadSmith.Editor.App;

namespace WadSmith.App;

/// <summary>
/// Every tooltip in the app at the length chosen in Preferences (D34): each one is cut, or not shown, as it opens, so the
/// tips stay written in full where they are made and bound values keep their bindings.
/// </summary>
internal static class TooltipLengths
{
    // The full text of a tip last shortened, and what was shown, so a tip shortened once is known when it opens again.
    private static readonly ConditionalWeakTable<Control, Tuple<string, string>> Shortened = [];
    private static bool _installed;

    /// <summary>Starts handling every control's tooltip as it opens; once per process.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>(Opening, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void Opening(Control control, CancelRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, control) || ToolTip.GetTip(control) is not string tip) return;
        var length = App.Services?.Settings.Current.Tooltips ?? TooltipLength.Full;
        var full = Shortened.TryGetValue(control, out var known) && known.Item2 == tip ? known.Item1 : tip;
        if (TipText.At(length, full) is not { } shown)
        {
            e.Cancel = true;
            return;
        }

        Shortened.AddOrUpdate(control, Tuple.Create(full, shown));
        if (shown != tip) control.SetCurrentValue(ToolTip.TipProperty, shown);
    }
}
