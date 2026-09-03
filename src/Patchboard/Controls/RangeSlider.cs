using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Patchboard.Controls;

/// <summary>
/// One track with two handles, for picking a window inside a clip.
///
/// WPF has no range slider, and the two stacked sliders this replaced were genuinely hard
/// to read: two separate tracks, each with its own full-length scale, and nothing on screen
/// showing the piece you had actually chosen. On one line the kept region is drawn between
/// the handles, so the answer to "what am I keeping" is the thing you are looking at.
///
/// The handles cannot cross. <see cref="MinimumRange"/> is the smallest window they can be
/// squeezed to, because a zero length selection decodes to nothing and a button that plays
/// silence looks broken rather than empty.
/// </summary>
[TemplatePart(Name = TrackPart, Type = typeof(Canvas))]
[TemplatePart(Name = LowerPart, Type = typeof(Thumb))]
[TemplatePart(Name = UpperPart, Type = typeof(Thumb))]
[TemplatePart(Name = SelectionPart, Type = typeof(FrameworkElement))]
public sealed class RangeSlider : Control
{
    private const string TrackPart = "PART_Track";
    private const string LowerPart = "PART_Lower";
    private const string UpperPart = "PART_Upper";
    private const string SelectionPart = "PART_Selection";

    private Canvas? _track;
    private Thumb? _lower;
    private Thumb? _upper;
    private FrameworkElement? _selection;

    static RangeSlider() =>
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RangeSlider), new FrameworkPropertyMetadata(typeof(RangeSlider)));

    public RangeSlider() => SizeChanged += (_, _) => Reposition();

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0d, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(1d, OnRangeChanged));

    /// <summary>
    /// Both values bind two way by default. They are the whole point of the control, and a
    /// one way binding on them is always a mistake rather than a choice.
    /// </summary>
    public static readonly DependencyProperty LowerValueProperty = DependencyProperty.Register(
        nameof(LowerValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(0d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangeChanged, CoerceLower));

    public static readonly DependencyProperty UpperValueProperty = DependencyProperty.Register(
        nameof(UpperValue), typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(1d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangeChanged, CoerceUpper));

    public static readonly DependencyProperty MinimumRangeProperty = DependencyProperty.Register(
        nameof(MinimumRange), typeof(double), typeof(RangeSlider), new PropertyMetadata(0.1d));

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => (double)GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => (double)GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }
    public double MinimumRange { get => (double)GetValue(MinimumRangeProperty); set => SetValue(MinimumRangeProperty, value); }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (RangeSlider)d;

        // Changing one end can invalidate the other, so both are re-coerced. Without this,
        // dragging the maximum down past a fixed upper value leaves it stranded off track.
        slider.CoerceValue(LowerValueProperty);
        slider.CoerceValue(UpperValueProperty);
        slider.Reposition();
    }

    private static object CoerceLower(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        var lower = (double)value;
        if (!double.IsFinite(lower)) return slider.Minimum;

        var ceiling = Math.Max(slider.Minimum, slider.UpperValue - slider.MinimumRange);
        return Math.Clamp(lower, slider.Minimum, ceiling);
    }

    private static object CoerceUpper(DependencyObject d, object value)
    {
        var slider = (RangeSlider)d;
        var upper = (double)value;
        if (!double.IsFinite(upper)) return slider.Maximum;

        var floor = Math.Min(slider.Maximum, slider.LowerValue + slider.MinimumRange);
        return Math.Clamp(upper, floor, slider.Maximum);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_lower is not null) _lower.DragDelta -= OnLowerDrag;
        if (_upper is not null) _upper.DragDelta -= OnUpperDrag;

        _track = GetTemplateChild(TrackPart) as Canvas;
        _lower = GetTemplateChild(LowerPart) as Thumb;
        _upper = GetTemplateChild(UpperPart) as Thumb;
        _selection = GetTemplateChild(SelectionPart) as FrameworkElement;

        if (_lower is not null) _lower.DragDelta += OnLowerDrag;
        if (_upper is not null) _upper.DragDelta += OnUpperDrag;

        Reposition();
    }

    private void OnLowerDrag(object sender, DragDeltaEventArgs e) =>
        LowerValue = ValueAt(PositionOf(LowerValue) + e.HorizontalChange);

    private void OnUpperDrag(object sender, DragDeltaEventArgs e) =>
        UpperValue = ValueAt(PositionOf(UpperValue) + e.HorizontalChange);

    /// <summary>
    /// Width the handle centres can travel across. The track is inset by half a handle at
    /// each end so a handle at either extreme sits fully on the control rather than
    /// hanging off it.
    /// </summary>
    private double Usable
    {
        get
        {
            var width = _track?.ActualWidth ?? 0;
            var handle = _lower?.Width ?? 0;
            return Math.Max(0, width - handle);
        }
    }

    private double Span => Math.Max(0.0001, Maximum - Minimum);

    private double PositionOf(double value) => (value - Minimum) / Span * Usable;

    private double ValueAt(double position)
    {
        if (Usable <= 0) return Minimum;
        return Minimum + Math.Clamp(position / Usable, 0, 1) * Span;
    }

    private void Reposition()
    {
        if (_track is null || _lower is null || _upper is null) return;

        var handle = _lower.Width;
        var lowerX = PositionOf(LowerValue);
        var upperX = PositionOf(UpperValue);

        Canvas.SetLeft(_lower, lowerX);
        Canvas.SetLeft(_upper, upperX);

        if (_selection is null) return;

        // The selection is drawn between the handle centres, not their left edges, so the
        // highlighted region lines up with what the handles are pointing at.
        _selection.Margin = new Thickness(lowerX + handle / 2, 0, 0, 0);
        _selection.Width = Math.Max(0, upperX - lowerX);
    }
}
