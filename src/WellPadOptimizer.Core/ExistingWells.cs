using System.Text.Json;

namespace WellPadOptimizer;

/// <summary>
/// Траектории существующих (пробуренных) скважин для антистолкновения.
/// Источники: раздел trajRealWells входного файла input_mass.json и файл ExistingWellsFile:
///  • JSON — массив в формате trajRealWells (или объект с этим разделом);
///  • Excel / CSV, точки траектории: «Скважина», «X», «Y», «Z» (или «TVD», «Абс. глубина»; «Абс. отметка» — высота, знак меняется),
///    необязательно «MD»;
///  • Excel / CSV, инклинометрия: «Скважина», «MD», «Зенит», «Азимут» и координаты устья «Xустья», «Yустья» (или «X», «Y»),
///    необязательно «Альтитуда» — тогда Z = TVD − альтитуда (абсолютная глубина), как в trajRealWells.
/// Координаты переводятся в систему расчёта: градусы WGS 84, СК-42 Гаусса–Крюгера с номером зоны или метры проекции расчёта.
/// Глубины Z должны быть в той же системе, что и глубины целей; поправка — ExistingWellsZShift.
/// </summary>
public static class ExistingWells
{
    /// <summary>Собрать существующие скважины из входных данных и файла; путь к файлу ищется также рядом с настройками и входным файлом.</summary>
    public static List<WellPath> Collect(Parameters p, InputData data, string inputPath, string baseDir, Action<string> log)
    {
        var list = new List<WellPath>();
        if (!p.UseExistingWells) return list;
        foreach (var r in data.RealWells)
        {
            if (r.Points.Count < 2) continue;
            var pts = r.Points.Select(q => new Vec3(q.N, q.E, q.Z + p.ExistingWellsZShift)).ToList();
            list.Add(WellPath.FromPoints(r.Name, pts, r.Md.Count == r.Points.Count ? r.Md : null));
        }
        if (list.Count > 0) log?.Invoke($"Существующие скважины из входного файла: {list.Count}");

        if (!string.IsNullOrWhiteSpace(p.ExistingWellsFile))
        {
            string path = Resolve(p.ExistingWellsFile, baseDir, inputPath);
            if (path == null) log?.Invoke($"Внимание: не найден файл существующих скважин «{p.ExistingWellsFile}» — он не учитывается.");
            else
            {
                var fromFile = Load(path, p, data, out string info);
                list.AddRange(fromFile);
                log?.Invoke($"Существующие скважины из «{Path.GetFileName(path)}»: {fromFile.Count} ({info})");
            }
        }
        return list;
    }

