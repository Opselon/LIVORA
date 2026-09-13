using System.ComponentModel;

using Shapes = Microsoft.Maui.Controls.Shapes;

namespace LIVORA.Presentation.Components;

/// <summary>
/// Pure-MAUI circular progress ring (Wave 3, lane 05): track + arc drawn as Shapes.Path
/// geometry sharing one coordinate frame — no Skia, no custom renderer. Reads as one family
/// with the ProgressTrack bar and StatChip.
///
/// Usage (XAML):
///   &lt;comp:ProgressRing Progress="{Binding Score}" WidthRequest="88" HeightRequest="88"
///                        CenterText="78%" SemanticProperties.Description="…"/&gt;
///
/// Color policy (§0.7): Shape.Stroke receives plain themed Colors installed via
/// SetAppThemeColor(light, dark) against §2 tokens, so a theme flip re-resolves the ring with no
/// geometry rebuild. No *Brush resource ever touches a Color-typed property here.
///
/// Geometry: both strokes are centered on the circle path, so the radius is
/// (min side − thickness)/2 — the stroke can never clip at the control edge. The sweep starts at
/// 12 o'clock and grows clockwise; a radial mark carries no reading direction, so the control is
/// RTL-neutral by construction (§0.6).
///
/// Animation: <see cref="ProgressTo"/> tweens through MAUI's animation loop (no timers of its
/// own); direct property sets jump instantly, which is what binding updates use.
/// </summary>
public sealed class ProgressRing : Grid
{
    public static readonly BindableProperty ProgressProperty =
        BindableProperty.Create(nameof(Progress), typeof(double), typeof(ProgressRing), 0.0,
            propertyChanged: (b, o, n) => ((ProgressRing)b).Redraw());

    public static readonly BindableProperty RingThicknessProperty =
        BindableProperty.Create(nameof(RingThickness), typeof(double), typeof(ProgressRing), 8.0,
            propertyChanged: (b, o, n) => ((ProgressRing)b).Redraw());

    public static readonly BindableProperty TrackColorProperty =
        BindableProperty.Create(nameof(TrackColor), typeof(Color), typeof(ProgressRing), null,
            propertyChanged: (b, o, n) => ((ProgressRing)b).ApplyColors());

    public static readonly BindableProperty ProgressColorProperty =
        BindableProperty.Create(nameof(ProgressColor), typeof(Color), typeof(ProgressRing), null,
            propertyChanged: (b, o, n) => ((ProgressRing)b).ApplyColors());

    public static readonly BindableProperty CenterTextProperty =
        BindableProperty.Create(nameof(CenterText), typeof(string), typeof(ProgressRing), null,
            propertyChanged: (b, o, n) => ((ProgressRing)b).OnCenterTextChanged((string?)n));

