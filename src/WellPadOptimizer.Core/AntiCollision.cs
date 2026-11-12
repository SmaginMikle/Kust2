namespace WellPadOptimizer;

/// <summary>Траектория ствола для проверки антистолкновения: точки (N, E, Z вниз) и MD.</summary>
public sealed class WellPath
{
    public string Name { get; init; } = "";
    /// <summary>Существующая (пробуренная) скважина.</summary>
    public bool Existing { get; init; }
    public double[] Md { get; init; } = Array.Empty<double>();
    public Vec3[] Pts { get; init; } = Array.Empty<Vec3>();
    public int Count => Pts.Length;
    public double MaxMd => Md.Length > 0 ? Md[^1] : 0;

    public static WellPath FromSurvey(string name, IList<SurveyPoint> sv) => new()
    {
        Name = name, Md = sv.Select(x => x.MD).ToArray(), Pts = sv.Select(x => new Vec3(x.N, x.E, x.TVD)).ToArray(),
    };

    /// <summary>Траектория по точкам; если MD не заданы — накопленная длина ломаной.</summary>
    public static WellPath FromPoints(string name, IList<Vec3> pts, IList<double> md = null, bool existing = true)
    {
        var m = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            m[i] = md != null && i < md.Count && !double.IsNaN(md[i]) ? md[i] : i == 0 ? 0 : m[i - 1] + (pts[i] - pts[i - 1]).Length;
        return new WellPath { Name = name, Existing = existing, Md = m, Pts = pts.ToArray() };
    }
}

/// <summary>Результат проверки сближения: минимальный коэффициент разделения, расстояние, с какой скважиной, на каком MD.</summary>
public readonly record struct AcHit(double Sf, double Distance, string Other, bool OtherExisting, double Md, double OtherMd)
{
    public static readonly AcHit None = new(double.PositiveInfinity, double.PositiveInfinity, "", false, 0, 0);
}

/// <summary>
/// Антистолкновение. Неопределённость положения ствола — шар радиусом r(MD) = AcSurfaceError + AcErrorPerKm·MD/1000
/// (упрощённая модель эллипсоида ошибок инклинометрии). Коэффициент разделения
/// SF = D / (r₁ + r₂), где D — расстояние между осями стволов (ближайшее сближение: точка — отрезок).
/// SF &lt; AcMinSF — нарушение «антистолкновение», SF &lt; AcWarnSF — предупреждение.
/// </summary>
public static class AntiCollision
{
    public static double Radius(Parameters p, double md) => p.AcSurfaceError + p.AcErrorPerKm * Math.Max(0, md) / 1000.0;

    /// <summary>Расстояние от точки q до отрезка ab и параметр проекции t ∈ [0, 1].</summary>
    public static double PointSegment(Vec3 q, Vec3 a, Vec3 b, out double t)
    {
        var ab = b - a; double l2 = ab.Dot(ab);
        t = l2 < 1e-12 ? 0 : Math.Clamp((q - a).Dot(ab) / l2, 0, 1);
        return (q - (a + t * ab)).Length;
    }

    // ---- быстрая проверка пары стволов: блоки по 8 точек с габаритами
    private const int Chunk = 8;

    private readonly record struct Box(double N0, double N1, double E0, double E1, double Z0, double Z1, double R)
    {
        public double Dist(Box o)
        {
            double dn = Math.Max(0, Math.Max(o.N0 - N1, N0 - o.N1));
            double de = Math.Max(0, Math.Max(o.E0 - E1, E0 - o.E1));
            double dz = Math.Max(0, Math.Max(o.Z0 - Z1, Z0 - o.Z1));
            return Math.Sqrt(dn * dn + de * de + dz * dz);
        }
    }

