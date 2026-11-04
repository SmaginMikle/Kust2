using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WellPadOptimizer;

/// <summary>Готовая карта желательности и её описание для журнала и отчёта.</summary>
public sealed record MapBuildResult(TerrainMap Map, string Name, string Description);

/// <summary>
/// Построение карты желательности по выбранному источнику (Parameters.MapSource):
///  None — вся поверхность суходол; Asc — растр ESRI ASCII Grid; Kin — карта КИН (.bin);
///  Osm — открытые источники OpenStreetMap (Overpass API или файл GeoJSON / ответ Overpass).
/// Карты КИН и OSM переводятся в растр в проекции координат скважин (UTM) с шагом MapCellSize.
/// </summary>
public static class MapBuilder
{
    public static MapBuildResult Build(Parameters p, InputData data, string baseDir, string outDir,
                                       Action<string> log, CancellationToken ct = default)
    {
        if (log == null) log = _ => { };
        string src = string.IsNullOrWhiteSpace(p.MapSource) ? MapSources.None : p.MapSource.Trim();
        string file = ResolvePath(p.MapFile, baseDir);
        MapBuildResult res;

        if (src.Equals(MapSources.Asc, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) return Fallback(p, data, log, $"файл карты {file} не найден");
            var m = TerrainMap.LoadAsc(file);
            res = new MapBuildResult(m, Path.GetFileName(file), $"растр {Path.GetFileName(file)} ({m.NCols}×{m.NRows}, ячейка {m.CellSize} м)");
        }
        else if (src.Equals(MapSources.Kin, StringComparison.OrdinalIgnoreCase))
        {
            KinGrid kin; string kinName;
            string emb = ProjectLoader.EmbeddedMapName(data);
            if (!string.IsNullOrWhiteSpace(p.MapFile) && File.Exists(file)) { kin = KinGrid.Load(file); kinName = Path.GetFileName(file); }
            else if (emb != null)
            {
                if (!string.IsNullOrWhiteSpace(p.MapFile)) log($"Внимание: файл карты КИН {file} не найден — используется карта, встроенная в проект.");
                kin = KinGrid.Load(data.EmbeddedFiles[emb]); kinName = emb + " (из проекта)";
            }
            else return Fallback(p, data, log, $"файл карты КИН {file} не найден");
            log($"Карта КИН «{kin.Title}»: {kin.NCols}×{kin.NRows}, долгота {kin.Lon0:F4}–{kin.Lon1:F4}°, широта {kin.Lat0:F4}–{kin.Lat1:F4}°; " +
                $"значения: {string.Join(", ", kin.ValueCounts().OrderBy(k => k.Key).Select(k => $"{k.Key:0.##} — {k.Value:P1}"))}");
            var m = kin.ToTerrainMap(p, data, Projection(p, data), log, ct);
            res = new MapBuildResult(m, "КИН: " + kinName, $"карта КИН {kinName} → растр {m.NCols}×{m.NRows}, ячейка {m.CellSize} м");
        }
        else if (src.Equals(MapSources.Osm, StringComparison.OrdinalIgnoreCase))
        {
            var tm = Projection(p, data);
            var grid = EmptyGrid(p, data, p.DefaultTerrainCode);
            string json; string origin;
            if (p.OsmMode.Equals("File", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(file)) return Fallback(p, data, log, $"файл OSM {file} не найден");
                json = File.ReadAllText(file); origin = Path.GetFileName(file);
            }
            else
            {
                json = OsmLoader.DownloadOverpass(p, grid, tm, log, ct);
                origin = "Overpass API";
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    string cache = Path.Combine(outDir, "osm_overpass.json");
                    File.WriteAllText(cache, json, new UTF8Encoding(false));
                    log($"Ответ Overpass сохранён: {cache} (можно использовать повторно: OsmMode = File)");
                }
            }
            var stats = OsmLoader.Rasterize(json, p, grid, tm, log, ct);
            res = new MapBuildResult(grid, "OpenStreetMap (" + origin + ")",
                $"OpenStreetMap ({origin}): {stats} → растр {grid.NCols}×{grid.NRows}, ячейка {grid.CellSize} м");
        }
        else
        {
            res = Uniform(p, data);
        }
        res.Map.ForbiddenCode = p.ForbiddenCode;
        log("Карта желательности: " + res.Description);
        var shares = res.Map.ClassShares();
        log("Доли классов на карте: " + string.Join(", ", shares.OrderBy(k => k.Key).Select(k => $"{p.TerrainName(k.Key)} {k.Value:P1}")));
        return res;
    }

    private static MapBuildResult Fallback(Parameters p, InputData data, Action<string> log, string why)
    {
        log("Внимание: " + why + " — вся поверхность считается суходолом.");
        return Uniform(p, data);
    }

    public static MapBuildResult Uniform(Parameters p, InputData data)
    {
        var m = TerrainMap.Uniform(data.Wells, p.DefaultTerrainCode, Margin(p));
        m.ForbiddenCode = p.ForbiddenCode;
        string name = p.TerrainName(p.DefaultTerrainCode).ToLowerInvariant();
        return new MapBuildResult(m, "не задана — вся поверхность: " + name, $"не задана — вся поверхность: {name}, запретных зон нет");
    }

    public static string ResolvePath(string path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return path;
        foreach (var b in new[] { baseDir, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(b)) continue;
            string f = Path.Combine(b, path);
            if (File.Exists(f)) return f;
        }
        return Path.Combine(string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir, path);
    }

    /// <summary>Запас вокруг целей: радиус поиска площадок + размер куста.</summary>
    public static double Margin(Parameters p) => p.MaxOffset * p.SoftRadiusFactor + p.PadDiagonal(p.MaxPadCapacity) + 500;

    public static TransverseMercator Projection(Parameters p, InputData data)
    {
        int epsg = p.ProjectedCrs > 0 ? p.ProjectedCrs : data.ProjectedCrs;
        if (epsg <= 0) throw new InvalidOperationException("Не задана проекция координат скважин: в файле нет mapDefinition.projected_crs. Укажите ProjectedCrs (например, 32642).");
        return TransverseMercator.FromEpsg(epsg);
    }

    /// <summary>Пустой растр, покрывающий цели с запасом, шаг MapCellSize.</summary>
    public static TerrainMap EmptyGrid(Parameters p, InputData data, int fill)
    {
        double mg = Margin(p), cell = Math.Max(5, p.MapCellSize);
        var w = data.Wells;
        double x0 = Math.Floor((w.Min(t => Math.Min(t.T1.E, t.T3.E)) - mg) / 1000) * 1000;
        double y0 = Math.Floor((w.Min(t => Math.Min(t.T1.N, t.T3.N)) - mg) / 1000) * 1000;
        double x1 = Math.Ceiling((w.Max(t => Math.Max(t.T1.E, t.T3.E)) + mg) / 1000) * 1000;
        double y1 = Math.Ceiling((w.Max(t => Math.Max(t.T1.N, t.T3.N)) + mg) / 1000) * 1000;
        return TerrainMap.Create(x0, y0, cell, (int)Math.Round((x1 - x0) / cell), (int)Math.Round((y1 - y0) / cell), fill, p.ForbiddenCode);
    }
}

/// <summary>
/// Карта желательности в формате КИН (.bin). Структура (little-endian):
///  0..19   служебные целые (0, 0, 1, 0, 0);
///  20      int32  число строк;  24 int32 число столбцов;
///  28      4×double: долгота мин., широта мин., долгота макс., широта макс. (градусы WGS84);
///  60..95  служебные поля;
///  96      int32  размер блока данных в байтах (= строки × столбцы × 4);
///  100     float32[строки × столбцы] — значения по строкам, строка 0 — южный край (KinRowsFromSouth);
///          значения ≥ 1e30 — нет данных;
///  далее   4 угла в градусах (8×double), строки «имя слоя» и «подпись» (int32 длина + байты),
///          4 угла в проекции исходной карты (8×double).
/// Значения ячеек (по умолчанию 2 — суходол, 3 — болото, 4 — водоём) переводятся в классы программы по таблице KinCodes.
/// </summary>
public sealed class KinGrid
{
    public int NRows { get; private set; }
    public int NCols { get; private set; }
    public double Lon0 { get; private set; }
    public double Lat0 { get; private set; }
    public double Lon1 { get; private set; }
    public double Lat1 { get; private set; }
    public string Title { get; private set; } = "";
    public float[] Values { get; private set; }

