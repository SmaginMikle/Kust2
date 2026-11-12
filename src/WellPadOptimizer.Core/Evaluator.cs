namespace WellPadOptimizer;

public sealed class PadResult
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double N { get; set; }
    public double E { get; set; }
    public int WellCount { get; set; }
    public double Angle { get; set; }
    public double Length { get; set; }
    public double Width { get; set; }
    public Dictionary<int, double> TerrainFractions { get; set; } = new();
    public double SandVolume { get; set; }
    public double SandCost { get; set; }
    public double BuildCost { get; set; }
    public bool Forbidden { get; set; }
    /// <summary>Z-куст: два ряда устьев и два направления движения станка (НДС).</summary>
    public bool ZPad { get; set; }
    public string Layout => ZPad ? "Z-куст (2 НДС)" : "однорядный (1 НДС)";
    /// <summary>Азимут направления движения станка первого ряда, °.</summary>
    public double Nds1 => (Angle % 360 + 360) % 360;
    /// <summary>Азимут НДС второго ряда (для Z-куста — встречный), °.</summary>
    public double? Nds2 => ZPad ? (Angle + 180) % 360 : null;
    public List<string> Wells { get; set; } = new();
}

public sealed class WellResult
{
    public string Well { get; set; } = "";
    public string Uid { get; set; } = "";
    public string Pad { get; set; } = "";
    public int Slot { get; set; }
    public double WhN { get; set; }
    public double WhE { get; set; }
    public double Offset { get; set; }
    public double AzimuthTurn { get; set; }
    public Vec3 T1 { get; set; }
    public Vec3 T3 { get; set; }
    public double Horizontal { get; set; }
    public double? MdT1 { get; set; }
    public double? MdT3 { get; set; }
    public double? TangentLength { get; set; }
    public double? TangentInc { get; set; }
    public double? TangentAz { get; set; }
    public double? Cost { get; set; }
    public double? CostNNS { get; set; }
    public double? CostGS { get; set; }
    public double? HookLoad { get; set; }
    public double? Torque { get; set; }
    public double? MaxDls { get; set; }
    /// <summary>Расстояние между осями стволов в точке минимального коэффициента разделения, м.</summary>
    public double? MinSeparation { get; set; }
    /// <summary>Минимальный коэффициент разделения SF (если ниже AcWarnSF; иначе не заполняется).</summary>
    public double? MinSf { get; set; }
    /// <summary>Скважина, с которой достигается минимальный SF («(сущ.)» — существующая).</summary>
    public string AcWith { get; set; } = "";
    /// <summary>ГС бурится от T3 к T1 (выбрано расчётом).</summary>
    public bool ReversedHorizontal { get; set; }
    public List<string> Violations { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<SurveyPoint> Survey { get; set; } = new();
}

public sealed class VariantResult
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";        // input | opt
    public string Scenario { get; set; } = "";
    public int? K { get; set; }
    public string SameAs { get; set; }
    public double? OptimizerEstimate { get; set; }
    public List<PadResult> Pads { get; set; } = new();
    public List<WellResult> Wells { get; set; } = new();
    public int PadCount => Pads.Count;
    public int WellCount => Wells.Count;
    public double MdTotal => Wells.Sum(w => w.MdT3 ?? 0);
    public double DrillCost => Wells.Sum(w => w.Cost ?? 0);
    public double PadBuildCost => Pads.Sum(p => p.BuildCost);
    public double SandCost => Pads.Sum(p => p.SandCost);
    public double SandVolume => Pads.Sum(p => p.SandVolume);
    public double TotalCost => DrillCost + PadBuildCost + SandCost;
    public int Violations => Wells.Count(w => w.Violations.Count > 0) + Pads.Count(p => p.Forbidden);

    public VariantResult AliasOf(string name, int? k) => new()
    {
        Name = name, Kind = Kind, Scenario = Scenario, K = k, SameAs = Name,
        OptimizerEstimate = OptimizerEstimate, Pads = Pads, Wells = Wells,
    };
}

