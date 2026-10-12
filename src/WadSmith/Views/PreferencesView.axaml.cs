using Avalonia.Controls;

namespace WadSmith.App.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView()
    {
        InitializeComponent();

        // Focus inside the sheet, so Escape closes it and Tab moves through it.
        AttachedToVisualTree += (_, _) => Done.Focus();
    }
}
