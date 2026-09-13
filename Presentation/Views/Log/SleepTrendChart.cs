using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation.Views.Log;

// =============================================================================
// Lane 03 — the real 14-night sleep chart (SkiaSharp SKCanvasView).
//
// Design rules baked in here:
//  * The view paints ONLY what SleepChartModel holds — every string (axis
//    labels, day labels, the fault placeholder) arrives pre-formatted from the
//    VM through IFormatService, so Persian digits and Jalali short dates need no
//    culture logic in this file at all.
//  * RTL mirrors the chart: time flows right → left and the minute axis moves
//    to the left edge, where an RTL reader expects the value scale.
//  * Provenance is visual, never implied: Manual nights are hollow + hatched
//    (self-reported), Mock nights are soft-filled (sample data) — the same
//    honesty the rest of the app carries in words, encoded in shape.
//  * The dashed line is the user's OWN baseline (confidence-gated upstream); it
//    simply isn't drawn while the baseline is still learning. No baseline, no line.
//  * Missing nights are gaps with a hollow tick — never a drawn zero.
//  * The paint handler NEVER throws: any fault paints a token-styled placeholder.
//
// IgnorePixelScaling stays false, so Skia pre-scales the canvas: all coordinates
// below are device-independent units matching the view's Width/Height.
// =============================================================================

public sealed class SleepTrendChart : SKCanvasView
{
    // One typeface cache for the whole app — embedded TTFs are immutable, and the
    // MAUI font loader keeps its own copies private, so we stream them once here.
    private static SKTypeface? _vazirmatn;
    private static SKTypeface? _openSans;
    private static Task? _fontsLoading;

    public SleepTrendChart()
    {
        Background = Colors.Transparent;
        SizeChanged += (_, _) => InvalidateSurface();
        // The palette is resolved at paint time, so a theme switch (OS or in-app) needs nothing
        // but one repaint. Hooked defensively: whichever notification the host app wired, the
        // chart follows it, and a missing hook only costs a repaint until the next size change.
        Loaded += (_, _) => HookTheme();
        Unloaded += (_, _) => UnhookTheme();
        // The palette is read at paint time, so a theme switch (OS or in-app) only needs one
        // invalidation. Both hooks are optional: whichever the host app wired repaints the chart.
        Loaded += (_, _) => HookTheme();
        Unloaded += (_, _) => UnhookTheme();
        EnsureFontsLoaded();
        _fontsLoading?.ContinueWith(_ =>
        {
            // First paint may have used the fallback face; one invalidation switches to the real one.
            try { MainThread.BeginInvokeOnMainThread(() => InvalidateSurface()); } catch { /* page gone */ }
        }, TaskScheduler.Default);
    }

    /// <summary>Called by the host whenever the chart model (or the language) changes.</summary>
    public void RefreshChart() => InvalidateSurface();

    // ---- Fonts --------------------------------------------------------------

    private static void EnsureFontsLoaded()
    {
        if (_fontsLoading is not null) return;
        _fontsLoading = Task.Run(async () =>
        {
            try
            {
                _vazirmatn ??= await LoadTypefaceAsync("Vazirmatn-Regular.ttf");
                _openSans ??= await LoadTypefaceAsync("OpenSans-Regular.ttf");
            }
            catch { /* the platform fallback typeface is acceptable — labels stay readable */ }
        });
    }