/// <summary>Расчёт варианта: устья по слотам, траектории, инклинометрия, нагрузки, антиколлизия, затраты.</summary>
public sealed class Evaluator
{
    private readonly Parameters _p;
    private readonly InputData _data;
    private readonly TerrainMap _map;
    private readonly List<(double s, double w)> _samples;

    public Evaluator(Parameters p, InputData data, TerrainMap map)
    {
        _p = p; _data = data; _map = map;
        _samples = Optimizer.FootprintSamples(p, p.MaxPadCapacity);
    }

    /// <param name="pads">центр куста (N, E) и индексы скважин</param>
    /// <param name="fixedWellheads">заданные устья (для исходных вариантов) или null</param>
    public VariantResult Evaluate(string name, string kind, string scenario, int? k,
        List<((double n, double e) center, List<int> wells)> pads, Dictionary<int, (double n, double e)> fixedWellheads = null)
    {
        var res = new VariantResult { Name = name, Kind = kind, Scenario = scenario, K = k };
        int padNo = _p.StartPadNumber;
        foreach (var (center, wells) in pads)
        {
            int n = wells.Count;
            double angle = 0; (bool forbidden, Dictionary<int, double> frac, double volume, double cost) fp = default;
            bool zPad = false;
            var slots = new List<(int well, double n, double e)>();
            if (fixedWellheads != null)
            {
                angle = PrincipalAngle(wells.Select(w => fixedWellheads[w]).ToList());
                fp = Optimizer.EvaluateFootprint(_p, _map, _samples, center.n, center.e, n, angle);
                foreach (var w in wells) slots.Add((w, fixedWellheads[w].n, fixedWellheads[w].e));
            }
            else
            {
                // перебор схем куста (однорядный / Z-куст) — выбирается схема с меньшими затратами
                double bestTotal = double.PositiveInfinity;
                foreach (var zz in _p.LayoutsFor(n))
                {
                    var (ang, fpz) = ChooseOrientation(center, wells, zz);
                    double uN = Math.Cos(ang * Geo.Deg), uE = Math.Sin(ang * Geo.Deg), vN = -uE, vE = uN;
                    var pos = _p.SlotPositions(n, zz)
                                .Select(q => (center.n + q.s * uN + q.w * vN, center.e + q.s * uE + q.w * vE)).ToList();
                    double U(int w) => (_data.Wells[w].T1.N - center.n) * uN + (_data.Wells[w].T1.E - center.e) * uE;
                    double V(int w) => (_data.Wells[w].T1.N - center.n) * vN + (_data.Wells[w].T1.E - center.e) * vE;
                    List<int> order;
                    if (!zz) order = wells.OrderBy(U).ThenBy(w => w).ToList();
                    else
                    {
                        // ряд 1 — скважины с целями по одну сторону от оси куста, ряд 2 — по другую; ряд 2 заполняется встречно
                        var (na, _) = _p.ZRows(n);
                        var byV = wells.OrderByDescending(V).ThenBy(w => w).ToList();
                        order = byV.Take(na).OrderBy(U).ThenBy(w => w)
                                   .Concat(byV.Skip(na).OrderByDescending(U).ThenBy(w => w)).ToList();
                    }
                    var (ord, drill) = ImproveSlots(order, pos);
                    double total = (fpz.Item1 ? 1e12 : 0) + fpz.Item4 + _p.PadBuildCost(n, zz) + drill;
                    if (total < bestTotal)
                    {
                        bestTotal = total; angle = ang; fp = fpz; zPad = zz;
                        slots = ord.Select((w, i) => (w, pos[i].Item1, pos[i].Item2)).ToList();
                    }
                }
            }

            var pad = new PadResult
            {
                Id = padNo, Name = _p.PadPrefix + padNo, N = center.n, E = center.e, WellCount = n, Angle = angle,
                Length = _p.PadDims(n, zPad).L, Width = _p.PadDims(n, zPad).W, TerrainFractions = fp.frac,
                SandVolume = fp.volume, SandCost = fp.cost, BuildCost = _p.PadBuildCost(n, zPad), Forbidden = fp.forbidden,
                ZPad = zPad,
            };
            padNo++;
            int slotNo = 1;
            foreach (var (w, sn, se) in slots)
            {
                var wh = new Vec3(sn, se, 0);
                var tgt = Oriented(_data.Wells[w], wh);
                var wr = new WellResult
                {
                    Well = tgt.Name, Uid = tgt.Uid, Pad = pad.Name, Slot = slotNo++, WhN = sn, WhE = se,
                    Offset = Geo.Hypot(tgt.T1.N - sn, tgt.T1.E - se),
                    AzimuthTurn = TrajectoryCalculator.AzimuthTurn(wh, tgt.T1, tgt.T3),
                    T1 = tgt.T1, T3 = tgt.T3, Horizontal = tgt.HorizontalLength,
                    ReversedHorizontal = !ReferenceEquals(tgt, _data.Wells[w]),
                };
                var lim = _p.LimitsToBottomHole ? tgt.T3 : tgt.T1;
                if (Geo.Hypot(lim.N - sn, lim.E - se) > _p.MaxOffset + 1e-6) wr.Violations.Add("отход");
                if (_p.MinOffsetShortfall(wh, tgt) > 1e-6) wr.Violations.Add("мин. отход");
                if (_p.UseAzimuthTurn && wr.AzimuthTurn > _p.MaxAzimuthTurn + 1e-6) wr.Violations.Add("разворот");
                var prof = TrajectoryCalculator.Solve(_p, wh, tgt.T1, tgt.T3);
                if (prof == null) wr.Violations.Add("профиль");
                else
                {
                    var sv = TrajectoryCalculator.Survey(_p, prof);
                    var (hook, tq) = TrajectoryCalculator.TorqueDrag(_p, sv, prof.MdT1);
                    double md = _p.LimitsToBottomHole ? prof.MdT3 : prof.MdT1;
                    if (md > _p.MaxMD + 1e-6) wr.Violations.Add("MD");
                    if (hook > _p.MaxHookLoad) wr.Violations.Add("крюк");
                    if (tq > _p.MaxTorque) wr.Violations.Add("момент");
                    wr.MdT1 = prof.MdT1; wr.MdT3 = prof.MdT3; wr.TangentLength = prof.TangentLength;
                    wr.TangentInc = prof.TangentInclination; wr.TangentAz = prof.TangentAzimuth;
                    wr.CostNNS = _p.CostNNS * prof.MdT1; wr.CostGS = _p.CostGS * prof.Horizontal;
                    wr.Cost = wr.CostNNS + wr.CostGS;
                    wr.HookLoad = hook; wr.Torque = tq; wr.MaxDls = sv.Max(x => x.Dls);
                    wr.Survey = sv;
                }
                pad.Wells.Add(wr.Well);
                res.Wells.Add(wr);
            }
            res.Pads.Add(pad);
        }
        CheckAntiCollision(res);
        return res;
    }

