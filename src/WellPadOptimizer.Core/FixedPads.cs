using System.Globalization;
using System.Text;

namespace WellPadOptimizer;

/// <summary>Слот (позиция) куста с заданными координатами устья.</summary>
public sealed class FixedSlot
{
    /// <summary>Позиция в кусте, как в файле.</summary>
    public string Label { get; init; } = "";
    public double N { get; set; } = double.NaN;
    public double E { get; set; } = double.NaN;
    /// <summary>Отметка устья в системе глубин расчёта (вниз), м: 0 или −альтитуда.</summary>
    public double Z { get; set; }
    /// <summary>Передвижка от предыдущей позиции, м (если задана).</summary>
    public double Shift { get; init; } = double.NaN;
    public double Altitude { get; set; } = double.NaN;
    /// <summary>Скважина, указанная в той же строке, — закреплена за этой позицией.</summary>
    public string PresetWell { get; init; }
    public Vec3 Pos => new(N, E, Z);
}

/// <summary>Куст с заданными устьями (файл «Обезличено куст»; имя файла — имя куста).</summary>
public sealed class FixedPad
{
    public string Name { get; init; } = "";
    public string Field { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public double Altitude { get; set; } = double.NaN;
    public List<FixedSlot> Slots { get; } = new();
    /// <summary>Скважины куста без привязки к позиции (распределяются по свободным позициям расчётом).</summary>
    public List<string> Wells { get; } = new();
}

/// <summary>
/// Загрузка кустов с заданными устьями. Структура файла (Excel или CSV), первая строка — заголовки:
/// «Название месторождения», «Номер куста», «Позиция в кусте», «Номер скважины», «X устья», «Y устья», «Передвижка», «Альтитуда».
/// Строки с позицией (и координатами) — слоты куста; строки только с номером скважины — скважины, которые бурятся с этого куста.
/// Если в строке есть и позиция, и скважина — скважина закреплена за позицией. Позиция без координат ставится от предыдущей
/// по передвижке вдоль ряда. Координаты: СК-42 Гаусса–Крюгера (X — север, Y — восток с номером зоны), WGS 84 или метры проекции расчёта.
/// </summary>
public static class FixedPadLoader
{
    /// <summary>Список файлов по строке настроек: пути через «;» или «|», папки (берутся все .xlsx, .xlsm, .csv).</summary>
    public static List<string> ResolveFiles(string spec, string baseDir, string inputPath, Action<string> log = null)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(spec)) return res;
        var dirs = new List<string> { "" };
        if (!string.IsNullOrEmpty(baseDir)) dirs.Add(baseDir);
        if (!string.IsNullOrEmpty(inputPath)) dirs.Add(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? "");
        foreach (var raw in spec.Split(new[] { ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string item = raw.Trim('"');
            string found = null;
            foreach (var d in dirs)
            {
                string f = d.Length == 0 ? item : Path.Combine(d, item);
                if (File.Exists(f) || Directory.Exists(f)) { found = Path.GetFullPath(f); break; }
            }
            if (found == null) { log?.Invoke($"Внимание: не найден файл или папка кустов «{item}»."); continue; }
            if (Directory.Exists(found))
                res.AddRange(Directory.EnumerateFiles(found)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".xlsx" or ".xlsm" or ".csv"
                                && !Path.GetFileName(f).StartsWith("~$"))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else res.Add(found);
        }
        return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Заголовок: строчные буквы и цифры (без пробелов, неразрывных пробелов, знаков препинания).</summary>
    private static string H(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in (s ?? "").ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    public static List<FixedPad> Load(IEnumerable<string> files, Parameters p, InputData data, Action<string> log)
    {
        var pads = new List<FixedPad>();
        foreach (var file in files)
        {
            try { pads.Add(LoadOne(file, p, data, log)); }
            catch (Exception ex) { throw new InvalidDataException($"Файл куста «{Path.GetFileName(file)}»: {ex.Message}", ex); }
        }
        var dup = pads.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) log?.Invoke($"Внимание: одинаковые имена кустов: {string.Join(", ", dup)}.");
        return pads;
    }

    private static FixedPad LoadOne(string file, Parameters p, InputData data, Action<string> log)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var rows = (ext is ".xlsx" or ".xlsm" ? TableLoader.ReadXlsx(file) : TableLoader.ReadCsv(file))
                   .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        // строка заголовков — первая, где есть позиция или координаты устья
        int hi = rows.FindIndex(r => r.Select(H).Any(h => h.StartsWith("позиция") || h.StartsWith("xустья") || h.StartsWith("хустья")));
        if (hi < 0) throw new InvalidDataException("не найдена строка заголовков («Позиция в кусте», «X устья», «Y устья»).");
        var hdr = rows[hi].Select(H).ToArray();
        int Col(Func<string, bool> f) => Array.FindIndex(hdr, f);
        int cField = Col(h => h.StartsWith("названиеместорождения") || h == "месторождение");
        int cPad = Col(h => h.StartsWith("номеркуста") || h == "куст");
        int cPos = Col(h => h.StartsWith("позиция"));
        int cWell = Col(h => h.StartsWith("номерскважины") || h == "скважина" || h == "имя");
        int cX = Col(h => h.StartsWith("xустья") || h.StartsWith("хустья") || h == "x" || h == "х");
        int cY = Col(h => h.StartsWith("yустья") || h.StartsWith("уустья") || h == "y" || h == "у");
        int cShift = Col(h => h.StartsWith("передвижка"));
        int cAlt = Col(h => h.StartsWith("альтитуда"));
        if (cX < 0 || cY < 0) throw new InvalidDataException("нет столбцов «X устья» и «Y устья».");

        string name = Path.GetFileNameWithoutExtension(file).Trim();
        var data0 = rows.Skip(hi + 1).ToList();
        string field = cField >= 0 ? data0.Select(r => TableLoader.Cell(r, cField).Trim()).FirstOrDefault(s => s.Length > 0) ?? "" : "";
        var pad = new FixedPad { Name = name, Field = field, SourceFile = file };
        if (cPad >= 0)
        {
            var nums = data0.Select(r => TableLoader.Cell(r, cPad).Trim()).Where(s => s.Length > 0).Distinct().ToList();
            if (nums.Count > 0 && !nums.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                log?.Invoke($"  куст «{name}»: в файле «Номер куста» = {string.Join(", ", nums)}; имя куста взято из имени файла.");
        }

        var raw = new List<(string label, double x, double y, double shift, double alt, string well)>();
        foreach (var r in data0)
        {
            string pos = TableLoader.Cell(r, cPos).Trim();
            string well = cWell >= 0 ? TableLoader.Cell(r, cWell).Trim() : "";
            double x = TableLoader.Num(r, cX), y = TableLoader.Num(r, cY);
            double sh = cShift >= 0 ? TableLoader.Num(r, cShift) : double.NaN;
            double alt = cAlt >= 0 ? TableLoader.Num(r, cAlt) : double.NaN;
            bool hasXY = !double.IsNaN(x) && !double.IsNaN(y);
            if (pos.Length > 0 || hasXY) raw.Add((pos.Length > 0 ? pos : (raw.Count + 1).ToString(CultureInfo.InvariantCulture), x, y, sh, alt, well.Length > 0 ? well : null));
            else if (well.Length > 0) pad.Wells.Add(well);
        }
        if (raw.Count == 0) throw new InvalidDataException("в файле нет позиций куста.");

        // пересчёт координат в систему расчёта
        var samples = raw.Where(q => !double.IsNaN(q.x) && !double.IsNaN(q.y)).Select(q => (q.x, q.y)).ToList();
        if (samples.Count == 0) throw new InvalidDataException("ни у одной позиции нет координат устья.");
        var conv = ExistingWells.MakeConverter(samples, p, data.ProjectedCrs, out string crs);
        foreach (var q in raw)
        {
            var s = new FixedSlot { Label = q.label, Shift = q.shift, Altitude = q.alt, PresetWell = q.well };
            if (!double.IsNaN(q.x) && !double.IsNaN(q.y)) { var (n, e) = conv(q.x, q.y); s.N = n; s.E = e; }
            pad.Slots.Add(s);
        }
        FillByShift(pad, p, log);

        // альтитуда: общая для куста — первая заданная
        double palt = pad.Slots.Select(s => s.Altitude).FirstOrDefault(a => !double.IsNaN(a), double.NaN);
        pad.Altitude = palt;
        foreach (var s in pad.Slots)
        {
            if (double.IsNaN(s.Altitude)) s.Altitude = palt;
            s.Z = p.DepthsFromSeaLevel && !double.IsNaN(s.Altitude) ? -s.Altitude : 0;
        }

        // повторы скважин
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dups = new List<string>();
        foreach (var w in pad.Slots.Where(s => s.PresetWell != null).Select(s => s.PresetWell).Concat(pad.Wells).ToList())
            if (!seen.Add(w)) dups.Add(w);
        if (dups.Count > 0)
        {
            log?.Invoke($"  куст «{name}»: скважины указаны повторно и учтены один раз: {string.Join(", ", dups.Distinct())}.");
            var keep = new HashSet<string>(pad.Slots.Where(s => s.PresetWell != null).Select(s => s.PresetWell), StringComparer.OrdinalIgnoreCase);
            var list = pad.Wells.Where(w => keep.Add(w)).ToList();
            pad.Wells.Clear(); pad.Wells.AddRange(list);
        }
        log?.Invoke($"Куст «{name}» ({Path.GetFileName(file)}): позиций {pad.Slots.Count}, скважин в списке {pad.Wells.Count + pad.Slots.Count(s => s.PresetWell != null)}" +
                    $", координаты: {crs}" + (double.IsNaN(palt) ? "" : $", альтитуда {palt:0.0} м"));
        return pad;
    }

    /// <summary>Позиции без координат — от предыдущей позиции по передвижке вдоль ряда.</summary>
    private static void FillByShift(FixedPad pad, Parameters p, Action<string> log)
    {
        var s = pad.Slots;
        if (s.All(x => !double.IsNaN(x.N))) return;
        // направление ряда — по двум первым позициям с координатами
        var known = Enumerable.Range(0, s.Count).Where(i => !double.IsNaN(s[i].N)).ToList();
        double dn = 0, de = 1;
        if (known.Count >= 2)
        {
            int a = known[0], b = known[1];
            double len = Geo.Hypot(s[b].N - s[a].N, s[b].E - s[a].E);
            if (len > 1e-6) { dn = (s[b].N - s[a].N) / len; de = (s[b].E - s[a].E) / len; }
        }
        else log?.Invoke($"  куст «{pad.Name}»: направление ряда не определяется по координатам — позиции без координат ставятся на восток.");
        for (int i = 0; i < s.Count; i++)
        {
            if (!double.IsNaN(s[i].N)) continue;
            int prev = i - 1;
            while (prev >= 0 && double.IsNaN(s[prev].N)) prev--;
            if (prev < 0) continue;
            double dist = 0;
            for (int k = prev + 1; k <= i; k++) dist += double.IsNaN(s[k].Shift) ? p.DistanceInGroup : s[k].Shift;
            s[i].N = s[prev].N + dn * dist; s[i].E = s[prev].E + de * dist;
        }
        int lost = s.Count(x => double.IsNaN(x.N));
        if (lost > 0)
        {
            log?.Invoke($"  куст «{pad.Name}»: {lost} позиций без координат перед первой заданной — пропущены.");
            s.RemoveAll(x => double.IsNaN(x.N));
        }
    }
}

/// <summary>Вариант профиля для разведения стволов: заглубление точки зарезки и темп первого набора.</summary>
public readonly record struct ProfileOption(double KopShift, double Dls1)
{
    public Parameters Apply(Parameters p)
    {
        if (KopShift == 0 && Dls1 == p.Dls1) return p;
        var q = p.Clone(); q.VerticalLength = p.VerticalLength + KopShift; q.Dls1 = Dls1; return q;
    }
}

/// <summary>Скважина, назначенная на позицию куста.</summary>
public sealed class FixedAssignment
{
    public int Well { get; set; }
    public int Pad { get; set; }
    public int Slot { get; set; }
    public int Option { get; set; }
    public WellTarget Target { get; set; }
}

public sealed class FixedPlan
{
    public List<FixedPad> Pads { get; init; } = new();
    public List<FixedAssignment> Items { get; init; } = new();
    public List<ProfileOption> Options { get; init; } = new();
    public List<string> Unassigned { get; init; } = new();
}

/// <summary>
/// Расчёт траекторий по заданным устьям:
///  1. Скважины из файла куста назначаются на свободные позиции (венгерский алгоритм по стоимости с штрафами,
///     включая сближение с фактическими скважинами); скважины без куста — на позиции кустов без списка скважин.
///  2. Разведение стволов: для скважин с SF &lt; AcWarnSF перебираются перестановки позиций в кусте, перенос на свободную
///     позицию, заглубление точки зарезки (до AcKopShiftMax с шагом AcKopShiftStep) и темп первого набора Dls2.
///     Ход принимается, если уменьшает число нарушений, затем недобор SF до AcWarnSF, затем затраты.
/// </summary>
public sealed class FixedPadPlanner
{
    private readonly Parameters _p;
    private readonly InputData _data;
    private readonly Action<string> _log;
    private const double Impossible = 1e16, Dummy = 1e15;

    public FixedPadPlanner(Parameters p, InputData data, Action<string> log) { _p = p; _data = data; _log = log ?? (_ => { }); }

    private sealed record Cand(WellTarget T, double Cost, int Viol, WellPath Path, double ExSf);

    public FixedPlan Plan(List<FixedPad> pads, CancellationToken ct = default)
    {
        var options = BuildOptions();
        var optParams = options.Select(o => o.Apply(_p)).ToList();
        var slots = new List<(int pad, int slot)>();
        for (int i = 0; i < pads.Count; i++) for (int k = 0; k < pads[i].Slots.Count; k++) slots.Add((i, k));
        Vec3 Pos(int g) => pads[slots[g].pad].Slots[slots[g].slot].Pos;

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _data.Wells.Count; i++) byName.TryAdd(Key(_data.Wells[i].Name), i);
        int Find(string n) => byName.TryGetValue(Key(n), out int i) ? i : -1;

        var plan = new FixedPlan { Pads = pads, Options = options };
        var used = new HashSet<int>();
        var occupied = new bool[slots.Count];
        var missing = new List<string>();

        // кэш кандидатов (скважина, позиция, вариант профиля)
        var cache = new Dictionary<(int w, int g, int o), Cand>();
        Cand Eval(int w, int g, int o)
        {
            lock (cache) if (cache.TryGetValue((w, g, o), out var c0)) return c0;
            var pq = optParams[o]; var wh = Pos(g);
            var t = _data.Wells[w];
            if (_p.CanReverse(t))
            {
                var r = t.Reversed();
                double a = Optimizer.SoftCost(pq, wh, t, out int na, _data.Existing), b = Optimizer.SoftCost(pq, wh, r, out int nb, _data.Existing);
                if (b < a || (double.IsInfinity(a) && double.IsInfinity(b) && nb < na)) t = r;
            }
            Cand c;
            var prof = TrajectoryCalculator.Solve(pq, wh, t.T1, t.T3);
            if (prof == null) c = new Cand(t, double.PositiveInfinity, 9, null, double.PositiveInfinity);
            else
            {
                double cost = Optimizer.SoftCost(pq, wh, t, out int nv);   // без существующих — они учитываются через SF
                var path = WellPath.FromSurvey(t.Name, TrajectoryCalculator.Survey(pq, prof, _p.AcOptimizationStep));
                double ex = _p.AntiCollisionCheck && _data.Existing != null ? _data.Existing.Query(path).Sf : double.PositiveInfinity;
                c = new Cand(t, cost, nv, path, ex);
            }
            lock (cache) cache[(w, g, o)] = c;
            return c;
        }
        double AssignCost(int w, int g)
        {
            var c = Eval(w, g, 0);
            if (double.IsInfinity(c.Cost)) return Impossible;
            double pen = _p.AntiCollisionCheck && c.ExSf < _p.AcMinSF ? _p.ViolationPenalty + _p.PenaltyAntiCollision * (_p.AcMinSF - c.ExSf) : 0;
            return Math.Min(Impossible, c.Cost + pen);
        }

        // 1) закреплённые за позициями и перечисленные в файлах скважины
        for (int pi = 0; pi < pads.Count; pi++)
        {
            var pad = pads[pi];
            for (int k = 0; k < pad.Slots.Count; k++)
            {
                string pw = pad.Slots[k].PresetWell;
                if (pw == null) continue;
                int w = Find(pw);
                if (w < 0) { missing.Add($"{pw} (куст {pad.Name})"); continue; }
                if (!used.Add(w)) { _log($"Внимание: скважина {pw} указана в нескольких кустах — оставлена в первом."); continue; }
                int g = slots.IndexOf((pi, k));
                occupied[g] = true;
                plan.Items.Add(new FixedAssignment { Well = w, Pad = pi, Slot = k, Option = 0, Target = Eval(w, g, 0).T });
            }
            var listed = new List<int>();
            foreach (var n in pad.Wells)
            {
                int w = Find(n);
                if (w < 0) { missing.Add($"{n} (куст {pad.Name})"); continue; }
                if (!used.Add(w)) { _log($"Внимание: скважина {n} указана в нескольких кустах — оставлена в первом."); continue; }
                listed.Add(w);
            }
            var free = Enumerable.Range(0, slots.Count).Where(g => slots[g].pad == pi && !occupied[g]).ToList();
            if (listed.Count > free.Count)
                _log($"Внимание: в кусте «{pad.Name}» скважин ({listed.Count}) больше свободных позиций ({free.Count}) — " +
                     "лишние скважины с наибольшими затратами не рассчитываются.");
            AssignGroup(listed, free, AssignCost, occupied, plan, slots, Eval);
        }
        if (missing.Count > 0)
            _log($"Внимание: нет целей (T1/T3) во входных данных для скважин: {string.Join(", ", missing)} — они не рассчитываются.");

        // 2) скважины без куста — на позиции кустов без списка скважин
        var openPads = Enumerable.Range(0, pads.Count).Where(i => pads[i].Wells.Count == 0 && pads[i].Slots.All(s => s.PresetWell == null)).ToHashSet();
        if (_p.FixedPadsAssignUnlisted && openPads.Count > 0)
        {
            var free = Enumerable.Range(0, slots.Count).Where(g => !occupied[g] && openPads.Contains(slots[g].pad)).ToList();
            double reach = _p.MaxOffset * _p.SoftRadiusFactor;
            var cand = Enumerable.Range(0, _data.Wells.Count)
                .Where(w => !used.Contains(w) && free.Any(g => Geo.Hypot(_data.Wells[w].T1.N - Pos(g).N, _data.Wells[w].T1.E - Pos(g).E) <= reach))
                .ToList();
            if (cand.Count > 0)
            {
                int before = plan.Items.Count;
                AssignGroup(cand, free, AssignCost, occupied, plan, slots, Eval);
                foreach (var it in plan.Items.Skip(before)) used.Add(it.Well);
                _log($"Скважины без указанного куста распределены по кустам без списка скважин: {plan.Items.Count - before} из {cand.Count} в зоне досягаемости.");
            }
        }
        plan.Unassigned.AddRange(Enumerable.Range(0, _data.Wells.Count).Where(w => !used.Contains(w) || !plan.Items.Any(i => i.Well == w))
                                           .Select(w => _data.Wells[w].Name));
        if (plan.Items.Count == 0) throw new InvalidOperationException("Ни одна скважина из файлов кустов не найдена во входных данных (нет целей T1/T3).");

        // 3) разведение стволов: перестановки, свободные позиции, KOP и темп набора
        Separate(plan, slots, occupied, Eval, ct);
        return plan;
    }

    private static string Key(string n) => (n ?? "").Trim().Replace(" ", "").ToUpperInvariant();

    private List<ProfileOption> BuildOptions()
    {
        var res = new List<ProfileOption> { new(0, _p.Dls1) };
        var shifts = new List<double> { 0 };
        if (_p.AcKopShiftMax > 0 && _p.AcKopShiftStep > 0)
            for (double s = _p.AcKopShiftStep; s <= _p.AcKopShiftMax + 1e-9; s += _p.AcKopShiftStep) shifts.Add(s);
        var dls = new List<double> { _p.Dls1 };
        if (_p.AcTryDls2 && _p.Dls2 > _p.Dls1 + 1e-9) dls.Add(_p.Dls2);
        foreach (var d in dls)
            foreach (var s in shifts)
                if (!(s == 0 && d == _p.Dls1)) res.Add(new ProfileOption(s, d));
        return res;
    }

    private void AssignGroup(List<int> wells, List<int> free, Func<int, int, double> cost, bool[] occupied, FixedPlan plan,
                             List<(int pad, int slot)> slots, Func<int, int, int, Cand> eval)
    {
        if (wells.Count == 0 || free.Count == 0) return;
        var c = new double[wells.Count, free.Count];
        Parallel.For(0, wells.Count, i => { for (int j = 0; j < free.Count; j++) c[i, j] = cost(wells[i], free[j]); });
        var a = Hungarian(c);
        for (int i = 0; i < wells.Count; i++)
        {
            if (a[i] < 0 || c[i, a[i]] >= Impossible) continue;
            int g = free[a[i]];
            occupied[g] = true;
            plan.Items.Add(new FixedAssignment { Well = wells[i], Pad = slots[g].pad, Slot = slots[g].slot, Option = 0, Target = eval(wells[i], g, 0).T });
        }
    }

    /// <summary>Венгерский алгоритм (минимум суммы) для прямоугольной матрицы; −1 — строка без назначения.</summary>
    public static int[] Hungarian(double[,] cost)
    {
        int rows = cost.GetLength(0), cols = cost.GetLength(1), n = Math.Max(rows, cols);
        var a = new double[n + 1, n + 1];
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= n; j++)
                a[i, j] = i <= rows && j <= cols ? Math.Min(cost[i - 1, j - 1], Impossible) : Dummy;
        var u = new double[n + 1]; var v = new double[n + 1]; var pr = new int[n + 1]; var way = new int[n + 1];
        for (int i = 1; i <= n; i++)
        {
            pr[0] = i; int j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var usedc = new bool[n + 1];
            do
            {
                usedc[j0] = true; int i0 = pr[j0], j1 = 0; double delta = double.PositiveInfinity;
                for (int j = 1; j <= n; j++)
                {
                    if (usedc[j]) continue;
                    double cur = a[i0, j] - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (int j = 0; j <= n; j++)
                    if (usedc[j]) { u[pr[j]] += delta; v[j] -= delta; } else minv[j] -= delta;
                j0 = j1;
            } while (pr[j0] != 0);
            do { int j1 = way[j0]; pr[j0] = pr[j1]; j0 = j1; } while (j0 != 0);
        }
        var res = Enumerable.Repeat(-1, rows).ToArray();
        for (int j = 1; j <= n; j++)
            if (pr[j] >= 1 && pr[j] <= rows && j <= cols) res[pr[j] - 1] = j - 1;
        return res;
    }

    /// <summary>Локальный поиск разведения стволов.</summary>
    private void Separate(FixedPlan plan, List<(int pad, int slot)> slots, bool[] occupied, Func<int, int, int, Cand> eval, CancellationToken ct)
    {
        if (!_p.AntiCollisionCheck) return;
        var items = plan.Items;
        int n = items.Count;
        int G(FixedAssignment a) => slots.IndexOf((a.Pad, a.Slot));
        var gOf = items.Select(G).ToArray();
        var oOf = items.Select(i => i.Option).ToArray();
        var pairCache = new Dictionary<(int, int, int, int, int, int), double>();
        double PairSf(int i, int gi, int oi, int j, int gj, int oj)
        {
            int wi = items[i].Well, wj = items[j].Well;
            var key = wi < wj ? (wi, gi, oi, wj, gj, oj) : (wj, gj, oj, wi, gi, oi);
            lock (pairCache) if (pairCache.TryGetValue(key, out double v)) return v;
            var a = eval(wi, gi, oi).Path; var b = eval(wj, gj, oj).Path;
            double sf = a == null || b == null ? double.PositiveInfinity : AntiCollision.Pair(_p, a, b).Sf;
            lock (pairCache) pairCache[key] = sf;
            return sf;
        }
        var M = new double[n, n];
        Parallel.For(0, n, i => { for (int j = i + 1; j < n; j++) { double s = PairSf(i, gOf[i], oOf[i], j, gOf[j], oOf[j]); M[i, j] = s; M[j, i] = s; } });
        for (int i = 0; i < n; i++) M[i, i] = double.PositiveInfinity;

        (int viol, double deficit, double cost) Score(int[] g, int[] o, double[,] m)
        {
            int viol = 0; double def = 0, cost = 0;
            for (int i = 0; i < n; i++)
            {
                var c = eval(items[i].Well, g[i], o[i]);
                viol += c.Viol; cost += c.Cost;
                double sf = c.ExSf;
                for (int j = 0; j < n; j++) sf = Math.Min(sf, m[i, j]);
                if (sf < _p.AcMinSF) viol++;
                def += Math.Max(0, _p.AcWarnSF - sf);
            }
            return (viol, def, cost);
        }
        static bool Better((int v, double d, double c) x, (int v, double d, double c) y)
            => x.v < y.v || (x.v == y.v && (x.d < y.d - 1e-9 || (Math.Abs(x.d - y.d) <= 1e-9 && x.c < y.c - 1e-6)));

        var cur = Score(gOf, oOf, M);
        var start = cur;
        int it = 0, moves = 0;
        while (it++ < _p.FixedPadsMaxIterations)
        {
            ct.ThrowIfCancellationRequested();
            // скважины с наихудшим SF или нарушениями
            var sfNow = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sf = eval(items[i].Well, gOf[i], oOf[i]).ExSf;
                for (int j = 0; j < n; j++) sf = Math.Min(sf, M[i, j]);
                sfNow[i] = sf;
            }
            var worst = Enumerable.Range(0, n).Where(i => sfNow[i] < _p.AcWarnSF || eval(items[i].Well, gOf[i], oOf[i]).Viol > 0)
                                  .OrderBy(i => sfNow[i]).Take(12).ToList();
            if (worst.Count == 0) break;
            bool improved = false;
            foreach (int i in worst)
            {
                // ходы: (скважина, новая позиция, новый вариант)…
                var movesList = new List<List<(int idx, int g, int o)>>();
                for (int o = 0; o < plan.Options.Count; o++) if (o != oOf[i]) movesList.Add(new() { (i, gOf[i], o) });
                for (int j = 0; j < n; j++)
                    if (j != i && items[j].Pad == items[i].Pad) movesList.Add(new() { (i, gOf[j], oOf[i]), (j, gOf[i], oOf[j]) });
                for (int g = 0; g < slots.Count; g++)
                    if (!occupied[g] && slots[g].pad == items[i].Pad) movesList.Add(new() { (i, g, oOf[i]) });

                (int, double, double) bestS = cur; List<(int idx, int g, int o)> bestMove = null; double[,] bestM = null;
                foreach (var mv in movesList)
                {
                    var g2 = (int[])gOf.Clone(); var o2 = (int[])oOf.Clone();
                    foreach (var (idx, g, o) in mv) { g2[idx] = g; o2[idx] = o; }
                    if (mv.Any(x => double.IsInfinity(eval(items[x.idx].Well, x.g, x.o).Cost))) continue;
                    var m2 = (double[,])M.Clone();
                    foreach (var (idx, _, _) in mv)
                        for (int j = 0; j < n; j++)
                        {
                            if (j == idx) continue;
                            double s = PairSf(idx, g2[idx], o2[idx], j, g2[j], o2[j]);
                            m2[idx, j] = s; m2[j, idx] = s;
                        }
                    var sc = Score(g2, o2, m2);
                    if (Better(sc, bestS)) { bestS = sc; bestMove = mv; bestM = m2; }
                }
                if (bestMove == null) continue;
                foreach (var (idx, g, o) in bestMove)
                {
                    occupied[gOf[idx]] = false;
                    gOf[idx] = g; oOf[idx] = o;
                }
                foreach (var (idx, _, _) in bestMove) occupied[gOf[idx]] = true;
                M = bestM; cur = bestS; moves++; improved = true;
                break;
            }
            if (!improved) break;
        }
        for (int i = 0; i < n; i++)
        {
            items[i].Pad = slots[gOf[i]].pad; items[i].Slot = slots[gOf[i]].slot; items[i].Option = oOf[i];
            items[i].Target = eval(items[i].Well, gOf[i], oOf[i]).T;
        }
        _log($"Разведение стволов: ходов {moves}; нарушений {start.viol} → {cur.viol}, недобор SF до {_p.AcWarnSF} " +
             $"{start.deficit:F2} → {cur.deficit:F2}; изменено профилей: {oOf.Count(o => o != 0)}.");
    }
}
