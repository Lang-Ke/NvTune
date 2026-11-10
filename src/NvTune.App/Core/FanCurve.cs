namespace SimpleNvidiaUndervolt;

/// <summary>A multi-point fan curve: ordered (temperature °C → fan %) points, interpolated with a
/// monotonic cubic curve between them. Points below the first hold the first value; points above
/// the last hold the last.</summary>
internal sealed record FanCurve(IReadOnlyList<(int TempC, int Percent)> Points)
{
    /// <summary>A deliberately gentle recommendation: fans stay near idle until the core is warm,
    /// then ramp in small steps instead of leaping from 65% to 100% over ten degrees.</summary>
    public static readonly FanCurve Default = new(new[]
    {
        (30, 25), (45, 32), (55, 40), (65, 50), (75, 62), (85, 76), (95, 90),
    });

    public int FanPercentAt(int tempC)
    {
        var pts = Points.OrderBy(p => p.TempC).ToArray();
        if (pts.Length == 0) return 50;
        if (tempC <= pts[0].TempC) return pts[0].Percent;
        if (tempC >= pts[^1].TempC) return pts[^1].Percent;

        for (int i = 0; i < pts.Length - 1; i++)
        {
            var (t0, f0) = pts[i];
            var (t1, f1) = pts[i + 1];
            if (tempC >= t0 && tempC <= t1)
            {
                if (t1 == t0) return f1;
                return (int)Math.Round(Hermite(pts, i, tempC));
            }
        }

        return pts[^1].Percent;
    }

    private static double Hermite(IReadOnlyList<(int TempC, int Percent)> pts, int segment, double tempC)
    {
        var (t0, f0) = pts[segment];
        var (t1, f1) = pts[segment + 1];
        double h = t1 - t0;
        double d0 = Slope(pts, segment);
        double d1 = Slope(pts, segment + 1);
        double u = (tempC - t0) / h;
        double u2 = u * u;
        double u3 = u2 * u;
        return f0 * (2 * u3 - 3 * u2 + 1)
            + h * d0 * (u3 - 2 * u2 + u)
            + f1 * (-2 * u3 + 3 * u2)
            + h * d1 * (u3 - u2);
    }

    private static double Slope(IReadOnlyList<(int TempC, int Percent)> pts, int i)
    {
        if (i == 0) return PointSlope(pts, 0, 1);
        if (i == pts.Count - 1) return PointSlope(pts, pts.Count - 2, pts.Count - 1);
        double prev = PointSlope(pts, i - 1, i);
        double next = PointSlope(pts, i, i + 1);
        return Math.Sign(prev) != Math.Sign(next) ? 0 : (prev + next) / 2;
    }

    private static double PointSlope(IReadOnlyList<(int TempC, int Percent)> pts, int a, int b)
    {
        double dt = pts[b].TempC - pts[a].TempC;
        return dt == 0 ? 0 : (pts[b].Percent - pts[a].Percent) / dt;
    }
}
