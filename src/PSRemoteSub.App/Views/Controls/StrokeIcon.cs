using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PSRemoteSub.App.Views.Controls;

/// <summary>
/// A line icon drawn from an SVG path on a 24×24 grid, stroked in the current foreground colour (the design's icons
/// are stroke paths, which <see cref="PathIcon"/> would fill).
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, string?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(Size), 20);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.8);

    /// <summary>Inherited, so the icon takes the colour of the button or nav item around it.</summary>
    public static readonly AttachedProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    private Geometry? _geometry;

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, StrokeThicknessProperty, ForegroundProperty);
        AffectsMeasure<StrokeIcon>(SizeProperty);
    }

    public string? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
            _geometry = string.IsNullOrWhiteSpace(Data) ? null : Geometry.Parse(Data);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (_geometry is null || Foreground is not { } brush)
            return;
        double scale = Size / 24.0;
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round) { LineJoin = PenLineJoin.Round };
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, _geometry);
    }
}