    private static Box[] Boxes(Parameters p, WellPath w)
    {
        int nb = Math.Max(0, (w.Count - 1 + Chunk - 1) / Chunk);
        var res = new Box[nb];
        for (int b = 0; b < nb; b++)
        {
            int i0 = b * Chunk, i1 = Math.Min(w.Count - 1, i0 + Chunk);
            double n0 = double.MaxValue, n1 = double.MinValue, e0 = n0, e1 = n1, z0 = n0, z1 = n1;
            for (int i = i0; i <= i1; i++)
            {
                var q = w.Pts[i];
                n0 = Math.Min(n0, q.N); n1 = Math.Max(n1, q.N); e0 = Math.Min(e0, q.E); e1 = Math.Max(e1, q.E);
                z0 = Math.Min(z0, q.Z); z1 = Math.Max(z1, q.Z);
            }
            res[b] = new Box(n0, n1, e0, e1, z0, z1, Radius(p, w.Md[i1]));
        }
        return res;
    }

    /// <summary>Минимальный SF между двумя стволами (по всей длине или до maxMd для обоих).</summary>
    public static AcHit Pair(Parameters p, WellPath a, WellPath b, double maxMd = double.PositiveInfinity)
    {
        if (a.Count < 2 || b.Count < 2) return AcHit.None;
        var ba = Boxes(p, a); var bb = Boxes(p, b);
        double best = double.PositiveInfinity, bestD = 0, mdA = 0, mdB = 0;
        double lim = p.AcWarnSF;
        for (int i = 0; i < ba.Length; i++)
        {
            if (a.Md[i * Chunk] > maxMd) break;
            for (int j = 0; j < bb.Length; j++)
            {
                if (b.Md[j * Chunk] > maxMd) break;
                double cap = Math.Min(best, lim);
                if (ba[i].Dist(bb[j]) / (ba[i].R + bb[j].R) >= cap) continue;
                // точки блока a к отрезкам блока b и наоборот
                Scan(a, i, b, j, ref best, ref bestD, ref mdA, ref mdB, false);
                Scan(b, j, a, i, ref best, ref bestD, ref mdB, ref mdA, true);
            }
        }
        return double.IsInfinity(best) ? AcHit.None : new AcHit(best, bestD, b.Name, b.Existing, mdA, mdB);

        void Scan(WellPath x, int bx, WellPath y, int by, ref double sfBest, ref double dBest, ref double mx, ref double my, bool swapped)
        {
            int x0 = bx * Chunk, x1 = Math.Min(x.Count - 1, x0 + Chunk);
            int y0 = by * Chunk, y1 = Math.Min(y.Count - 1, y0 + Chunk);
            for (int s = x0; s <= x1; s++)
            {
                if (x.Md[s] > maxMd) break;
                double rx = Radius(p, x.Md[s]);
                for (int k = y0; k < y1; k++)
                {
                    double d = PointSegment(x.Pts[s], y.Pts[k], y.Pts[k + 1], out double t);
                    double myd = y.Md[k] + t * (y.Md[k + 1] - y.Md[k]);
                    double sf = d / (rx + Radius(p, myd));
                    if (sf < sfBest) { sfBest = sf; dBest = d; mx = x.Md[s]; my = myd; }
                }
            }
        }
    }

    /// <summary>Быстрая оценка для раскладки по слотам: SF по равным MD (стволы одного куста идут почти параллельно).</summary>
    public static double SameMdSf(Parameters p, double[] mdA, Vec3[] a, Vec3[] b)
    {
        if (a == null || b == null) return double.PositiveInfinity;
        int m = Math.Min(a.Length, b.Length); double best = double.PositiveInfinity;
        for (int i = 0; i < m; i++) best = Math.Min(best, (a[i] - b[i]).Length / (2 * Radius(p, mdA[i])));
        return best;
    }
}

/// <summary>
/// Пространственный индекс существующих скважин: плоская сетка (N, E) с ячейкой CellSize, в ячейках — отрезки стволов.
/// Запрос по точке проектного ствола возвращает минимальный SF с существующими стволами в радиусе влияния.
/// </summary>
public sealed class ExistingWellIndex
{
    private readonly Parameters _p;
    private readonly List<WellPath> _wells;
    private readonly List<(int w, int k)> _segs = new();
    private readonly List<int>[] _cells;
    private readonly double _n0, _e0, _cell;
    private readonly int _nn, _ne;
    private readonly double _rMax;
    public int Count => _wells.Count;
    public IReadOnlyList<WellPath> Wells => _wells;

