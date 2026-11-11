using System.Globalization;
using CopilotUsage.Core.Calculations;

namespace CopilotUsage.Web.Ui;

public enum PoolState
{
    /// <summary>No seats, so there is no pool to measure against.</summary>
    NoPool,
    Ok,
    /// <summary>At least <see cref="GaugeGeometry.WarnAtPercent"/> of the pool is gone.</summary>
    Warning,
    /// <summary>The pool is spent (used is at or past the limit); further use is overage.</summary>
    Exhausted,
}

/// <summary>Pure geometry for the radial gauge. Angles are degrees clockwise from 12 o'clock.</summary>
public static class GaugeGeometry
{
    public const decimal WarnAtPercent = 90m;

    /// <summary>The ring starts at bottom-left and sweeps 270 degrees clockwise to bottom-right.</summary>
    public const double StartDeg = 225;
    public const double SweepDeg = 270;

    public static PoolState StateOf(PoolSummary pool)
    {
        if (pool.Limit <= 0m) return PoolState.NoPool;
        if (pool.Used >= pool.Limit) return PoolState.Exhausted;
        return pool.PercentUsed >= WarnAtPercent ? PoolState.Warning : PoolState.Ok;
    }

    /// <summary>Share of the ring to fill, clamped to 0..1 (an exhausted pool is a full ring).</summary>
    public static double Fraction(decimal used, decimal limit) =>
        limit <= 0m ? 0 : (double)Math.Clamp(used / limit, 0m, 1m);

    public static (double X, double Y) Point(double cx, double cy, double r, double deg)
    {
        var rad = deg * Math.PI / 180.0;
        return (cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
    }

    /// <summary>Angle on the ring for a 0..1 fraction (clamped).</summary>
    public static double AngleAt(double fraction) => StartDeg + SweepDeg * Math.Clamp(fraction, 0, 1);

    /// <summary>SVG path for an arc from <paramref name="startDeg"/> sweeping clockwise by <paramref name="sweepDeg"/> (at most 359.99).</summary>
    public static string ArcPath(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Clamp(sweepDeg, 0.01, 359.99);
        var (x1, y1) = Point(cx, cy, r, startDeg);
        var (x2, y2) = Point(cx, cy, r, startDeg + sweepDeg);
        var large = sweepDeg > 180 ? 1 : 0;
        return string.Create(CultureInfo.InvariantCulture, $"M {x1:0.##} {y1:0.##} A {r:0.##} {r:0.##} 0 {large} 1 {x2:0.##} {y2:0.##}");
    }
}
