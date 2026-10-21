using System.Windows;
using System.Windows.Media;

namespace FractalViewer;

/// <summary>
/// A lightweight drawing surface. It overrides OnRender and draws pre-built, frozen geometry,
/// which WPF can render much faster than thousands of individual Shape elements.
/// </summary>
public sealed class FractalCanvas : FrameworkElement
{
    private FractalSettings? _settings;
    private Color _background = Colors.White;
    private FractalModel? _model;       // cached geometry; rebuilt lazily when the shape changes

    public FractalCanvas()
    {
        ClipToBounds = true;
    }

    /// <summary>Called by the window whenever any control changes.</summary>
    public void Update(FractalSettings settings, Color background)
    {
        // Colors are applied at draw time, so only a change of SHAPE needs a new model.
        if (!settings.SameGeometryAs(_settings))
            _model = null;

        _settings = settings;
        _background = background;
        InvalidateVisual();             // WPF coalesces several invalidations into one render
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(_background), null, new Rect(0, 0, w, h));

        if (_settings is null || w < 20 || h < 20) return;

        _model ??= FractalGenerator.Generate(_settings);

        // Fit the fractal's bounding box into the window, keeping the aspect ratio.
        const double margin = 24;
        Rect b = _model.Bounds;
        double bw = Math.Max(b.Width, 1e-6), bh = Math.Max(b.Height, 1e-6);
        double scale = Math.Min((w - 2 * margin) / bw, (h - 2 * margin) / bh);

        var m = Matrix.Identity;
        m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));   // center on origin
        m.Scale(scale, scale);                                        // fit
        m.Translate(w / 2, h / 2);                                    // move to window center

        dc.PushTransform(new MatrixTransform(m));
        foreach (GeometryLayer layer in _model.Layers)
        {
            var brush = new SolidColorBrush(Lerp(_settings.StartColor, _settings.EndColor, layer.T));
            brush.Freeze();

            if (layer.Filled)
            {
                dc.DrawGeometry(brush, null, layer.Geometry);
            }
            else
            {
                // The transform also scales strokes, so divide to get the desired size in pixels.
                var pen = new Pen(brush, layer.ThicknessPx / scale)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();
                dc.DrawGeometry(null, pen, layer.Geometry);
            }
        }
        dc.Pop();
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