    public ExistingWellIndex(Parameters p, IEnumerable<WellPath> wells)
    {
        _p = p;
        _wells = wells.Where(w => w.Count >= 2).ToList();
        if (_wells.Count == 0) { _cells = Array.Empty<List<int>>(); return; }
        double n0 = _wells.Min(w => w.Pts.Min(q => q.N)), n1 = _wells.Max(w => w.Pts.Max(q => q.N));
        double e0 = _wells.Min(w => w.Pts.Min(q => q.E)), e1 = _wells.Max(w => w.Pts.Max(q => q.E));
        _cell = 100;
        while ((n1 - n0) / _cell * (e1 - e0) / _cell > 4e6) _cell *= 2;
        _n0 = n0 - _cell; _e0 = e0 - _cell;
        _nn = (int)((n1 - _n0) / _cell) + 2; _ne = (int)((e1 - _e0) / _cell) + 2;
        _cells = new List<int>[_nn * _ne];
        _rMax = AntiCollision.Radius(p, _wells.Max(w => w.MaxMd));
        for (int wi = 0; wi < _wells.Count; wi++)
        {
            var w = _wells[wi];
            for (int k = 0; k + 1 < w.Count; k++)
            {
                int id = _segs.Count; _segs.Add((wi, k));
                var a = w.Pts[k]; var b = w.Pts[k + 1];
                int i0 = I(Math.Min(a.N, b.N)), i1 = I(Math.Max(a.N, b.N)), j0 = J(Math.Min(a.E, b.E)), j1 = J(Math.Max(a.E, b.E));
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                        (_cells[i * _ne + j] ??= new List<int>()).Add(id);
            }
        }
    }

    private int I(double n) => Math.Clamp((int)((n - _n0) / _cell), 0, _nn - 1);
    private int J(double e) => Math.Clamp((int)((e - _e0) / _cell), 0, _ne - 1);

    /// <summary>Минимальный SF проектного ствола (точки с MD) с существующими скважинами.</summary>
    public AcHit Query(IReadOnlyList<double> md, IReadOnlyList<Vec3> pts)
    {
        if (_wells.Count == 0) return AcHit.None;
        double best = double.PositiveInfinity, bestD = 0, bMd = 0, oMd = 0; int bw = -1;
        for (int s = 0; s < pts.Count; s++)
        {
            var q = pts[s];
            double rq = AntiCollision.Radius(_p, md[s]);
            double R = Math.Min(best, _p.AcWarnSF) * (rq + _rMax);      // дальше этого SF заведомо больше порога
            if (q.N + R < _n0 || q.E + R < _e0) continue;
            int i0 = I(q.N - R), i1 = I(q.N + R), j0 = J(q.E - R), j1 = J(q.E + R);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    var cell = _cells[i * _ne + j];
                    if (cell == null) continue;
                    foreach (int id in cell)
                    {
                        var (wi, k) = _segs[id];
                        var w = _wells[wi];
                        var a = w.Pts[k]; var b = w.Pts[k + 1];
                        if (q.Z + R < Math.Min(a.Z, b.Z) || q.Z - R > Math.Max(a.Z, b.Z)) continue;
                        double d = AntiCollision.PointSegment(q, a, b, out double t);
                        double om = w.Md[k] + t * (w.Md[k + 1] - w.Md[k]);
                        double sf = d / (rq + AntiCollision.Radius(_p, om));
                        if (sf < best) { best = sf; bestD = d; bMd = md[s]; oMd = om; bw = wi; }
                    }
                }
        }
        return bw < 0 ? AcHit.None : new AcHit(best, bestD, _wells[bw].Name, true, bMd, oMd);
    }

    public AcHit Query(WellPath w) => Query(w.Md, w.Pts);
}
