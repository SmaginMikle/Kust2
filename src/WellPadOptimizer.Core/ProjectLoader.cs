using System.Globalization;
using System.Text.Json;

namespace WellPadOptimizer;

/// <summary>
/// Загрузка проекта кустования КИН (JSON: version, props, wrappers, assets):
///  • цели скважин — props.Wells[]: Rating[0].Hor.Trajectory.Points (T1, T3 в градусах WGS 84),
///    глубина — Rating[0].WellBottom.Depth (если точек нет — WGS скважины, T1 = T3);
///  • варианты кустования КИН — wrappers[].Owner[] (кусты) с устьями WellHeadUTM (UTM, зона UTMzone);
///  • встроенные файлы — assets (base64), в т.ч. карта желательности .bin;
///  • параметры расчёта — props (переносятся в Parameters методом ApplyProjectSettings).
/// Координаты целей переводятся в UTM той зоны, где находится центр участка.
/// </summary>
public static class ProjectLoader
{
    public static InputData Load(JsonElement root)
    {
        var data = new InputData();
        var props = root.GetProperty("props");
        var wells = props.TryGetProperty("Wells", out var w) && w.ValueKind == JsonValueKind.Array ? w : default;
        if (wells.ValueKind != JsonValueKind.Array || wells.GetArrayLength() == 0)
            throw new InvalidDataException("В проекте нет скважин (props.Wells).");

        // ---- цели в градусах
        var raw = new List<(string uid, string name, double lon1, double lat1, double lon3, double lat3, double tvd)>();
        foreach (var x in wells.EnumerateArray())
        {
            string uid = x.TryGetProperty("Uid", out var u) ? u.ToString() : "";
            string name = x.TryGetProperty("Name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : uid;
            double lon = 0, lat = 0;
            if (x.TryGetProperty("WGS", out var g) && g.ValueKind == JsonValueKind.Object) { lon = Num(g, "x"); lat = Num(g, "y"); }
            double lon1 = lon, lat1 = lat, lon3 = lon, lat3 = lat, tvd = double.NaN;
            var rating = First(x, "Rating");
            foreach (var src in new[] { rating, x.TryGetProperty("Summary", out var sm) ? sm : default })
            {
                if (src.ValueKind != JsonValueKind.Object) continue;
                if (double.IsNaN(tvd) && src.TryGetProperty("WellBottom", out var wb) && wb.ValueKind == JsonValueKind.Object)
                {
                    double d = Num(wb, "Depth");
                    if (!double.IsNaN(d) && d > 0) tvd = d;
                }
                if (src.TryGetProperty("Hor", out var hor) && hor.ValueKind == JsonValueKind.Object &&
                    hor.TryGetProperty("Trajectory", out var tr) && tr.ValueKind == JsonValueKind.Object &&
                    tr.TryGetProperty("Points", out var pts) && pts.ValueKind == JsonValueKind.Array && pts.GetArrayLength() >= 1 &&
                    lon1 == lon && lat1 == lat && lon3 == lon && lat3 == lat)
                {
                    var p0 = pts[0]; var p1 = pts[pts.GetArrayLength() - 1];
                    lon1 = Num(p0, "x"); lat1 = Num(p0, "y"); lon3 = Num(p1, "x"); lat3 = Num(p1, "y");
                }
            }
            if (double.IsNaN(tvd)) throw new InvalidDataException($"Скважина {name}: не задана глубина (WellBottom.Depth).");
            raw.Add((uid, name, lon1, lat1, lon3, lat3, tvd));
        }

        // ---- проекция: UTM по центру участка
        double lonC = raw.Average(r => r.lon1), latC = raw.Average(r => r.lat1);
        int zone = (int)Math.Floor((lonC + 180) / 6) + 1;
        int epsg = (latC >= 0 ? 32600 : 32700) + zone;
        var tm = TransverseMercator.FromEpsg(epsg);
        string title = props.TryGetProperty("ExistingSurface", out var es) && es.ValueKind == JsonValueKind.String ? es.GetString() : "";
        data.SetProject(epsg, props, title);

        var byUid = new Dictionary<string, int>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw)
        {
            var (e1, n1) = tm.Forward(r.lat1, r.lon1);
            var (e3, n3) = tm.Forward(r.lat3, r.lon3);
            int idx = data.Wells.Count;
            data.Wells.Add(new WellTarget
            {
                Index = idx, Name = string.IsNullOrWhiteSpace(r.name) ? $"W{idx + 1:00}" : r.name, Uid = r.uid,
                T1 = new Vec3(n1, e1, r.tvd), T3 = new Vec3(n3, e3, r.tvd),
                Reversible = true,   // в проекте КИН начало горизонтального участка выбирается при расчёте (IsT1First)
            });
            if (!string.IsNullOrEmpty(r.uid)) byUid[r.uid] = idx;
            if (!string.IsNullOrEmpty(r.name)) byName[r.name] = idx;
        }

        // ---- варианты кустования из проекта
        int selected = root.TryGetProperty("selectedVariantIndex", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : -1;
        if (root.TryGetProperty("wrappers", out var wrappers) && wrappers.ValueKind == JsonValueKind.Array)
        {
            int vi = 0;
            foreach (var wr in wrappers.EnumerateArray())
            {
                var iv = new InputVariant { Id = vi, Name = $"Вариант КИН {vi + 1}" + (vi == selected ? " (выбранный в проекте)" : "") };
                if (wr.TryGetProperty("Owner", out var owners) && owners.ValueKind == JsonValueKind.Array)
                {
                    int pi = 0;
                    foreach (var pad in owners.EnumerateArray())
                    {
                        pi++;
                        if (!pad.TryGetProperty("Wells", out var pw) || pw.ValueKind != JsonValueKind.Array) continue;
                        var list = new List<(int well, double n, double e)>();
                        foreach (var x in pw.EnumerateArray())
                        {
                            string uid = x.TryGetProperty("Uid", out var u) ? u.ToString() : "";
                            string name = x.TryGetProperty("Name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : "";
                            if (!byUid.TryGetValue(uid, out int idx) && !byName.TryGetValue(name, out idx)) continue;
                            double he = double.NaN, hn = double.NaN;
                            int z = x.TryGetProperty("UTMzone", out var uz) && uz.ValueKind == JsonValueKind.Number ? uz.GetInt32() : 0;
                            if (x.TryGetProperty("WellHeadUTM", out var hu) && hu.ValueKind == JsonValueKind.Object && (z == 0 || z == zone))
                            { he = Num(hu, "x"); hn = Num(hu, "y"); }
                            else if (x.TryGetProperty("WellHeadUTM", out var hu2) && hu2.ValueKind == JsonValueKind.Object && z > 0)
                            {
                                // устье в другой зоне UTM — пересчёт через градусы
                                var other = TransverseMercator.FromEpsg((latC >= 0 ? 32600 : 32700) + z);
                                var (la, lo) = other.Inverse(Num(hu2, "x"), Num(hu2, "y"));
                                (he, hn) = tm.Forward(la, lo);
                            }
                            if (double.IsNaN(he) || double.IsNaN(hn)) continue;
                            list.Add((idx, hn, he));
                        }
                        if (list.Count > 0) iv.Pads[pi] = list;
                    }
                }
                if (iv.Pads.Count > 0 && iv.Pads.Values.Sum(l => l.Count) == data.Wells.Count) data.Variants.Add(iv);
                vi++;
            }
        }

        // ---- встроенные файлы
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object)
        {
            foreach (var a in assets.EnumerateObject())
            {
                if (a.Value.ValueKind != JsonValueKind.String) continue;
                try { data.EmbeddedFiles[Path.GetFileName(a.Name)] = Convert.FromBase64String(a.Value.GetString()); }
                catch (FormatException) { /* не base64 — пропускаем */ }
            }
        }
        return data;
    }

    /// <summary>Имя встроенной карты желательности (.bin), указанной в проекте (TopologyMap), или первой найденной.</summary>
    public static string EmbeddedMapName(InputData data)
    {
        if (data.EmbeddedFiles.Count == 0) return null;
        if (data.ProjectProps is JsonElement pr && pr.TryGetProperty("TopologyMap", out var tm) && tm.ValueKind == JsonValueKind.String)
        {
            string fn = Path.GetFileName((tm.GetString() ?? "").Replace('\\', '/'));
            if (!string.IsNullOrEmpty(fn) && data.EmbeddedFiles.ContainsKey(fn)) return fn;
        }
        return data.EmbeddedFiles.Keys.FirstOrDefault(k => k.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Перенести параметры проекта (props) в Parameters. Возвращает список перенесённых значений для журнала.
    /// </summary>
    public static List<string> ApplyProjectSettings(InputData data, Parameters p)
    {
        var log = new List<string>();
        if (data.ProjectProps is not JsonElement pr) return log;
        void D(string key, Action<double> set, string title)
        {
            if (pr.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number) { set(v.GetDouble()); log.Add($"{title} = {v.GetDouble().ToString(CultureInfo.InvariantCulture)}"); }
        }
        void I(string key, Action<int> set, string title)
        {
            if (pr.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number) { int x = (int)Math.Round(v.GetDouble()); set(x); log.Add($"{title} = {x}"); }
        }
        void B(string key, Action<bool> set, string title)
        {
            if (pr.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            { set(v.GetBoolean()); log.Add($"{title} = {(v.GetBoolean() ? "да" : "нет")}"); }
        }
        I("MinWellsPerPad", x => p.MinWellsPerPad = x, "Мин. число скважин на кусте");
        I("InitialMaximumNumberWellsCluster", x => p.StartMaxWellsPerPad = x, "Начальное макс. число скважин на кусте");
        I("FinalMaximumNumberWellsCluster", x => p.EndMaxWellsPerPad = x, "Конечное макс. число скважин на кусте");
        I("SkippingSeedWells", x => p.VariantStep = Math.Max(1, x), "Пропуск вариантов");
        D("MaxWaste", x => p.MaxOffset = x, "Максимальный отход, м");
        D("MaxTrajLength", x => p.MaxMD = x, "Максимальная длина по стволу, м");
        D("RateFirstSet", x => p.Dls1 = x, "Темп первого набора");
        D("RateSecondSet", x => p.Dls2 = x, "Темп второго набора");
        B("IsMaximumZenithAngle", x => p.UseAzimuthTurn = x, "Учитывать макс. разворот по азимуту");
        D("MaximumZenithAngle", x => p.MaxAzimuthTurn = x, "Макс. разворот по азимуту, °");
        D("MaximumZenithAngleStabilizationArea", x => p.VerticalLength = x, "Длина вертикального участка, м");
        D("CostPerMeterDrillingNNS", x => p.CostNNS = x, "Стоимость метра ННС");
        D("CostPerMeterDrillingGS", x => p.CostGS = x, "Стоимость метра ГС");
        D("SandCost", x => p.SandCostPer1000m3 = x, "Стоимость песка за тыс. м³");
        var groups = p.PadCostGroups.OrderBy(g => g.MaxWells).ToList();
        for (int k = 0; k < Math.Min(3, groups.Count); k++)
        {
            var g = groups[k];
            D($"CostGroup{k + 1}", x => g.CostPerWell = x, $"Стоимость куста группы {k + 1} на скважину");
        }
        I("NumberWellsGroup", x => p.WellsInGroup = Math.Max(1, x), "Скважин в группе");
        D("DistanceBetweenWellsGroup", x => p.DistanceInGroup = x, "Расстояние между скважинами в группе, м");
        D("DistanceBetweenGroups", x => p.DistanceBetweenGroups = x, "Расстояние между группами, м");
        D("PadDistanceBuffer", x => p.ExtraPadGap = x, "Доп. расстояние между кустами, м");
        D("MaxHookLoad", x => p.MaxHookLoad = x, "Макс. нагрузка на крюке, тс");
        D("MaxTorque", x => p.MaxTorque = x, "Макс. момент, кН·м");
        I("PadStartingNumber", x => p.StartPadNumber = x, "Начальный номер куста");
        if (pr.TryGetProperty("PadNamePrefix", out var pf) && pf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pf.GetString()))
        { p.PadPrefix = pf.GetString(); log.Add("Префикс имени куста = " + p.PadPrefix); }
        // карта желательности, встроенная в проект
        string map = EmbeddedMapName(data);
        if (map != null)
        {
            p.MapSource = MapSources.Kin; p.MapFile = "";
            log.Add($"Карта желательности = карта КИН из проекта ({map})");
        }
        return log;
    }

    private static JsonElement First(JsonElement x, string arrayProp) =>
        x.TryGetProperty(arrayProp, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 ? a[0] : default;

    private static double Num(JsonElement o, string prop) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
}
