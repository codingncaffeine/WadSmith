using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WadSmith.App.ViewModels;
using WadSmith.Editor.App;

namespace WadSmith.App.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
        HeightsRamp.Background = WadSmith.App.Canvas.HeightRamp.Brush();
        View3D.CameraMoved += camera => Canvas.ShowCamera(camera);
        View3D.CameraMoved += _ => (DataContext as EditorViewModel)?.CameraMoved();
        View3D.EngineViewToggled += () =>
        {
            if (DataContext is EditorViewModel vm) vm.EngineView = !vm.EngineView;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not EditorViewModel vm) return;
            ApplyLayout(vm.Layout, focus: false);
            vm.PlaySpot = () => View3D.PlaySpot();
            vm.ViewCamera = () =>
            {
                var c = View3D.CurrentCamera;
                return (c.X, c.Y, c.Z, c.Yaw, c.Pitch);
            };
            vm.FitMap = Canvas.FitMap;
            vm.ViewSize = () =>
            {
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                return ((int)Math.Round(View3D.Bounds.Width * scale), (int)Math.Round(View3D.Bounds.Height * scale));
            };
            vm.Problems.ShowRequested += elements => Canvas.FitTo(elements);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditorViewModel.Layout)) ApplyLayout(vm.Layout, focus: true);
                if (e.PropertyName == nameof(EditorViewModel.Palette)) PaletteChanged(vm);
            };
        };
        AddHandler(KeyDownEvent, OnTabDown, RoutingStrategies.Tunnel);
        Canvas.HoverChanged += (element, at) => (DataContext as EditorViewModel)?.Hover(element, at);
        Canvas.Drawing += (size, step) => (DataContext as EditorViewModel)?.Drawing(size, step);
        Canvas.Moving += move => (DataContext as EditorViewModel)?.Moving(move);
        Canvas.Placing += text => (DataContext as EditorViewModel)?.Hint(text);
        Canvas.Measured += (from, to) => (DataContext as EditorViewModel)?.Measured(from, to);
        Canvas.DropHint += hint => (DataContext as EditorViewModel)?.Hint(hint);
        View3D.DropHint += hint => (DataContext as EditorViewModel)?.Hint(hint);
        Canvas.CornersChanged += c => (DataContext as EditorViewModel)?.Corners(c);
        Canvas.GhostChanged += (fit, at) =>
        {
            (DataContext as EditorViewModel)?.Ghost(fit);
            View3D.ShowGhost(fit is { } f ? (at, f.Floor, f.Fits) : null);
        };
    }

    /// <summary>
    /// Gives the map area to the plan, the 3D view or both (D46); a hidden view does no work. The view that shows
    /// takes the keys when it is alone, so Tab, WASD and the tool letters go where the user is looking.
    /// </summary>
    private void ApplyLayout(MapLayout layout, bool focus)
    {
        var plan = layout != MapLayout.ThreeD;
        var threeD = layout != MapLayout.Plan;
        var star = new GridLength(1, GridUnitType.Star);
        MapViews.ColumnDefinitions[0].Width = plan ? star : new GridLength(0);
        MapViews.ColumnDefinitions[1].Width = new GridLength(plan && threeD ? 4 : 0);
        MapViews.ColumnDefinitions[2].Width = threeD ? star : new GridLength(0);
        PlanPane.IsVisible = plan;
        Split3D.IsVisible = plan && threeD;
        Pane3D.IsVisible = threeD;
        View3D.IsVisible = threeD;
        if (focus && layout != MapLayout.Split) (plan ? (Control)Canvas : View3D).Focus();
    }

    /// <summary>
    /// Tab flips the 3D view to fill the map area and back, as Doom Builder's Tab does, while the plan or the 3D
    /// view has the keys; in the Inspector and the panels Tab still moves between fields.
    /// </summary>
    private void OnTabDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None || DataContext is not EditorViewModel vm) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is not null && focused != this && focused != Canvas && focused != View3D) return;
        vm.Toggle3DCommand.Execute(null);
        e.Handled = true;
    }

    // The control that had the keys before the palette opened, given them back when it closes.
    private IInputElement? _beforePalette;

    /// <summary>The palette opened: its search box takes the keys. Closed: the keys go back where they were.</summary>
    private void PaletteChanged(EditorViewModel vm)
    {
        if (vm.Palette is not null)
        {
            _beforePalette = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => PaletteSearch.Focus(), DispatcherPriority.Input);
            return;
        }

        var back = _beforePalette is Control { IsEffectivelyVisible: true } c ? c : Canvas.IsEffectivelyVisible ? Canvas : (Control)View3D;
        _beforePalette = null;
        back.Focus();
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if ((DataContext as EditorViewModel)?.Palette is not { } palette) return;
        switch (e.Key)
        {
            case Key.Up:
                palette.Move(-1);
                break;
            case Key.Down:
                palette.Move(1);
                break;
            case Key.PageUp:
                palette.Move(-8);
                break;
            case Key.PageDown:
                palette.Move(8);
                break;
            case Key.Enter:
                palette.RunSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                palette.CloseCommand.Execute(null);
                break;
            default:
                return;
        }

        if (palette.SelectedIndex >= 0) PaletteList.ScrollIntoView(palette.SelectedIndex);
        e.Handled = true;
    }

    private void OnPaletteTapped(object? sender, TappedEventArgs e)
    {
        if ((DataContext as EditorViewModel)?.Palette is { } palette && (e.Source as Control)?.DataContext is EditorCommand command) palette.RunCommand.Execute(command);
    }

    // A click outside the palette closes it; one inside it stays there.
    private void OnPaletteBackdropPressed(object? sender, PointerPressedEventArgs e) => (DataContext as EditorViewModel)?.Palette?.CloseCommand.Execute(null);

    private void OnPalettePressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>A shelf picture pressed: it is dragged onto the map (the canvas takes the drop).</summary>
    private async void OnShelfTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShelfTile tile || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ShelfDrop.Format, tile.Drop));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy).ConfigureAwait(true);
    }

    private void OnTextRowLostFocus(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as TextRow)?.Commit();

    private void OnTextRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((sender as Control)?.DataContext as TextRow)?.Commit();
        e.Handled = true;
    }
}
