using System.Windows;
using System.Windows.Media;

namespace FractalViewer;

// ─────────────────────────────────────────────────────────────────────────────
//  Data types
// ─────────────────────────────────────────────────────────────────────────────

public enum FractalType { Tree, Sierpinski, Koch }

/// <summary>Everything the user can change in the UI.</summary>
public sealed record FractalSettings(
    FractalType Type,
    int Depth,
    Color StartColor,
    Color EndColor,
    double BranchAngleDegrees,
    double BranchRatio)
{
    /// <summary>
    /// True when the *shape* is identical (colors may differ). Lets the canvas re-use the
    /// expensive geometry when only colors change.
    /// </summary>
    public bool SameGeometryAs(FractalSettings? o) =>
        o is not null &&
        Type == o.Type &&
        Depth == o.Depth &&
        BranchAngleDegrees.Equals(o.BranchAngleDegrees) &&
        BranchRatio.Equals(o.BranchRatio);
}

/// <summary>
/// All shapes that share one gradient position T (0..1) are batched into ONE geometry.
/// Drawing 60,000 tiny shapes as 60,000 draw calls would be slow; ~64 batched calls is fast.
/// </summary>
public sealed record GeometryLayer(Geometry Geometry, double T, double ThicknessPx, bool Filled);

public sealed record FractalModel(IReadOnlyList<GeometryLayer> Layers, Rect Bounds);

// ─────────────────────────────────────────────────────────────────────────────
//  Depth limits
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Recursion-depth safety. The recursion STACK depth is only (depth + 1) frames, so a 1 MB
/// thread stack could hold thousands of levels. The real danger is that the amount of WORK
/// grows exponentially with depth (2^d, 3^d, 4^d), which would freeze the UI or exhaust memory
/// long before the stack overflows. So we cap depth per fractal, plus one absolute cap.
/// </summary>
public static class FractalLimits
{
    /// <summary>Hard ceiling no matter what any caller passes in.</summary>
    public const int AbsoluteMaxDepth = 20;

    public static int MaxDepth(FractalType type) => type switch
    {
        FractalType.Tree       => 14,   // 2^15 - 1 = 32,767 branches
        FractalType.Sierpinski => 10,   // 3^10     = 59,049 triangles
        FractalType.Koch       => 7,    // 3 * 4^7  = 49,152 segments
        _                      => 0
    };

    public static int DefaultDepth(FractalType type) => type switch
    {
        FractalType.Tree       => 10,
        FractalType.Sierpinski => 6,
        FractalType.Koch       => 4,
        _                      => 0
    };

    public static int Clamp(FractalType type, int depth) =>
        Math.Clamp(depth, 0, Math.Min(MaxDepth(type), AbsoluteMaxDepth));

    /// <summary>Closed-form solution of each recurrence (see the explanation in the chat).</summary>
    public static long ElementCount(FractalType type, int depth) => type switch
    {
        FractalType.Tree       => (1L << (depth + 1)) - 1,       // N(d) = 1 + 2N(d-1)  =>  2^(d+1) - 1
        FractalType.Sierpinski => (long)Math.Pow(3, depth),      // T(d) = 3T(d-1)      =>  3^d
        FractalType.Koch       => 3L * (1L << (2 * depth)),      // S(d) = 4S(d-1) x3 sides => 3*4^d
        _                      => 0
    };