    /// <summary>0..1. Out-of-range values clamp at draw time; NaN never reaches the path math.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    /// <summary>Optional explicit override; null keeps the themed token default (Overlay/Accent).</summary>
    public Color? TrackColor
    {
        get => (Color?)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    public Color? ProgressColor
    {
        get => (Color?)GetValue(ProgressColorProperty);
        set => SetValue(ProgressColorProperty, value);
    }

    /// <summary>Optional label centered inside the ring (percent, score…). Empty = hidden.</summary>
    public string? CenterText
    {
        get => (string?)GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    private readonly Shapes.Path _track = new();
    private readonly Shapes.Path _arc = new();
    private readonly Label _centerLabel = new();

    public ProgressRing()
    {
        _arc.StrokeLineCap = Shapes.PenLineCap.Round;   // soft ends, brand language
        _track.StrokeLineCap = Shapes.PenLineCap.Flat;  // a closed ring shows no caps anyway
        _track.IsVisible = false;                      // realized with geometry (first measure)

        _centerLabel.FontSize = 12;
        _centerLabel.FontAttributes = FontAttributes.Bold;
        _centerLabel.HorizontalTextAlignment = TextAlignment.Center;
        _centerLabel.VerticalTextAlignment = TextAlignment.Center;
        _centerLabel.HorizontalOptions = LayoutOptions.Center;
        _centerLabel.VerticalOptions = LayoutOptions.Center;
        _centerLabel.LineBreakMode = LineBreakMode.TailTruncation;
        _centerLabel.MaxLines = 2;
        _centerLabel.IsVisible = false;
        _centerLabel.SetAppThemeColor(                    // Color-typed: themed Colors, never a brush
            Label.TextColorProperty,
            Token("TextPrimary", "#1C1B1A"),
            Token("TextPrimaryDark", "#ECEAE6"));

        Children.Add(_track);
        Children.Add(_arc);
        Children.Add(_centerLabel);

        SizeChanged += (_, _) => Redraw();
        ApplyColors();
    }

    /// <summary>
    /// Style for the center label (size/weight from the §2 type scale). NEVER set FontFamily here:
    /// BaseContentPage's walk applies the per-language font to Labels at runtime.
    /// </summary>
    public Style? CenterTextStyle
    {
        get => _centerLabel.Style;
        set => _centerLabel.Style = value;
    }

    /// <summary>Tween Progress to <paramref name="to"/> through MAUI's animation loop (no timers).</summary>
    public void ProgressTo(double to, uint lengthMs = 500)
    {
        double from = Progress;
        to = Clamp(to);
        if (Math.Abs(from - to) < 0.001)
        {
            Progress = to;
            return;
        }
        this.Animate("ring-progress", d => SetValue(ProgressProperty, d), from, to, 16, lengthMs,
            Easing.CubicOut, finished: (_, _) => SetValue(ProgressProperty, to));
    }

    private void OnCenterTextChanged(string? text)
    {
        _centerLabel.Text = text ?? string.Empty;
        _centerLabel.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void ApplyColors()
    {
        var lightTrack = Token("Overlay", "#E7E5E0");
        var darkTrack = Token("OverlayDark", "#3A3936");
        var lightArc = Token("Accent", "#3E7C6F");
        var darkArc = Token("AccentDark", "#7CBBA9");

        _track.SetAppThemeColor(Shapes.Shape.StrokeProperty,
            TrackColor ?? lightTrack, TrackColor ?? darkTrack);
        _arc.SetAppThemeColor(Shapes.Shape.StrokeProperty,
            ProgressColor ?? lightArc, ProgressColor ?? darkArc);
    }

    private void Redraw()
    {
        double w = Width, h = Height;
        if (w <= 0 || h <= 0) return; // pre-layout; SizeChanged re-runs us

        double stroke = Math.Max(1, RingThickness);
        double side = Math.Min(w, h);
        double r = Math.Max(1, (side - stroke) / 2.0);
        double size = 2 * r + stroke; // the square the two paths share
        double cx = size / 2.0, cy = size / 2.0;
        double p = Clamp(Progress);

        foreach (var path in new Shapes.Path[] { _track, _arc })
        {
            path.StrokeThickness = stroke;
            path.WidthRequest = path.HeightRequest = size;
            path.HorizontalOptions = path.VerticalOptions = LayoutOptions.Center;
        }
        _track.IsVisible = true;

        var top = new Point(cx, cy - r);
        _track.Data = Circle(top, cx, cy, r);

        if (p < 0.001)
        {
            _arc.Data = null;
            return;
        }

        var segments = new Shapes.PathSegmentCollection();
        if (p > 0.999)
        {
            // Full sweep = two half arcs (a single 360° arc is degenerate path math).
            var bottom = new Point(cx, cy + r);
            segments.Add(new Shapes.ArcSegment { Size = new Size(r, r), Point = bottom, SweepDirection = SweepDirection.Clockwise, IsLargeArc = false });
            segments.Add(new Shapes.ArcSegment { Size = new Size(r, r), Point = top, SweepDirection = SweepDirection.Clockwise, IsLargeArc = false });
        }
        else
        {
            // 12 o'clock start, clockwise — the ring grows the way the bar fills.
            double angle = -Math.PI / 2 + p * 2 * Math.PI;
            var end = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
            segments.Add(new Shapes.ArcSegment
            {
                Size = new Size(r, r),
                Point = end,
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = p > 0.5,
            });
        }

        _arc.Data = new Shapes.PathGeometry
        {
            Figures = new Shapes.PathFigureCollection
            {
                new Shapes.PathFigure
                {
                    StartPoint = top,
                    IsClosed = false,
                    IsFilled = false,
                    Segments = segments,
                }
            }
        };
    }

    /// <summary>A closed circle as two half arcs (a single 360° arc is degenerate path math).</summary>
    private static Shapes.Geometry Circle(Point top, double cx, double cy, double r)
    {
        var bottom = new Point(cx, cy + r);
        return new Shapes.PathGeometry
        {
            Figures = new Shapes.PathFigureCollection
            {
                new Shapes.PathFigure
                {
                    StartPoint = top,
                    IsClosed = true,
                    IsFilled = false,
                    Segments = new Shapes.PathSegmentCollection
                    {
                        new Shapes.ArcSegment { Size = new Size(r, r), Point = bottom, SweepDirection = SweepDirection.Clockwise, IsLargeArc = false },
                        new Shapes.ArcSegment { Size = new Size(r, r), Point = top,    SweepDirection = SweepDirection.Clockwise, IsLargeArc = false },
                    }
                }
            }
        };
    }

    private static Color Token(string key, string fallbackHex) =>
        Microsoft.Maui.Controls.Application.Current?.Resources is { } res
        && res.TryGetValue(key, out var v) && v is Color c ? c : Color.FromArgb(fallbackHex);

    private static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
}