    /// <summary>Ориентация площадки: без запретных зон, минимум отсыпки, затем — поперёк среднего азимута ГС.</summary>
    private (double angle, (bool, Dictionary<int, double>, double, double) fp) ChooseOrientation((double n, double e) c, List<int> wells, bool z)
    {
        double? meanAz = null;
        double sx = 0, sy = 0; int cnt = 0;
        foreach (var w in wells)
        {
            var t = _data.Wells[w];
            if (!t.HasHorizontal) continue;
            double a = Geo.Azimuth(t.T3.N - t.T1.N, t.T3.E - t.T1.E) * Geo.Deg;
            sx += Math.Cos(2 * a); sy += Math.Sin(2 * a); cnt++;
        }
        if (cnt > 0) meanAz = ((Math.Atan2(sy, sx) / Geo.Deg / 2) % 180 + 180) % 180;
        (bool, double, double) bestKey = (true, double.MaxValue, double.MaxValue);
        double bestAng = 0; (bool, Dictionary<int, double>, double, double) bestFp = default;
        bool first = true;
        foreach (var a in _p.PadOrientations)
        {
            var fp = Optimizer.EvaluateFootprint(_p, _map, _samples, c.n, c.e, wells.Count, a, z);
            double perp = meanAz.HasValue ? Math.Abs((((a - (meanAz.Value + 90)) + 90) % 180 + 180) % 180 - 90) : 0;
            var key = (fp.forbidden, Math.Round(fp.cost), perp);
            if (first || Compare(key, bestKey) < 0) { bestKey = key; bestAng = a; bestFp = fp; first = false; }
        }
        return (bestAng, bestFp);

        static int Compare((bool f, double c, double p) x, (bool f, double c, double p) y)
        {
            if (x.f != y.f) return x.f ? 1 : -1;
            if (x.c != y.c) return x.c.CompareTo(y.c);
            return x.p.CompareTo(y.p);
        }
    }