    private static async Task<SKTypeface?> LoadTypefaceAsync(string fileName)
    {
        // The TTFs ship as MauiFont items; where they surface in the package differs per head,
        // so probe the known spellings and take the first that opens. All failures land in the
        // caller's catch and fall back to the platform typeface — labels stay readable either way.
        string[] candidates = [fileName, $"Fonts/{fileName}", $"Resources/Fonts/{fileName}"];
        foreach (var path in candidates)
        {
            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync(path);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                var face = SKTypeface.FromStream(ms);
                if (face is not null) return face;
            }
            catch { /* try the next spelling */ }
        }
        return null;
    }

    private static SKTypeface? TypefaceFor(bool persian) =>
        persian ? _vazirmatn ?? _openSans : _openSans ?? _vazirmatn;

    // ---- Token colors (theme aware, resolved at paint time) -----------------

    /// <summary>
    /// Theme actually in effect for painting. An in-app override (MAUI's UserAppTheme, set by lane
    /// 05) outranks the OS setting — reading RequestedTheme alone would paint a light palette onto
    /// a dark app the moment someone picks Dark in Settings. Unspecified means "follow the OS".
    /// </summary>
    private static bool IsDark
    {
        get
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app is null) return false;
            var theme = app.UserAppTheme == Microsoft.Maui.ApplicationModel.AppTheme.Unspecified
                ? app.RequestedTheme
                : app.UserAppTheme;
            return theme == Microsoft.Maui.ApplicationModel.AppTheme.Dark;
        }
    }

    // ---- Theme repaint wiring -------------------------------------------------
    // The palette is resolved at paint time, so a theme switch needs only one repaint.
    // The app-wide signal fires for the OS setting; an in-app override (lane 05's
    // UserAppTheme) changes the same value, so it repaints through the same hook. The
    // subscription is detached on unload, so a disposed tab never keeps the app alive.
    private EventHandler<Microsoft.Maui.Controls.AppThemeChangedEventArgs>? _osThemeHandler;
    private Microsoft.Maui.Controls.Application? _themeApp;
    private LIVORA.Application.Abstractions.IThemeService? _themeService;
    private Action<LIVORA.Domain.Enums.AppThemeKind>? _appThemeHandler;

    private void HookTheme()
    {
        if (_themeApp is null)
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (app is not null)
            {
                _osThemeHandler = (_, _) => InvalidateSurface();
                app.RequestedThemeChanged += _osThemeHandler;
                _themeApp = app;
            }
        }

        // An in-app override (lane 05's ThemeService) does not raise the OS event — it raises its
        // own. Subscribe only if the service is actually registered; absent it, the chart still
        // repaints with the page, and no assumption is made about a service that isn't there.
        if (_themeService is not null) return;
        var svc = ServiceHelper.TryGet<LIVORA.Application.Abstractions.IThemeService>();
        if (svc is null) return;
        _appThemeHandler = _ => InvalidateSurface();
        svc.ThemeChanged += _appThemeHandler;
        _themeService = svc;
    }

    private void UnhookTheme()
    {
        if (_themeApp is not null && _osThemeHandler is not null)
            _themeApp.RequestedThemeChanged -= _osThemeHandler;
        _themeApp = null;
        _osThemeHandler = null;

        if (_themeService is not null && _appThemeHandler is not null)
            _themeService.ThemeChanged -= _appThemeHandler;
        _themeService = null;
        _appThemeHandler = null;
    }

    /// <summary>Reads a LivoraColors token by key (dark mirrors use the *Dark suffix).</summary>
    private static SKColor Token(string key, SKColor fallback)
    {
        string lookup = IsDark && key is "Surface" or "SurfaceAlt" or "Overlay" or "BgPrimary"
            or "AccentSoft" or "TextPrimary" or "TextTertiary" or "TextSecondary"
            ? key + "Dark"
            : key;
        var resources = Microsoft.Maui.Controls.Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(lookup, out var value) && value is Color c)
            return ToSk(c);
        // MetricSleep is theme-stable (no Dark mirror in the token set) — plain key wins anyway.
        if (resources is not null && resources.TryGetValue(key, out var plain) && plain is Color pc)
            return ToSk(pc);
        return fallback;
    }

    private static SKColor ToSk(Color c) => new(
        (byte)Math.Round(Math.Clamp(c.Red, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.Green, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.Blue, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.Alpha, 0, 1) * 255));

    /// <summary>One-arg read: falls back to Theme.cs values if the dictionary has no key.</summary>
    private static SKColor Token(string key) => Token(key, Fallback(key));

    /// <summary>Plain-color mirrors of LivoraColors.xaml (Theme.cs is the source of truth).</summary>
    private static SKColor Fallback(string key) => key switch
    {
        "Accent" => SKColor.Parse("#3E7C6F"),
        "BgPrimary" => SKColor.Parse("#F7F6F3"),
        "Surface" => SKColor.Parse("#FFFFFF"),
        "SurfaceAlt" => SKColor.Parse("#F0EFEB"),
        "Overlay" => SKColor.Parse("#E7E5E0"),
        "TextSecondary" => SKColor.Parse("#6B6963"),
        "TextTertiary" => SKColor.Parse("#9B9890"),
        "MetricSleep" => SKColor.Parse("#5B6FA8"),
        _ => SKColors.Gray,
    };

    private static SKColor Alpha(SKColor c, byte a) => c.WithAlpha(a);

    // ---- Paint ----------------------------------------------------------------

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        try
        {
            Paint(e.Surface.Canvas, (float)Width, (float)Height);
        }
        catch (Exception)
        {
            // A chart must never take a page down: one guarded fallback pass, then silence.
            try
            {
                var canvas = e.Surface.Canvas;
                canvas.Clear(Token("SurfaceAlt", SKColor.Parse("#F0EFEB")));
                DrawCenteredNote(canvas, (float)Width, (float)Height,
                    Model?.PlaceholderText ?? "—", 1f);
            }
            catch { /* give up quietly — the card's own caption still renders above it */ }
        }
    }

    private void Paint(SKCanvas canvas, float cw, float ch)
    {
        var model = Model ?? SleepChartModel.Empty(string.Empty);
        if (cw < 40 || ch < 40) return;

        canvas.Clear(Token("Surface", SKColor.Parse("#FFFFFF")));

        var loc = ServiceHelper.TryGet<ILocalizationService>();
        bool rtl = loc?.IsRightToLeft ?? (this.FlowDirection == Microsoft.Maui.FlowDirection.RightToLeft);
        // Gentle size-up on wide windows; text stays within [0.86, 1.45] of its phone size.
        float s = Math.Clamp(cw / 400f, 0.86f, 1.45f);

        // No nights at all (store unreadable): paint the localized placeholder, nothing invented.
        if (model.Points.Count == 0)
        {
            DrawCenteredNote(canvas, cw, ch,
                string.IsNullOrEmpty(model.PlaceholderText) ? "—" : model.PlaceholderText, s);
            return;
        }

        using var paintGrid = new SKPaint { Color = Alpha(Token("Overlay"), 165), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        using var paintAxisText = new SKPaint { Color = Token("TextTertiary", SKColors.Gray), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var paintDayText = new SKPaint { Color = Token("TextTertiary", SKColors.Gray), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var paintDayToday = new SKPaint { Color = Token("TextSecondary", SKColors.Gray), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var paintBarSoft = new SKPaint { Color = Alpha(Token("MetricSleep"), 62), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var paintBarEdge = new SKPaint { Color = Alpha(Token("MetricSleep"), 150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * s };
        using var paintManualStroke = new SKPaint { Color = Token("MetricSleep", SKColors.SlateGray), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * s };
        using var paintHatch = new SKPaint { Color = Alpha(Token("MetricSleep"), 66), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f * s };
        using var paintBaseline = new SKPaint
        {
            Color = Alpha(Token("Accent"), 205), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * s,
            PathEffect = SKPathEffect.CreateDash(new float[] { 6f * s, 4.5f * s }, 0),
        };
        using var paintToday = new SKPaint { Color = Token("MetricSleep", SKColors.SlateGray), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var paintMissing = new SKPaint { Color = Alpha(Token("TextTertiary"), 120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * s };
        using var paintBase = new SKPaint { Color = Alpha(Token("Overlay"), 230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        using var paintTrend = new SKPaint { Color = Alpha(Token("MetricSleep"), 85), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f * s, StrokeCap = SKStrokeCap.Round };

        float top = 10f * s, bottom = ch - 26f * s;
        float axisGutter = 46f * s, edgePad = 8f * s;
        // RTL: the value scale rides the FAR (right) edge and time runs right-to-left; LTR mirrors.
        float plotLeft = rtl ? edgePad : axisGutter;
        float plotRight = rtl ? cw - axisGutter : cw - edgePad;
        float plotW = plotRight - plotLeft;
        if (plotW < 60 || bottom - top < 40)
        {
            DrawCenteredNote(canvas, cw, ch, model.PlaceholderText, s);
            return;
        }

        float axisMax = Math.Max(1f, (float)model.AxisMaxMinutes);
        float Y(float minutes) => bottom - Math.Clamp(minutes / axisMax, 0f, 1f) * (bottom - top);
        // index 0 = oldest night: oldest hugs the start edge for the reader's direction.
        float CenterX(int index) => rtl
            ? plotRight - (index + 0.5f) * (plotW / model.Points.Count)
            : plotLeft + (index + 0.5f) * (plotW / model.Points.Count);

        using var font = new SKFont { Size = 10.5f * s };
        var face = TypefaceFor(rtl);
        if (face is not null) font.Typeface = face;

        // --- grid + y-axis labels ------------------------------------------------
        // A gutter too narrow for the full localized label ("۸ ساعت" is wider than "8h") falls
        // back to the locale-digit short form before it falls back to nothing: the line is
        // always drawn, the number stays readable, and text is never clipped.
        float gutterRoom = axisGutter - 10f * s;
        float labelX = rtl ? plotRight + 6f * s : plotLeft - 6f * s;
        var labelAlign = rtl ? SKTextAlign.Left : SKTextAlign.Right;
        foreach (var g in model.GridLines)
        {
            float y = Y((float)g.Minutes);
            canvas.DrawLine(plotLeft, y, plotRight, y, paintGrid);
            string text = font.MeasureText(g.Label) <= gutterRoom ? g.Label
                        : font.MeasureText(g.ShortLabel) <= gutterRoom ? g.ShortLabel
                        : string.Empty;
            if (text.Length > 0)
                canvas.DrawText(text, labelX, y + 3.6f * s, labelAlign, font, paintAxisText);
        }

        // --- personal baseline (dashed) --------------------------------------------
        if (model.BaselineMinutes is { } baseMin && baseMin > 0)
            canvas.DrawLine(plotLeft, Y((float)baseMin), plotRight, Y((float)baseMin), paintBaseline);

        // --- soft trend line through consecutive data nights -------------------------
        // (SKPathBuilder is the non-obsolete Skia#4 surface; gaps break the run, so a
        // missing night never fakes a slope across itself.)
        using var trendBuilder = new SKPathBuilder();
        bool drew = false;
        float lastX = 0, lastY = 0; bool hasLast = false;
        for (int i = 0; i < model.Points.Count; i++)
        {
            if (model.Points[i].SleepMinutes is not { } mt) { hasLast = false; continue; }
            float x = CenterX(i), y = Y(mt);
            if (hasLast)
            {
                trendBuilder.MoveTo(lastX, lastY);
                trendBuilder.LineTo(x, y);
                drew = true;
            }
            lastX = x; lastY = y; hasLast = true;
        }
        if (drew)
        {
            using var trend = trendBuilder.Snapshot();
            canvas.DrawPath(trend, paintTrend);
        }

        // --- bars ---------------------------------------------------------------------
        float slot = plotW / model.Points.Count;
        float barW = Math.Min(slot * 0.62f, 30f * s);
        float radius = Math.Min(barW / 2.4f, 6f * s);
        // Thin screens label every other / every third night; today always wins its slot.
        int labelStep = slot >= 44f * s ? 1 : slot >= 30f * s ? 2 : 3;

        for (int i = 0; i < model.Points.Count; i++)
        {
            var p = model.Points[i];
            float cx = CenterX(i);

            if (p.SleepMinutes is not { } minutes)
            {
                // honest gap: a hollow tick — "no data", never "zero sleep"
                canvas.DrawCircle(cx, bottom - 3f * s, 2f * s, paintMissing);
            }
            else
            {
                float barTop = Math.Min(Y(minutes), bottom - 2f * s);
                var rect = new SKRect(cx - barW / 2, barTop, cx + barW / 2, bottom);

                if (p.Origin == DataOrigin.Manual)
                {
                    // hollow outline + diagonal hatch = self-reported (never looks measured)
                    canvas.DrawRoundRect(rect, radius, radius, paintManualStroke);
                    canvas.Save();
                    canvas.ClipRoundRect(new SKRoundRect(rect, radius, radius), SKClipOperation.Intersect, true);
                    for (float d = -rect.Height; d < rect.Width; d += 6f * s)
                        canvas.DrawLine(rect.Left + d, rect.Bottom, rect.Left + d + rect.Height, rect.Top, paintHatch);
                    canvas.Restore();
                }
                else
                {
                    // soft fill + faint edge = sample data
                    canvas.DrawRoundRect(rect, radius, radius, paintBarSoft);
                    canvas.DrawRoundRect(rect, radius, radius, paintBarEdge);
                }

                if (p.IsToday)
                    canvas.DrawCircle(cx, barTop, 3.1f * s, paintToday);
            }

            bool showLabel = i % labelStep == (model.Points.Count - 1) % labelStep || p.IsToday;
            if (showLabel && i < model.DayLabels.Count && !string.IsNullOrEmpty(model.DayLabels[i]))
                canvas.DrawText(model.DayLabels[i], cx, bottom + 15f * s, SKTextAlign.Center, font,
                    p.IsToday ? paintDayToday : paintDayText);
        }

        canvas.DrawLine(plotLeft, bottom, plotRight, bottom, paintBase);
    }

    private void DrawCenteredNote(SKCanvas canvas, float cw, float ch, string text, float s)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var paint = new SKPaint { Color = Token("TextTertiary", SKColors.Gray), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var font = new SKFont { Size = 12f * s };
        var face = TypefaceFor(ServiceHelper.TryGet<ILocalizationService>()?.IsRightToLeft ?? false);
        if (face is not null) font.Typeface = face;
        canvas.DrawText(text, cw / 2, ch / 2 + 4f * s, SKTextAlign.Center, font, paint);
    }

    // ---- Bindable model ---------------------------------------------------------

    /// <summary>The immutable paint snapshot built by <c>LogViewModel</c> (all labels included).</summary>
    public static readonly BindableProperty ModelProperty =
        BindableProperty.Create(nameof(Model), typeof(SleepChartModel), typeof(SleepTrendChart),
            propertyChanged: (b, _, _) => ((SleepTrendChart)b).InvalidateSurface());

    public SleepChartModel? Model
    {
        get => (SleepChartModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }
}