    private static string Resolve(string file, string baseDir, string inputPath)
    {
        if (File.Exists(file)) return Path.GetFullPath(file);
        foreach (var dir in new[] { baseDir, Path.GetDirectoryName(Path.GetFullPath(inputPath ?? ".")) })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var f = Path.Combine(dir, file);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public static List<WellPath> Load(string path, Parameters p, InputData data, out string info)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".json")
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root
                    : root.TryGetProperty("trajRealWells", out var rw) ? rw
                    : throw new InvalidDataException("В JSON нет раздела trajRealWells.");
            info = "JSON trajRealWells, координаты — в проекции расчёта";
            return InputData.ReadRealWells(arr).Where(r => r.Points.Count >= 2)
                .Select(r => WellPath.FromPoints(r.Name, r.Points.Select(q => new Vec3(q.N, q.E, q.Z + p.ExistingWellsZShift)).ToList(),
                                                 r.Md.Count == r.Points.Count ? r.Md : null)).ToList();
        }
        if (!TableLoader.IsTable(path)) throw new InvalidDataException($"Неизвестный формат файла существующих скважин: {ext}");
        var rows = ext is ".xlsx" or ".xlsm" ? TableLoader.ReadXlsx(path) : TableLoader.ReadCsv(path);
        rows = rows.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count < 2) throw new InvalidDataException("Таблица существующих скважин пуста.");
        var hdr = rows[0].Select(TableLoader.Norm).ToArray();
        int Col(params string[] names)
        {
            foreach (var n in names) { int i = Array.IndexOf(hdr, TableLoader.Norm(n)); if (i >= 0) return i; }
            return -1;
        }
        int cName = Col("Скважина", "Имя", "Name", "Well", "Подпись");
        int cX = Col("X", "Восток", "East", "E"), cY = Col("Y", "Север", "North", "N");
        int cXw = Col("Xустья", "X устья", "XWH", "WellheadX"), cYw = Col("Yустья", "Y устья", "YWH", "WellheadY");
        int cZ = Col("Z", "TVD", "Глубина", "Абс. глубина", "Абсолютная глубина", "TVDSS", "AbsoluteDepth", "Depth");
        int cEl = Col("Абс. отметка", "Абсолютная отметка", "Отметка", "Elevation");
        int cMd = Col("MD", "Глубина по стволу", "MeasuredDepth", "Measured Depth");
        int cInc = Col("Зенит", "Зенитный угол", "Inc", "Inclination");
        int cAz = Col("Азимут", "Az", "Azimuth");
        int cAlt = Col("Альтитуда", "Altitude", "Альтитуда стола ротора");
        if (cName < 0) throw new InvalidDataException("В таблице существующих скважин нет столбца «Скважина».");
        bool survey = cMd >= 0 && cInc >= 0 && cAz >= 0;
        if (!survey && (cX < 0 || cY < 0 || (cZ < 0 && cEl < 0)))
            throw new InvalidDataException("Таблица существующих скважин: нужны столбцы «X», «Y», «Z» (точки траектории) " +
                                           "или «MD», «Зенит», «Азимут» с координатами устья (инклинометрия).");

        // строки по скважинам (в порядке файла)
        var groups = new List<(string name, List<string[]> rows)>();
        var idx = new Dictionary<string, int>();
        for (int i = 1; i < rows.Count; i++)
        {
            string name = TableLoader.Cell(rows[i], cName).Trim();
            if (name.Length == 0) continue;
            if (!idx.TryGetValue(name, out int g)) { idx[name] = g = groups.Count; groups.Add((name, new List<string[]>())); }
            groups[g].rows.Add(rows[i]);
        }

        double X(string[] r) { double v = TableLoader.Num(r, survey && cXw >= 0 ? cXw : cX); return v; }
        double Y(string[] r) { double v = TableLoader.Num(r, survey && cYw >= 0 ? cYw : cY); return v; }
        var samples = groups.Select(g => (X(g.rows[0]), Y(g.rows[0]))).Where(s => !double.IsNaN(s.Item1) && !double.IsNaN(s.Item2)).ToList();
        if (samples.Count == 0) throw new InvalidDataException("В таблице существующих скважин нет координат.");
        var conv = MakeConverter(samples, p, data.ProjectedCrs, out string crsName);

        var res = new List<WellPath>();
        foreach (var (name, rr) in groups)
        {
            var pts = new List<Vec3>(); var md = new List<double>();
            if (!survey)
            {
                foreach (var r in rr)
                {
                    double x = X(r), y = Y(r);
                    double z = cZ >= 0 ? TableLoader.Num(r, cZ) : -TableLoader.Num(r, cEl);
                    if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) continue;
                    var (n, e) = conv(x, y);
                    pts.Add(new Vec3(n, e, z + p.ExistingWellsZShift));
                    md.Add(cMd >= 0 ? TableLoader.Num(r, cMd) : double.NaN);
                }
            }
            else
            {
                double x0 = X(rr[0]), y0 = Y(rr[0]);
                if (double.IsNaN(x0) || double.IsNaN(y0)) continue;
                var (n0, e0) = conv(x0, y0);
                double alt = cAlt >= 0 ? TableLoader.Num(rr[0], cAlt) : 0; if (double.IsNaN(alt)) alt = 0;
                var st = rr.Select(r => (md: TableLoader.Num(r, cMd), inc: TableLoader.Num(r, cInc), az: TableLoader.Num(r, cAz)))
                           .Where(s => !double.IsNaN(s.md) && !double.IsNaN(s.inc) && !double.IsNaN(s.az)).OrderBy(s => s.md).ToList();
                if (st.Count == 0) continue;
                if (st[0].md > 1e-6) st.Insert(0, (0, 0, 0));
                double dn = 0, de = 0, tvd = 0;
                for (int i = 0; i < st.Count; i++)
                {
                    if (i > 0)
                    {
                        var (n, e, v) = MinCurv(st[i - 1], st[i]);
                        dn += n; de += e; tvd += v;
                    }
                    pts.Add(new Vec3(n0 + dn, e0 + de, tvd - alt + p.ExistingWellsZShift));
                    md.Add(st[i].md);
                }
            }
            if (pts.Count >= 2) res.Add(WellPath.FromPoints(name, pts, md.All(m => double.IsNaN(m)) ? null : md));
        }
        info = (survey ? "инклинометрия MD/зенит/азимут" : "точки траектории") + ", координаты: " + crsName;
        return res;
    }

    /// <summary>Приращения (dN, dE, dTVD) между станциями методом минимальной кривизны.</summary>
    public static (double dn, double de, double dv) MinCurv((double md, double inc, double az) a, (double md, double inc, double az) b)
    {
        double i1 = a.inc * Geo.Deg, i2 = b.inc * Geo.Deg, a1 = a.az * Geo.Deg, a2 = b.az * Geo.Deg, dmd = b.md - a.md;
        double cosDl = Math.Cos(i2 - i1) - Math.Sin(i1) * Math.Sin(i2) * (1 - Math.Cos(a2 - a1));
        double dl = Math.Acos(Geo.Clamp1(cosDl));
        double rf = dl > 1e-9 ? 2 / dl * Math.Tan(dl / 2) : 1;
        return (dmd / 2 * (Math.Sin(i1) * Math.Cos(a1) + Math.Sin(i2) * Math.Cos(a2)) * rf,
                dmd / 2 * (Math.Sin(i1) * Math.Sin(a1) + Math.Sin(i2) * Math.Sin(a2)) * rf,
                dmd / 2 * (Math.Cos(i1) + Math.Cos(i2)) * rf);
    }

    /// <summary>
    /// Пересчёт координат таблицы (x, y) в систему расчёта (N, E): градусы WGS 84, СК-42 Гаусса–Крюгера с номером зоны
    /// или метры (считаются уже в проекции расчёта; восточная координата — меньшая по величине).
    /// </summary>
    public static Func<double, double, (double n, double e)> MakeConverter(List<(double x, double y)> samples, Parameters p, int targetEpsg, out string crsName)
    {
        bool degrees = samples.All(s => Math.Abs(s.x) <= 180 && Math.Abs(s.y) <= 90);
        double ax = samples[0].x, ay = samples[0].y;
        bool bothBig = ax >= 1e6 && ay >= 1e6;
        bool xEast = degrees || (bothBig ? (ax >= 1e7 && ay < 1e7) || (!(ay >= 1e7 && ax < 1e7) && p.TableXIsEast) : ax < ay);
        bool gk = !degrees && bothBig && TableLoader.HasZonePrefix(xEast ? ax : ay);
        if (!degrees && !gk)
        {
            crsName = targetEpsg > 0 ? $"метры, EPSG:{targetEpsg}" : "местные координаты";
            return (x, y) => xEast ? (y, x) : (x, y);
        }
        if (targetEpsg <= 0)
            throw new InvalidDataException("Координаты существующих скважин географические или СК-42, а у целей нет проекции — пересчёт невозможно выполнить.");
        var tm = TransverseMercator.FromEpsg(targetEpsg);
        if (degrees)
        {
            crsName = "WGS 84, градусы";
            return (x, y) => { var (e, n) = tm.Forward(y, x); return (n, e); };
        }
        int zone = p.TableGkZone > 0 ? p.TableGkZone : (int)Math.Floor((xEast ? ax : ay) / 1e6);
        var gkp = Datum.GaussKruger(zone);
        bool shift = p.TableDatumShift;
        crsName = $"СК-42, Гаусс–Крюгер, зона {zone}";
        return (x, y) =>
        {
            double e = xEast ? x : y, n = xEast ? y : x;
            if (e >= 1e6) e -= Math.Floor(e / 1e6) * 1e6;
            var (la, lo) = gkp.Inverse(e, n);
            if (shift) (la, lo) = Datum.Sk42ToWgs84(la, lo);
            var (ee, nn) = tm.Forward(la, lo);
            return (nn, ee);
        };
    }
}
