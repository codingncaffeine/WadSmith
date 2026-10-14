using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using WadSmith.Editor.App;

namespace WadSmith.App.Views;

/// <summary>Read-only text that can be selected and copied, its pieces coloured by kind: comments dim, quoted text, numbers and the language's words each in a colour of the theme.</summary>
internal sealed class CodeText : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<(string Text, CodeKind Kind)>?> PiecesProperty =
        AvaloniaProperty.Register<CodeText, IReadOnlyList<(string Text, CodeKind Kind)>?>(nameof(Pieces));

    public IReadOnlyList<(string Text, CodeKind Kind)>? Pieces
    {
        get => GetValue(PiecesProperty);
        set => SetValue(PiecesProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PiecesProperty) Fill();
    }

    private void Fill()
    {
        Inlines ??= [];
        Inlines.Clear();
        foreach (var (text, kind) in Pieces ?? [])
        {
            var run = new Run(text);
            var key = kind switch
            {
                CodeKind.Comment => "Brush.Text.Tertiary",
                CodeKind.Text => "Brush.Success",
                CodeKind.Number => "Brush.Warning",
                CodeKind.Word => "Brush.Accent",
                _ => null,
            };
            if (key is not null && this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush) run.Foreground = brush;
            Inlines.Add(run);
        }
    }
}