    /// <summary>Направление бурения ГС для устья wh: если допускается обратное (T3→T1) — выбирается вариант
    /// с меньшей стоимостью с учётом штрафов за нарушения.</summary>
    private WellTarget Oriented(WellTarget t, Vec3 wh)
    {
        if (!_p.CanReverse(t)) return t;
        var r = t.Reversed();
        double a = Optimizer.SoftCost(_p, wh, t, out int na, _data.Existing), b = Optimizer.SoftCost(_p, wh, r, out int nb, _data.Existing);
        if (double.IsInfinity(a) && double.IsInfinity(b)) return nb < na ? r : t;   // профиль не строится — по числу нарушений
        return b < a ? r : t;
    }

    private static double PrincipalAngle(List<(double n, double e)> pts)
    {
        if (pts.Count < 2) return 0;
        double mn = pts.Average(p => p.n), me = pts.Average(p => p.e);
        double snn = 0, see = 0, sne = 0;
        foreach (var (n, e) in pts) { snn += (n - mn) * (n - mn); see += (e - me) * (e - me); sne += (n - mn) * (e - me); }
        double th = 0.5 * Math.Atan2(2 * sne, snn - see);          // угол главной оси от оси N
        double az = Geo.Azimuth(Math.Cos(th), Math.Sin(th));
        return az % 180;
    }