    static KinGrid() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

    public static KinGrid Load(string path) => Load(File.ReadAllBytes(path));

    public static KinGrid Load(byte[] b)
    {
        if (b == null || b.Length < 100) throw new InvalidDataException("Файл карты КИН слишком короткий.");
        var g = new KinGrid
        {
            NRows = BitConverter.ToInt32(b, 20),
            NCols = BitConverter.ToInt32(b, 24),
            Lon0 = BitConverter.ToDouble(b, 28),
            Lat0 = BitConverter.ToDouble(b, 36),
            Lon1 = BitConverter.ToDouble(b, 44),
            Lat1 = BitConverter.ToDouble(b, 52),
        };
        int nbytes = BitConverter.ToInt32(b, 96);
        long expected = (long)g.NRows * g.NCols * 4;
        if (g.NRows <= 0 || g.NCols <= 0 || nbytes != expected || 100 + expected > b.Length)
            throw new InvalidDataException($"Неизвестная структура файла КИН: строк {g.NRows}, столбцов {g.NCols}, блок данных {nbytes} байт.");
        if (!(g.Lon1 > g.Lon0 && g.Lat1 > g.Lat0 && Math.Abs(g.Lat0) <= 90 && Math.Abs(g.Lat1) <= 90))
            throw new InvalidDataException("Файл КИН: некорректные географические границы сетки.");
        g.Values = new float[g.NRows * g.NCols];
        Buffer.BlockCopy(b, 100, g.Values, 0, (int)expected);
        // подпись слоя (необязательная часть)
        try
        {
            int off = 100 + (int)expected + 64;
            var parts = new List<string>();
            for (int k = 0; k < 2 && off + 4 <= b.Length; k++)
            {
                int len = BitConverter.ToInt32(b, off);
                if (len <= 0 || len > 1000 || off + 4 + len > b.Length) break;
                parts.Add(Encoding.GetEncoding(1251).GetString(b, off + 4, len).Trim(' ', '-'));
                off += 4 + len;
            }
            g.Title = string.Join(" ", parts.Where(x => x.Length > 0));
        }
        catch { /* подпись не обязательна */ }
        return g;
    }

