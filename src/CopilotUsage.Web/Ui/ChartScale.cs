namespace CopilotUsage.Web.Ui;

/// <summary>Axis maths for the hand-built charts.</summary>
public static class ChartScale
{
    /// <summary>
    /// Round axis ticks from zero up to the first tick at or above <paramref name="max"/>, using steps of
    /// 1, 2, 2.5, 5 x 10^n. Roughly <paramref name="target"/> intervals.
    /// </summary>
    public static IReadOnlyList<decimal> NiceTicks(decimal max, int target = 4)
    {
        if (max <= 0m || target < 1) return [0m, 1m];
        var raw = (double)max / target;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var residual = raw / magnitude;
        var nice = residual <= 1 ? 1 : residual <= 2 ? 2 : residual <= 2.5 ? 2.5 : residual <= 5 ? 5 : 10;
        var step = (decimal)(nice * magnitude);
        var ticks = new List<decimal>();
        for (var v = 0m; ; v += step)
        {
            ticks.Add(v);
            if (v >= max) break;
        }
        return ticks;
    }

    /// <summary>Maps <paramref name="value"/> in [min,max] to [from,to]; a zero-width domain maps to <paramref name="from"/>.</summary>
    public static double Map(double value, double min, double max, double from, double to) =>
        max == min ? from : from + (value - min) / (max - min) * (to - from);
}