    /// <summary>
    /// Раскладка скважин по слотам: попарные перестановки, максимизирующие минимальное
    /// сближение стволов (до AntiCollisionMaxMD, по равным MD), затем минимальную стоимость бурения.
    /// </summary>
    private (List<int> order, double drill) ImproveSlots(List<int> order, List<(double, double)> pos)
    {
        int n = order.Count;
        var cache = new Dictionary<(int, int), (double[] md, Vec3[] pts, double cost, int viol)>();
        (double[] md, Vec3[] pts, double cost, int viol) GetPath(int w, int k)
        {
            if (cache.TryGetValue((w, k), out var v)) return v;
            var wh = new Vec3(pos[k].Item1, pos[k].Item2, 0);
            var t = Oriented(_data.Wells[w], wh);
            var pr = TrajectoryCalculator.Solve(_p, wh, t.T1, t.T3);
            if (pr == null) v = (null, null, double.PositiveInfinity, 9);
            else
            {
                var sv = TrajectoryCalculator.Survey(_p, pr);
                // нарушения в этом слоте: отход, мин. отход до T1, разворот, MD, сближение с существующими скважинами
                Optimizer.SoftCost(_p, wh, t, out int nv, _data.Existing);
                var near = sv.Where(x => x.MD <= _p.AntiCollisionMaxMD).ToList();
                v = (near.Select(x => x.MD).ToArray(), near.Select(x => x.Pos).ToArray(), TrajectoryCalculator.DrillCost(_p, pr), nv);
            }
            cache[(w, k)] = v;
            return v;
        }
        bool ac = _p.AntiCollisionCheck;
        (int viol, double sep, double negCost) Score(List<int> ord)
        {
            double ms = double.PositiveInfinity, cost = 0; int viol = 0;
            for (int i = 0; i < n; i++)
            {
                var a = GetPath(ord[i], i); cost += a.cost; viol += a.viol;
                if (!ac) continue;
                for (int j = i + 1; j < n; j++)
                {
                    // коэффициент разделения стволов куста по равным MD (до AntiCollisionMaxMD)
                    double sf = AntiCollision.SameMdSf(_p, a.md, a.pts, GetPath(ord[j], j).pts);
                    if (sf < _p.AcMinSF) viol++;
                    ms = Math.Min(ms, sf);
                }
            }
            return (viol, Math.Min(ms, _p.AcWarnSF), -cost);
        }
        // сначала меньше нарушений (отход, мин. отход до T1, разворот, MD, антистолкновение), затем SF, затем стоимость
        static bool Better((int v, double s, double c) x, (int v, double s, double c) y)
            => x.v < y.v || (x.v == y.v && (x.s > y.s || (x.s == y.s && x.c > y.c)));

        var cur = Score(order);
        order = new List<int>(order);
        if (n < 2) return (order, -cur.negCost + cur.viol * _p.PenaltyViolation);
        for (int pass = 0; pass < 4; pass++)
        {
            bool imp = false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    (order[i], order[j]) = (order[j], order[i]);
                    var sc = Score(order);
                    if (Better(sc, cur)) { cur = sc; imp = true; }
                    else (order[i], order[j]) = (order[j], order[i]);
                }
            if (!imp) break;
        }
        // нарушения в раскладке учитываются при выборе схемы куста (однорядный / Z)
        return (order, -cur.negCost + cur.viol * _p.PenaltyViolation);
    }

    /// <summary>
    /// Антистолкновение варианта: каждая пара проектных стволов (во всех кустах) по всей длине и каждый ствол
    /// с существующими скважинами. SF &lt; AcMinSF — нарушение «антистолкновение», SF &lt; AcWarnSF — предупреждение.
    /// </summary>
    private void CheckAntiCollision(VariantResult res)
    {
        if (!_p.AntiCollisionCheck) return;
        var ws = res.Wells;
        var paths = ws.Select(w => w.Survey.Count > 1 ? WellPath.FromSurvey(w.Well, w.Survey) : null).ToArray();
        var hits = Enumerable.Repeat(AcHit.None, ws.Count).ToArray();
        var pairs = new List<(int i, int j)>();
        for (int i = 0; i < ws.Count; i++)
            for (int j = i + 1; j < ws.Count; j++)
                if (paths[i] != null && paths[j] != null) pairs.Add((i, j));
        var found = new AcHit[pairs.Count];
        Parallel.For(0, pairs.Count, k => found[k] = AntiCollision.Pair(_p, paths[pairs[k].i], paths[pairs[k].j]));
        for (int k = 0; k < pairs.Count; k++)
        {
            var h = found[k]; if (double.IsInfinity(h.Sf)) continue;
            var (i, j) = pairs[k];
            if (h.Sf < hits[i].Sf) hits[i] = h;
            if (h.Sf < hits[j].Sf) hits[j] = new AcHit(h.Sf, h.Distance, ws[i].Well, false, h.OtherMd, h.Md);
        }
        var ex = _data.Existing;
        if (ex != null && ex.Count > 0)
        {
            var eh = new AcHit[ws.Count];
            Parallel.For(0, ws.Count, i => eh[i] = paths[i] == null ? AcHit.None : ex.Query(paths[i]));
            for (int i = 0; i < ws.Count; i++) if (eh[i].Sf < hits[i].Sf) hits[i] = eh[i];
        }
        for (int i = 0; i < ws.Count; i++)
        {
            var h = hits[i]; var w = ws[i];
            if (double.IsInfinity(h.Sf)) continue;
            w.MinSf = h.Sf; w.MinSeparation = h.Distance;
            w.AcWith = h.Other + (h.OtherExisting ? " (сущ.)" : "");
            string txt = $"SF {h.Sf:F2} с {w.AcWith}: {h.Distance:F1} м на MD {h.Md:F0} м";
            if (h.Sf < _p.AcMinSF) { w.Violations.Add("антистолкновение"); w.Warnings.Add(txt); }
            else w.Warnings.Add("сближение: " + txt);
        }
    }
}