    public static string ElementName(FractalType type) => type switch
    {
        FractalType.Tree       => "branches",
        FractalType.Sierpinski => "triangles",
        FractalType.Koch       => "line segments",
        _                      => "elements"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
//  Geometry collection
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Collects lines/triangles produced by the recursion into 64 gradient "buckets"
/// and tracks the overall bounding box so the view can auto-fit the result.
/// </summary>
internal sealed class GradientGeometryBuilder
{
    public const int BucketCount = 64;

    private readonly StreamGeometry?[] _geometries = new StreamGeometry?[BucketCount];
    private readonly StreamGeometryContext?[] _contexts = new StreamGeometryContext?[BucketCount];

    private double _minX = double.MaxValue, _minY = double.MaxValue;
    private double _maxX = double.MinValue, _maxY = double.MinValue;

    public void AddLine(Point a, Point b, double t)
    {
        var ctx = ContextFor(t);
        ctx.BeginFigure(a, isFilled: false, isClosed: false);
        ctx.LineTo(b, isStroked: true, isSmoothJoin: false);
        Grow(a);
        Grow(b);
    }

    public void AddTriangle(Point a, Point b, Point c, double t)
    {
        var ctx = ContextFor(t);
        ctx.BeginFigure(a, isFilled: true, isClosed: true);
        ctx.LineTo(b, isStroked: true, isSmoothJoin: false);
        ctx.LineTo(c, isStroked: true, isSmoothJoin: false);
        Grow(a);
        Grow(b);
        Grow(c);
    }

    public FractalModel Build(Func<double, double> thicknessForT, bool filled)
    {
        var layers = new List<GeometryLayer>();

        for (int i = 0; i < BucketCount; i++)
        {
            if (_contexts[i] is null) continue;

            _contexts[i]!.Close();          // finish writing the geometry
            var geometry = _geometries[i]!;
            geometry.Freeze();              // immutable => faster to render

            double t = i / (double)(BucketCount - 1);
            layers.Add(new GeometryLayer(geometry, t, thicknessForT(t), filled));
        }

        Rect bounds = _minX > _maxX
            ? new Rect(0, 0, 1, 1)
            : new Rect(new Point(_minX, _minY), new Point(_maxX, _maxY));

        return new FractalModel(layers, bounds);
    }

    private StreamGeometryContext ContextFor(double t)
    {
        int i = (int)Math.Round(Math.Clamp(t, 0.0, 1.0) * (BucketCount - 1));
        if (_contexts[i] is null)
        {
            var g = new StreamGeometry();
            _geometries[i] = g;
            _contexts[i] = g.Open();
        }
        return _contexts[i]!;
    }

    private void Grow(Point p)
    {
        if (p.X < _minX) _minX = p.X;
        if (p.X > _maxX) _maxX = p.X;
        if (p.Y < _minY) _minY = p.Y;
        if (p.Y > _maxY) _maxY = p.Y;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  The recursive algorithms
// ─────────────────────────────────────────────────────────────────────────────

public static class FractalGenerator
{
    // Coordinates here are abstract "fractal space" units (y grows downward, like the screen).
    // FractalCanvas scales and centers the result to fit the window.

    public static FractalModel Generate(FractalSettings s)
    {
        // Defense in depth: even if a caller forgot to clamp, the recursion can never run away.
        int depth = FractalLimits.Clamp(s.Type, s.Depth);
        var builder = new GradientGeometryBuilder();

        switch (s.Type)
        {
            case FractalType.Tree:
            {
                var spec = new TreeSpec(depth, s.BranchAngleDegrees * Math.PI / 180.0, s.BranchRatio);

                // Trunk starts at the origin, 100 units long, pointing straight up (90 degrees).
                BuildTree(builder, new Point(0, 0), 100.0, Math.PI / 2, 0, spec);

                // Trunk is thick, twigs are thin (linear taper by gradient position t).
                double trunkPx = 2.0 + depth * 0.8;
                return builder.Build(t => 1.0 + (trunkPx - 1.0) * (1.0 - t), filled: false);
            }

            case FractalType.Sierpinski:
            {
                const double side = 100.0;
                double height = side * Math.Sqrt(3) / 2;
                var top   = new Point(side / 2, 0);
                var right = new Point(side, height);
                var left  = new Point(0, height);

                BuildSierpinski(builder, top, right, left, depth, 0.0, 1.0);
                return builder.Build(_ => 0.0, filled: true);
            }

            case FractalType.Koch:
            {
                const double side = 100.0;
                double height = side * Math.Sqrt(3) / 2;
                var top   = new Point(side / 2, 0);
                var right = new Point(side, height);
                var left  = new Point(0, height);

                // Three Koch curves, one per side of the triangle. The vertex order
                // top -> right -> left is clockwise on screen, which makes the "bump"
                // point OUTWARD (see BuildKoch).
                BuildKoch(builder, top,   right, depth, 0.0,       1.0 / 3.0);
                BuildKoch(builder, right, left,  depth, 1.0 / 3.0, 2.0 / 3.0);
                BuildKoch(builder, left,  top,   depth, 2.0 / 3.0, 1.0);
                return builder.Build(_ => 1.5, filled: false);
            }

            default:
                return builder.Build(_ => 1.0, filled: false);
        }
    }

    // ── 1. Fractal tree ──────────────────────────────────────────────────────

    private sealed record TreeSpec(int MaxDepth, double SpreadRadians, double Ratio);

    /// <summary>
    /// Draws one branch, then asks "itself" to draw two smaller branches growing from its tip.
    ///
    ///   BASE CASE      : level > MaxDepth  -> stop (draw nothing).
    ///   RECURSIVE CASE : draw this branch, then call BuildTree twice with
    ///                      * length  = length * Ratio       (shorter),
    ///                      * heading = heading +/- Spread   (rotated left/right),
    ///                      * level   = level + 1            (progress toward the base case).
    ///
    /// Termination: 'level' increases by exactly 1 per call and MaxDepth is clamped, so the
    /// call stack is never deeper than MaxDepth + 1 frames.
    /// </summary>
    private static void BuildTree(
        GradientGeometryBuilder b, Point start, double length, double heading, int level, TreeSpec spec)
    {
        // BASE CASE
        if (level > spec.MaxDepth) return;

        // Tip of this branch (minus sign because screen Y grows downward).
        var end = new Point(
            start.X + length * Math.Cos(heading),
            start.Y - length * Math.Sin(heading));

        // Gradient position: 0 at the trunk, 1 at the outermost twigs.
        double t = spec.MaxDepth == 0 ? 0.0 : (double)level / spec.MaxDepth;
        b.AddLine(start, end, t);

        // RECURSIVE CASE: two children, each a smaller copy of the same problem.
        double childLength = length * spec.Ratio;
        BuildTree(b, end, childLength, heading + spec.SpreadRadians, level + 1, spec); // left
        BuildTree(b, end, childLength, heading - spec.SpreadRadians, level + 1, spec); // right
    }

    // ── 2. Sierpinski triangle ───────────────────────────────────────────────

    /// <summary>
    /// Splits a triangle into 4 by joining its edge midpoints, ignores the middle one
    /// (that is the "hole") and recurses into the 3 corner triangles.
    ///
    ///   BASE CASE      : level <= 0 -> fill the triangle.
    ///   RECURSIVE CASE : 3 calls, each with half the side length and level - 1.
    ///
    /// [lo, hi] is the slice of the color gradient owned by this triangle; each child gets one
    /// third of it, so the gradient flows smoothly across the whole figure.
    ///
    /// (We test "<= 0" instead of "== 0" so that a bad negative value can never recurse forever.)
    /// </summary>
    private static void BuildSierpinski(
        GradientGeometryBuilder b, Point a, Point bPt, Point c, int level, double lo, double hi)
    {
        // BASE CASE
        if (level <= 0)
        {
            b.AddTriangle(a, bPt, c, (lo + hi) / 2);
            return;
        }

        Point ab = Mid(a, bPt);
        Point bc = Mid(bPt, c);
        Point ca = Mid(c, a);

        double step = (hi - lo) / 3.0;

        // RECURSIVE CASE: three smaller copies, one per corner.
        BuildSierpinski(b, a,  ab, ca,  level - 1, lo,            lo + step);
        BuildSierpinski(b, ab, bPt, bc, level - 1, lo + step,     lo + 2 * step);
        BuildSierpinski(b, ca, bc, c,   level - 1, lo + 2 * step, hi);
    }

    private static Point Mid(Point p, Point q) => new((p.X + q.X) / 2, (p.Y + q.Y) / 2);

    // ── 3. Koch snowflake ────────────────────────────────────────────────────

    private const double Cos60 = 0.5;
    private const double SinNeg60 = -0.8660254037844386;   // sin(-60 degrees)

    /// <summary>
    /// Replaces the segment p1->p5 by four segments of 1/3 the length:
    ///
    ///        p3
    ///       /  \
    ///  p1--p2   p4--p5
    ///
    /// p3 is p2 plus the vector (p2->p4) rotated by -60 degrees, making an equilateral bump.
    ///
    ///   BASE CASE      : level <= 0 -> draw a straight line.
    ///   RECURSIVE CASE : 4 calls (p1->p2, p2->p3, p3->p4, p4->p5) with level - 1.
    /// </summary>
    private static void BuildKoch(
        GradientGeometryBuilder b, Point p1, Point p5, int level, double lo, double hi)
    {
        // BASE CASE
        if (level <= 0)
        {
            b.AddLine(p1, p5, (lo + hi) / 2);
            return;
        }

        // One third of the segment as a vector.
        double dx = (p5.X - p1.X) / 3.0;
        double dy = (p5.Y - p1.Y) / 3.0;

        var p2 = new Point(p1.X + dx, p1.Y + dy);
        var p4 = new Point(p1.X + 2 * dx, p1.Y + 2 * dy);

        // Standard 2D rotation of (dx, dy) by -60 degrees:
        //   x' = dx*cos(t) - dy*sin(t),  y' = dx*sin(t) + dy*cos(t)
        var p3 = new Point(
            p2.X + dx * Cos60 - dy * SinNeg60,
            p2.Y + dx * SinNeg60 + dy * Cos60);

        double q = (hi - lo) / 4.0;

        // RECURSIVE CASE: four smaller copies.
        BuildKoch(b, p1, p2, level - 1, lo,         lo + q);
        BuildKoch(b, p2, p3, level - 1, lo + q,     lo + 2 * q);
        BuildKoch(b, p3, p4, level - 1, lo + 2 * q, lo + 3 * q);
        BuildKoch(b, p4, p5, level - 1, lo + 3 * q, hi);
    }
}