    public static bool IsNoData(float v) => float.IsNaN(v) || Math.Abs(v) >= 1e30f;

    public Dictionary<double, double> ValueCounts()
    {
        var d = new Dictionary<double, double>();
        foreach (var v in Values)
        {
            double k = IsNoData(v) ? double.NaN : v;
            if (double.IsNaN(k)) continue;
            d[k] = d.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        foreach (var k in d.Keys.ToList()) d[k] /= Values.Length;
        return d;
    }

    /// <summary>Значение в точке (широта, долгота) или NaN вне карты / без данных.</summary>
    public double Sample(double lat, double lon, bool rowsFromSouth)
    {
        double fx = (lon - Lon0) / (Lon1 - Lon0) * NCols, fy = (lat - Lat0) / (Lat1 - Lat0) * NRows;
        if (fx < 0 || fy < 0 || fx >= NCols || fy >= NRows) return double.NaN;
        int col = (int)fx, row = (int)fy;
        if (!rowsFromSouth) row = NRows - 1 - row;
        float v = Values[row * NCols + col];
        return IsNoData(v) ? double.NaN : v;
    }

    public TerrainMap ToTerrainMap(Parameters p, InputData data, TransverseMercator tm, Action<string> log, CancellationToken ct)
    {
        var map = MapBuilder.EmptyGrid(p, data, p.DefaultTerrainCode);
        var codes = new Dictionary<float, int>();
        foreach (var k in p.KinCodes) codes[(float)k.Value] = k.Code;
        int noData = p.KinNoDataForbidden ? p.ForbiddenCode : p.DefaultTerrainCode;
        var unknown = new System.Collections.Concurrent.ConcurrentDictionary<double, int>();
        Parallel.For(0, map.NRows, new ParallelOptions { CancellationToken = ct }, r =>
        {
            double n = map.CellN(r);
            for (int c = 0; c < map.NCols; c++)
            {
                var (lat, lon) = tm.Inverse(map.CellE(c), n);
                double v = Sample(lat, lon, p.KinRowsFromSouth);
                int code;
                if (double.IsNaN(v)) code = noData;
                else if (!codes.TryGetValue((float)v, out code)) { code = p.DefaultTerrainCode; unknown.AddOrUpdate(v, 1, (_, x) => x + 1); }
                map.Set(r, c, code);
            }
        });
        if (!unknown.IsEmpty)
            log("Внимание: значения карты КИН без соответствия в KinCodes (считаются суходолом): " +
                string.Join(", ", unknown.Keys.OrderBy(x => x).Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))));
        return map;
    }
}

