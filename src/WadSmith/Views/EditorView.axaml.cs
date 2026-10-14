using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
            vm.PutCamera = View3D.StandAt;
            vm.ViewCamera = () =>
            {
                var c = View3D.CurrentCamera;
                return (c.X, c.Y, c.Z, c.Yaw, c.Pitch);
            };
            vm.FitMap = Canvas.FitMap;
            vm.FitSelection = () => Canvas.FitTo(vm.Session.Selection.Items);
            vm.GridNow = () => Canvas.GridStepNow;
            vm.PickModel = PickModelAsync;
            vm.PickMaterial = PickMaterialAsync;
            vm.ReadClipboardPicture = ReadClipboardPictureAsync;
            vm.PickExportFile = PickExportFileAsync;
            vm.Inspector.PickPanorama = PickPanoramaAsync;
            vm.Inspector.PickGrid = PickGridAsync;
            vm.Inspector.PickMusic = PickMusicAsync;
            vm.ViewSize = () =>
            {
                var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                return ((int)Math.Round(View3D.Bounds.Width * scale), (int)Math.Round(View3D.Bounds.Height * scale));
            };
            vm.Problems.ShowRequested += elements => Canvas.FitTo(elements);
            vm.ShowElementsRequested += elements => Canvas.FitTo(elements);
            vm.PlaceRequested += (at, span) =>
            {
                Canvas.FitAround(at, span);
                View3D.LookAround(at, span / 2);
            };
            if (vm.PendingPlace is { } place)
            {
                // After the bindings hand both views the session: a landmark (D28) is shown instead of the whole map.
                vm.PendingPlace = null;
                Dispatcher.UIThread.Post(() =>
                {
                    Canvas.FitAround(place.At, place.Span);
                    View3D.LookAround(place.At, place.Span / 2);
                }, DispatcherPriority.Loaded);
            }

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditorViewModel.Layout)) ApplyLayout(vm.Layout, focus: true);
                if (e.PropertyName == nameof(EditorViewModel.Palette)) PaletteChanged(vm);
            };
        };
        AddHandler(KeyDownEvent, OnKeys, RoutingStrategies.Bubble);
        Canvas.HoverChanged += (element, at) => (DataContext as EditorViewModel)?.Hover(element, at);
        View3D.HoverChanged += element => (DataContext as EditorViewModel)?.Hover3D(element);
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
    /// The editor's keys (D14): a key the plan or the 3D view did not use itself runs the command the keymap gives it
    /// (WadSmith's keys or Doom Builder's). Letters and other plain keys work while the plan or the 3D view has the keys,
    /// so typing in a field never runs a tool; keys with Ctrl or Alt and F-keys work anywhere in the editor. During a
    /// drawing or a drag the keys wait (an F-key still plays).
    /// </summary>
    private void OnKeys(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not EditorViewModel vm || vm.Palette is not null) return;
        var fKey = e.Key is >= Key.F1 and <= Key.F24;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var onMap = focused is null || focused == this || focused == Canvas || focused == View3D;
        var plain = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0 && !fKey;
        if ((plain && !onMap) || (Canvas.IsGesturing && !fKey)) return;
        if (vm.CommandFor(e.Key, e.KeyModifiers) is not { Available: true } command) return;
        command.Run();
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
    private void OnShelfTileEntered(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is ShelfTile tile) (DataContext as EditorViewModel)?.PointedAtShelf(tile);
    }

    /// <summary>A tile moved in its list (scrolled): its picture is fetched once it is in view, if still to come (a free source's preview, D61).</summary>
    private void OnShelfTileSeen(object? sender, Avalonia.Layout.EffectiveViewportChangedEventArgs e) => WantIfSeen(sender as Control);

    /// <summary>A tile shows another entry (the list was filtered): the same check, once the layout has placed it.</summary>
    private void OnShelfTileChanged(object? sender, EventArgs e)
    {
        if (sender is Control control) Avalonia.Threading.Dispatcher.UIThread.Post(() => WantIfSeen(control), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Asks for a tile's picture when the tile is inside its list's visible part (never the whole list at once: a site is not crowded).</summary>
    private static void WantIfSeen(Control? control)
    {
        if (control?.DataContext is not ShelfTile { LoadPicture: not null, Picture: null } tile || !control.IsEffectivelyVisible) return;
        if (control.FindAncestorOfType<ScrollViewer>() is not { } list || control.TranslatePoint(default, list) is not { } at) return;
        if (new Rect(at, control.Bounds.Size).Intersects(new Rect(list.Bounds.Size))) tile.WantPicture();
    }

    private async void OnShelfTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShelfTile tile || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (tile.Activate is { } activate)
        {
            // A tile that does something (download a pack, get a free game) instead of being dragged.
            activate();
            e.Handled = true;
            return;
        }

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

    /// <summary>The file picker for "Add a 3D model or picture…": model files first, any file as the second choice.</summary>
    private async Task<string?> PickModelAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Add a 3D model or a picture as a decoration",
            AllowMultiple = false,
            FileTypeFilter = [new("3D models and voxels (OBJ, glTF, MD3, IQM, MD2, MDL, VOX, KVX)") { Patterns = ["*.obj", "*.OBJ", "*.glb", "*.GLB", "*.gltf", "*.GLTF", "*.md3", "*.MD3", "*.iqm", "*.IQM", "*.md2", "*.MD2", "*.mdl", "*.MDL", "*.vox", "*.VOX", "*.kvx", "*.KVX"] }, new("Pictures as decorations (PNG, JPEG, GIF, WebP)") { Patterns = ["*.png", "*.PNG", "*.jpg", "*.JPG", "*.jpeg", "*.JPEG", "*.gif", "*.GIF", "*.webp", "*.WEBP"] }, new("Block sets (any picture of one: NAME_side.png, NAME_top.png…)") { Patterns = ["*_side.*", "*_top.*", "*_bottom.*", "*_front.*", "*_back.*", "*_left.*", "*_right.*"] }, Avalonia.Platform.Storage.FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>The save picker for Export: the file's kind follows the name suggested (.pk3 or .wad).</summary>
    private async Task<string?> PickExportFileAsync(string suggested)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return null;
        var extension = Path.GetExtension(suggested);
        var kind = extension == ".pk3" ? "GZDoom package (.pk3)" : "Doom WAD (.wad)";
        var picked = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save the export",
            SuggestedFileName = suggested,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new(kind) { Patterns = ["*" + extension] }],
        }).ConfigureAwait(true);
        return picked?.TryGetLocalPath();
    }

    /// <summary>The file picker for "Add a picture or material…".</summary>
    private async Task<string?> PickMaterialAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Add a picture, an animation, or a material (any picture of its set)",
            AllowMultiple = false,
            FileTypeFilter = [new("Pictures and animations (PNG, JPG, GIF, WebP, TGA)") { Patterns = ["*.png", "*.PNG", "*.jpg", "*.JPG", "*.jpeg", "*.JPEG", "*.gif", "*.GIF", "*.webp", "*.WEBP", "*.tga", "*.TGA", "*.bmp", "*.BMP"] }, new("Videos, their first 10 seconds (MP4, WebM, MKV, MOV; needs ffmpeg)") { Patterns = ["*.mp4", "*.MP4", "*.webm", "*.WEBM", "*.mkv", "*.MKV", "*.mov", "*.MOV", "*.avi", "*.AVI", "*.m4v", "*.M4V"] }, Avalonia.Platform.Storage.FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>The file picker for "Make a sky from a 360° picture…".</summary>
    /// <summary>A picture on the system clipboard: a picture file copied in the file manager, else a picture copied (as PNG); null when there is none.</summary>
    private async Task<(string Name, byte[] Bytes)?> ReadClipboardPictureAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;
        if (await Avalonia.Input.Platform.ClipboardExtensions.TryGetFileAsync(clipboard).ConfigureAwait(true) is { } file && file.TryGetLocalPath() is { } path && WadSmith.Editor.Editing.Pictures.IsPicture(path))
        {
            return (System.IO.Path.GetFileName(path), await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(true));
        }

        using var bitmap = await Avalonia.Input.Platform.ClipboardExtensions.TryGetBitmapAsync(clipboard).ConfigureAwait(true);
        if (bitmap is null) return null;
        using var png = new System.IO.MemoryStream();
        bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return ("pasted.png", png.ToArray());
    }

    private async Task<string?> PickPanoramaAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Make a sky from a 360° picture",
            AllowMultiple = false,
            FileTypeFilter = [new("360° pictures (JPG, PNG, HDR)") { Patterns = ["*.jpg", "*.JPG", "*.jpeg", "*.JPEG", "*.png", "*.PNG", "*.hdr", "*.HDR", "*.webp", "*.WEBP"] }, Avalonia.Platform.Storage.FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>The file picker for "Bring in a music file…": any music GZDoom plays.</summary>
    private async Task<string?> PickMusicAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        string[] kinds = ["ogg", "mp3", "flac", "wav", "mid", "midi", "mus", "mod", "xm", "it", "s3m"];
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Bring in a music file for this map",
            AllowMultiple = false,
            FileTypeFilter = [new("Music (OGG, MP3, FLAC, WAV, MIDI, MUS, MOD, XM, IT, S3M)") { Patterns = [.. kinds.Select(k => $"*.{k}"), .. kinds.Select(k => $"*.{k.ToUpperInvariant()}")] }, Avalonia.Platform.Storage.FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>The file picker for "Bring in a Wolfenstein 3D level…": the game's MAPHEAD file.</summary>
    private async Task<string?> PickGridAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return null;
        var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Bring in a Wolfenstein 3D level: pick the game's MAPHEAD file",
            AllowMultiple = false,
            FileTypeFilter = [new("Wolfenstein 3D levels (MAPHEAD.WL6, .WL1, .SOD…)") { Patterns = ["MAPHEAD.*", "maphead.*"] }, Avalonia.Platform.Storage.FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