/// <summary>
/// Слои OpenStreetMap: загрузка через Overpass API и перевод в растр.
/// Поддерживаются ответ Overpass в формате JSON (out geom) и GeoJSON (FeatureCollection; координаты в градусах
/// WGS84 или уже в проекции скважин). В GeoJSON свойство wpo_class (число) задаёт класс напрямую.
/// Приоритет классов при наложении: запрет > водоём > болото > кедровник > лес > суходол.
/// </summary>
public static class OsmLoader
{
    private sealed class Feature
    {
        public Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase);
        public List<List<(double lat, double lon)>> Rings = new();   // площадные (чёт-нечет: внешние и внутренние)
        public List<List<(double lat, double lon)>> Lines = new();   // линейные
        public bool Projected;                                      // координаты уже в метрах (E, N) — хранятся как (N, E)
    }

    public static string BuildQuery(Parameters p, double south, double west, double north, double east)
    {
        string bb = string.Format(CultureInfo.InvariantCulture, "({0:F6},{1:F6},{2:F6},{3:F6})", south, west, north, east);
        var q = new StringBuilder();
        q.AppendLine($"[out:json][timeout:{Math.Max(60, p.OverpassTimeoutSec)}];");
        q.AppendLine("(");
        void Both(string filter) { q.AppendLine($"  way{filter}{bb};"); q.AppendLine($"  relation{filter}{bb};"); }
        if (p.OsmWetlands) Both("[\"natural\"=\"wetland\"]");
        if (p.OsmWater)
        {
            Both("[\"natural\"=\"water\"]");
            Both("[\"waterway\"=\"riverbank\"]");
            Both("[\"landuse\"=\"reservoir\"]");
        }
        if (p.OsmRivers) q.AppendLine($"  way[\"waterway\"~\"^(river|canal|stream|ditch|drain)$\"]{bb};");
        if (p.OsmProtected)
        {
            Both("[\"boundary\"~\"^(protected_area|national_park)$\"]");
            Both("[\"leisure\"=\"nature_reserve\"]");
        }
        if (p.OsmCedar || p.OsmForest)
        {
            Both("[\"landuse\"=\"forest\"]");
            Both("[\"natural\"=\"wood\"]");
        }
        if (p.OsmBuiltUp) Both("[\"landuse\"~\"^(residential|industrial|commercial|cemetery|military|quarry)$\"]");
        q.AppendLine(");");
        q.AppendLine("out geom;");
        return q.ToString();
    }

    public static string DownloadOverpass(Parameters p, TerrainMap grid, TransverseMercator tm, Action<string> log, CancellationToken ct)
    {
        // границы растра → градусы
        double s = 90, w = 180, n = -90, e = -180;
        foreach (var (x, y) in new[] { (grid.XLL, grid.YLL), (grid.XMax, grid.YLL), (grid.XMax, grid.YMax), (grid.XLL, grid.YMax),
                                       ((grid.XLL + grid.XMax) / 2, grid.YLL), ((grid.XLL + grid.XMax) / 2, grid.YMax) })
        {
            var (lat, lon) = tm.Inverse(x, y);
            s = Math.Min(s, lat); n = Math.Max(n, lat); w = Math.Min(w, lon); e = Math.Max(e, lon);
        }
        string query = BuildQuery(p, s, w, n, e);
        log($"Запрос Overpass API ({p.OverpassUrl}), область {s:F4}–{n:F4}° с.ш., {w:F4}–{e:F4}° в.д. …");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(60, p.OverpassTimeoutSec) + 30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WellPadOptimizer/1.0 (+https://www.openstreetmap.org)");
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
        using var resp = http.PostAsync(p.OverpassUrl, content, ct).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Overpass API вернул {(int)resp.StatusCode}: {Trunc(body, 300)}. " +
                "Проверьте доступ к интернету или загрузите данные вручную (overpass-turbo.eu, NextGIS) и укажите файл (OsmMode = File).");
        log($"Получено {body.Length / 1024} КБ данных OSM.");
        return body;
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    public static string Rasterize(string json, Parameters p, TerrainMap grid, TransverseMercator tm, Action<string> log, CancellationToken ct)
    {
        var feats = Parse(json);
        // классификация с приоритетом
        var layers = new List<(int prio, int code, Feature f, double width)>();
        var counts = new Dictionary<string, int>();
        void Count(string k) => counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
        foreach (var f in feats)
        {
            ct.ThrowIfCancellationRequested();
            if (f.Tags.TryGetValue("wpo_class", out var wc) && int.TryParse(wc, out int direct))
            {
                layers.Add((p.IsForbidden(direct) ? 6 : 5, direct, f, Width(p, f))); Count("класс из файла"); continue;
            }
            string natural = Tag(f, "natural"), waterway = Tag(f, "waterway"), landuse = Tag(f, "landuse");
            string boundary = Tag(f, "boundary"), leisure = Tag(f, "leisure");
            if (p.OsmProtected && (boundary == "protected_area" || boundary == "national_park" || leisure == "nature_reserve"))
            { layers.Add((7, p.ForbiddenCode, f, 0)); Count("ООПТ"); }
            else if (p.OsmBuiltUp && (landuse is "residential" or "industrial" or "commercial" or "cemetery" or "military" or "quarry"))
            { layers.Add((6, p.ForbiddenCode, f, 0)); Count("застройка"); }
            else if (p.OsmWater && (natural == "water" || waterway == "riverbank" || landuse == "reservoir"))
            { layers.Add((5, p.OsmWaterCode, f, 0)); Count("водоёмы"); }
            else if (p.OsmRivers && waterway is "river" or "canal" or "stream" or "ditch" or "drain")
            { layers.Add((5, p.OsmWaterCode, f, Width(p, f))); Count("реки и ручьи"); }
            else if (p.OsmWetlands && natural == "wetland")
            { layers.Add((4, p.OsmWetlandCode, f, 0)); Count("болота"); }
            else if ((landuse == "forest" || natural == "wood") && p.OsmCedar && IsCedar(f))
            { layers.Add((3, p.OsmCedarCode, f, 0)); Count("кедровники"); }
            else if ((landuse == "forest" || natural == "wood") && p.OsmForest)
            { layers.Add((2, p.OsmForestCode, f, 0)); Count("лес"); }
        }
        foreach (var (prio, code, f, width) in layers.OrderBy(x => x.prio))
        {
            ct.ThrowIfCancellationRequested();
            var rings = f.Rings.Select(r => r.Select(pt => ToXY(f, pt, tm)).ToList()).Where(r => r.Count >= 3).ToList();
            if (rings.Count > 0) FillPolygon(grid, rings, code);
            if (f.Lines.Count > 0)
            {
                double wdt = width > 0 ? width : p.StreamWidth;
                foreach (var line in f.Lines) DrawLine(grid, line.Select(pt => ToXY(f, pt, tm)).ToList(), wdt, code);
            }
        }
        if (p.WaterProtectionBuffer > 0) ApplyBuffer(grid, p.OsmWaterCode, p.WaterProtectionBuffer, p.ForbiddenCode);
        string stats = counts.Count == 0 ? "объектов не найдено" : string.Join(", ", counts.Select(k => $"{k.Key} — {k.Value}"));
        log("Объекты OSM: " + stats);
        return stats;
    }

    private static string Tag(Feature f, string k) => f.Tags.TryGetValue(k, out var v) ? v.ToLowerInvariant() : "";

    private static double Width(Parameters p, Feature f)
    {
        if (f.Tags.TryGetValue("width", out var w) && double.TryParse(w.Replace(',', '.').Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
            return v;
        return Tag(f, "waterway") switch { "river" => p.RiverWidth, "canal" => p.CanalWidth, _ => p.StreamWidth };
    }

    private static bool IsCedar(Feature f)
    {
        foreach (var kv in f.Tags)
        {
            string v = kv.Value.ToLowerInvariant();
            if (v.Contains("кедр") || v.Contains("pinus sibirica") || v.Contains("pinus koraiensis") || v.Contains("pinus pumila")) return true;
        }
        return false;
    }

    private static (double x, double y) ToXY(Feature f, (double lat, double lon) pt, TransverseMercator tm)
    {
        if (f.Projected) return (pt.lon, pt.lat);   // для проекционных координат хранится (N, E)
        var (e, n) = tm.Forward(pt.lat, pt.lon);
        return (e, n);
    }

    // ------------------------------------------------------------------ разбор

    private static List<Feature> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        var list = new List<Feature>();
        if (root.TryGetProperty("elements", out var elements)) ParseOverpass(elements, list);
        else if (root.TryGetProperty("features", out var features)) ParseGeoJson(features, list);
        else throw new InvalidDataException("Файл OSM: ожидается ответ Overpass (elements) или GeoJSON (features).");
        return list;
    }

    private static Dictionary<string, string> ReadTags(JsonElement e, string prop)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty(prop, out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var kv in t.EnumerateObject())
                d[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.ToString();
        return d;
    }

    private static List<(double lat, double lon)> Geometry(JsonElement e)
    {
        var pts = new List<(double, double)>();
        if (e.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Array)
            foreach (var p in g.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("lat", out var la) && p.TryGetProperty("lon", out var lo))
                    pts.Add((la.GetDouble(), lo.GetDouble()));
        return pts;
    }

    private static bool IsAreaFeature(Dictionary<string, string> tags)
    {
        if (tags.TryGetValue("area", out var a) && a == "no") return false;
        if (tags.TryGetValue("waterway", out var w) && w != "riverbank") return false;
        return true;
    }

    private static void ParseOverpass(JsonElement elements, List<Feature> list)
    {
        foreach (var el in elements.EnumerateArray())
        {
            string type = el.TryGetProperty("type", out var t) ? t.GetString() : "";
            var f = new Feature { Tags = ReadTags(el, "tags") };
            if (type == "way")
            {
                var pts = Geometry(el);
                if (pts.Count < 2) continue;
                bool closed = pts.Count >= 4 && pts[0] == pts[^1];
                if (closed && IsAreaFeature(f.Tags)) f.Rings.Add(pts); else f.Lines.Add(pts);
            }
            else if (type == "relation" && el.TryGetProperty("members", out var mem))
            {
                var parts = new List<List<(double lat, double lon)>>();
                foreach (var m in mem.EnumerateArray())
                {
                    if (!m.TryGetProperty("type", out var mt) || mt.GetString() != "way") continue;
                    string role = m.TryGetProperty("role", out var r) ? r.GetString() : "";
                    if (role != "outer" && role != "inner" && role != "") continue;
                    var pts = Geometry(m);
                    if (pts.Count >= 2) parts.Add(pts);
                }
                f.Rings.AddRange(AssembleRings(parts));
            }
            else continue;
            if (f.Rings.Count > 0 || f.Lines.Count > 0) list.Add(f);
        }
    }

    private static void ParseGeoJson(JsonElement features, List<Feature> list)
    {
        foreach (var ft in features.EnumerateArray())
        {
            if (!ft.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object) continue;
            var f = new Feature { Tags = ReadTags(ft, "properties") };
            string type = g.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (!g.TryGetProperty("coordinates", out var c)) continue;
            switch (type)
            {
                case "Polygon": foreach (var ring in c.EnumerateArray()) f.Rings.Add(Coords(ring, f)); break;
                case "MultiPolygon": foreach (var poly in c.EnumerateArray()) foreach (var ring in poly.EnumerateArray()) f.Rings.Add(Coords(ring, f)); break;
                case "LineString": f.Lines.Add(Coords(c, f)); break;
                case "MultiLineString": foreach (var l in c.EnumerateArray()) f.Lines.Add(Coords(l, f)); break;
                default: continue;
            }
            list.Add(f);
        }
    }

    /// <summary>Координаты GeoJSON [x, y]: градусы (долгота, широта) или метры в проекции скважин.</summary>
    private static List<(double lat, double lon)> Coords(JsonElement arr, Feature f)
    {
        var pts = new List<(double, double)>();
        foreach (var p in arr.EnumerateArray())
        {
            double x = p[0].GetDouble(), y = p[1].GetDouble();
            if (Math.Abs(x) > 180 || Math.Abs(y) > 90) f.Projected = true;
            pts.Add((y, x));
        }
        return pts;
    }

    /// <summary>Сборка колец мультиполигона из фрагментов линий (соединение по совпадающим концам).</summary>
    private static List<List<(double lat, double lon)>> AssembleRings(List<List<(double lat, double lon)>> parts)
    {
        var rings = new List<List<(double, double)>>();
        var pool = parts.Select(x => new List<(double, double)>(x)).ToList();
        static bool Same((double a, double b) p, (double a, double b) q) => Math.Abs(p.a - q.a) < 1e-9 && Math.Abs(p.b - q.b) < 1e-9;
        while (pool.Count > 0)
        {
            var cur = pool[0]; pool.RemoveAt(0);
            bool grown = true;
            while (!Same(cur[0], cur[^1]) && grown)
            {
                grown = false;
                for (int i = 0; i < pool.Count; i++)
                {
                    var s = pool[i];
                    if (Same(cur[^1], s[0])) { cur.AddRange(s.Skip(1)); }
                    else if (Same(cur[^1], s[^1])) { s.Reverse(); cur.AddRange(s.Skip(1)); }
                    else if (Same(cur[0], s[^1])) { cur.InsertRange(0, s.Take(s.Count - 1)); }
                    else if (Same(cur[0], s[0])) { s.Reverse(); cur.InsertRange(0, s.Take(s.Count - 1)); }
                    else continue;
                    pool.RemoveAt(i); grown = true; break;
                }
            }
            if (cur.Count >= 3) rings.Add(cur);
        }
        return rings;
    }

    // ------------------------------------------------------------------ растеризация

    /// <summary>Заливка многоугольника (правило чёт-нечет по всем кольцам) по центрам ячеек.</summary>
    public static void FillPolygon(TerrainMap g, List<List<(double x, double y)>> rings, int code)
    {
        double minY = rings.Min(r => r.Min(p => p.y)), maxY = rings.Max(r => r.Max(p => p.y));
        int r0 = Math.Max(0, (int)Math.Floor((g.YMax - maxY) / g.CellSize));
        int r1 = Math.Min(g.NRows - 1, (int)Math.Ceiling((g.YMax - minY) / g.CellSize));
        var xs = new List<double>();
        for (int row = r0; row <= r1; row++)
        {
            double y = g.CellN(row);
            xs.Clear();
            foreach (var ring in rings)
            {
                int m = ring.Count;
                for (int i = 0; i < m; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % m];
                    if ((a.y <= y && b.y > y) || (b.y <= y && a.y > y))
                        xs.Add(a.x + (y - a.y) / (b.y - a.y) * (b.x - a.x));
                }
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int c0 = Math.Max(0, (int)Math.Ceiling((xs[k] - g.XLL) / g.CellSize - 0.5));
                int c1 = Math.Min(g.NCols - 1, (int)Math.Floor((xs[k + 1] - g.XLL) / g.CellSize - 0.5));
                for (int c = c0; c <= c1; c++) g.Set(row, c, code);
            }
        }
    }

    /// <summary>Линия шириной width: ячейки, центр которых ближе width/2 к отрезку.</summary>
    public static void DrawLine(TerrainMap g, List<(double x, double y)> pts, double width, int code)
    {
        double h = Math.Max(width / 2, g.CellSize * 0.5);
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            int c0 = Math.Max(0, (int)Math.Floor((Math.Min(a.x, b.x) - h - g.XLL) / g.CellSize));
            int c1 = Math.Min(g.NCols - 1, (int)Math.Floor((Math.Max(a.x, b.x) + h - g.XLL) / g.CellSize));
            int r0 = Math.Max(0, (int)Math.Floor((g.YMax - Math.Max(a.y, b.y) - h) / g.CellSize));
            int r1 = Math.Min(g.NRows - 1, (int)Math.Floor((g.YMax - Math.Min(a.y, b.y) + h) / g.CellSize));
            double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
            for (int r = r0; r <= r1; r++)
            {
                double y = g.CellN(r);
                for (int c = c0; c <= c1; c++)
                {
                    double x = g.CellE(c);
                    double t = l2 > 0 ? Math.Clamp(((x - a.x) * dx + (y - a.y) * dy) / l2, 0, 1) : 0;
                    double ex = a.x + t * dx - x, ey = a.y + t * dy - y;
                    if (ex * ex + ey * ey <= h * h) g.Set(r, c, code);
                }
            }
        }
    }

    /// <summary>Запретная полоса шириной dist вокруг ячеек класса waterCode (не затрагивает сами водоёмы).</summary>
    public static void ApplyBuffer(TerrainMap g, int waterCode, double dist, int forbiddenCode)
    {
        int k = (int)Math.Ceiling(dist / g.CellSize);
        var mark = new bool[g.NRows * g.NCols];
        for (int r = 0; r < g.NRows; r++)
            for (int c = 0; c < g.NCols; c++)
            {
                if (g[r, c] != waterCode) continue;
                for (int dr = -k; dr <= k; dr++)
                    for (int dc = -k; dc <= k; dc++)
                    {
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= g.NRows || cc >= g.NCols) continue;
                        if ((dr * dr + dc * dc) * g.CellSize * g.CellSize > dist * dist) continue;
                        mark[rr * g.NCols + cc] = true;
                    }
            }
        for (int r = 0; r < g.NRows; r++)
            for (int c = 0; c < g.NCols; c++)
                if (mark[r * g.NCols + c] && g[r, c] != waterCode) g.Set(r, c, forbiddenCode);
    }
}
