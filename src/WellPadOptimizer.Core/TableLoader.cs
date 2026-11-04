using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace WellPadOptimizer;

/// <summary>
/// Загрузка целей из таблицы Excel (.xlsx) или CSV / TXT — например, выгрузки слоя проектных скважин.
/// Первая строка — заголовки; распознаются (без учёта регистра):
///  • имя: «Имя», «Скважина», «Name», «Подпись»;
///  • начало ГС (T1): «X1», «Y1» или «X», «Y»;
///  • конец ГС (T3): «X2», «Y2»; если их нет — «Xзаб», «Yзаб»; если нет и их — по «Азимут» и «Длина» от T1;
///  • глубина (TVD): «Глубина», «TVD», «Z», «Z1», «Абс. отметка» (и «Z2» / «Глубина T3» для T3);
///    если глубины в таблице нет — берётся параметр DefaultTargetTvd.
/// Координаты (параметр TableCrs): Auto — градусы, если |X| ≤ 180 и |Y| ≤ 90; Гаусс–Крюгер СК-42, если в восточной
/// координате есть номер зоны (например, 12 601 924); иначе — UTM (ProjectedCrs). GK — СК-42 Гаусса–Крюгера
/// (зона из префикса или TableGkZone), UTM — EPSG ProjectedCrs, WGS84 — градусы. Для СК-42 выполняется пересчёт
/// в WGS 84 по ГОСТ 32453-2017 (TableDatumShift). Расчёт ведётся в UTM зоны центра участка.
/// </summary>
public static class TableLoader
{
    public static bool IsTable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".xlsx" or ".xlsm" or ".csv" or ".txt" or ".tsv";
    }

    public static InputData Load(string path, Parameters p)
    {
        p ??= new Parameters();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var rows = ext is ".xlsx" or ".xlsm" ? ReadXlsx(path) : ReadCsv(path);
        rows = rows.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count < 2) throw new InvalidDataException("Таблица пуста: нужна строка заголовков и хотя бы одна скважина.");
        var hdr = rows[0].Select(h => Norm(h)).ToArray();
        int Col(params string[] names)
        {
            foreach (var n in names)
            {
                int i = Array.IndexOf(hdr, Norm(n));
                if (i >= 0) return i;
            }
            return -1;
        }
        int cName = Col("Имя", "Скважина", "Name", "Well", "Подпись");
        int cX1 = Col("X1", "XT1", "X T1"), cY1 = Col("Y1", "YT1", "Y T1");
        int cX = Col("X", "Восток", "East", "E"), cY = Col("Y", "Север", "North", "N");
        int cX2 = Col("X2", "XT3", "X T3"), cY2 = Col("Y2", "YT3", "Y T3");
        int cXb = Col("Xзаб", "X заб", "XЗабой"), cYb = Col("Yзаб", "Y заб", "YЗабой");
        int cAz = Col("Азимут", "Azimuth"), cLen = Col("Длина", "Length", "Длина ГС");
        int cZ1 = Col("Глубина", "TVD", "Z", "Z1", "Глубина T1", "Абс. отметка", "Абсолютная отметка", "Depth");
        int cZ2 = Col("Z2", "Глубина T3", "TVD2");
        if (cName < 0) throw new InvalidDataException("В таблице нет столбца с именем скважины («Имя» / «Скважина»).");
        if ((cX1 < 0 || cY1 < 0) && (cX < 0 || cY < 0)) throw new InvalidDataException("В таблице нет координат начала ГС («X», «Y» или «X1», «Y1»).");

        // ---- строки таблицы → координаты в исходной системе
        var recs = new List<(string name, double x1, double y1, double x3, double y3, double z1, double z3)>();
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string name = Cell(r, cName).Trim();
            if (name.Length == 0) continue;
            double x1 = Num(r, cX1), y1 = Num(r, cY1);
            if (double.IsNaN(x1) || double.IsNaN(y1)) { x1 = Num(r, cX); y1 = Num(r, cY); }
            if (double.IsNaN(x1) || double.IsNaN(y1)) throw new InvalidDataException($"Скважина {name}: не заданы координаты.");
            double x3 = Num(r, cX2), y3 = Num(r, cY2);
            if (double.IsNaN(x3) || double.IsNaN(y3)) { x3 = Num(r, cXb); y3 = Num(r, cYb); }
            if (double.IsNaN(x3) || double.IsNaN(y3))
            {
                double az = Num(r, cAz), len = Num(r, cLen);
                if (!double.IsNaN(az) && !double.IsNaN(len) && len > 0 && Math.Abs(x1) > 180)
                { x3 = x1 + len * Math.Sin(az * Math.PI / 180); y3 = y1 + len * Math.Cos(az * Math.PI / 180); }
                else { x3 = x1; y3 = y1; }
            }
            double z1 = Num(r, cZ1), z3 = Num(r, cZ2);
            if (double.IsNaN(z1)) z1 = p.DefaultTargetTvd;
            if (double.IsNaN(z3)) z3 = z1;
            recs.Add((name, x1, y1, x3, y3, Math.Abs(z1), Math.Abs(z3)));
        }
        if (recs.Count == 0) throw new InvalidDataException("В таблице нет скважин.");

        // ---- система координат
        string crs = (p.TableCrs ?? "Auto").Trim().ToLowerInvariant();
        bool degrees = recs.All(r => Math.Abs(r.x1) <= 180 && Math.Abs(r.y1) <= 90);
        // Гаусс–Крюгер с номером зоны: обе координаты ≥ 1 000 000 (восточная с префиксом зоны, например 12 601 924).
        // Восточная — та, что ≥ 10 000 000, иначе столбец X (TableXIsEast). Если одна координата < 1 000 000 —
        // это метрическая система без префикса (UTM или местная), восточная — меньшая.
        double ax = recs[0].x1, ay = recs[0].y1;
        bool bothBig = ax >= 1e6 && ay >= 1e6;
        bool xEast = degrees || (bothBig ? (ax >= 1e7 && ay < 1e7) || (!(ay >= 1e7 && ax < 1e7) && p.TableXIsEast) : ax < ay);
        if (crs == "auto") crs = degrees ? "wgs84" : bothBig && HasZonePrefix(xEast ? ax : ay) ? "gk" : "utm";

        Func<double, double, (double lat, double lon)> toGeo;
        string crsName;
        switch (crs)
        {
            case "wgs84":
                toGeo = (x, y) => (y, x);
                crsName = "WGS 84, градусы";
                break;
            case "gk":
            {
                int zone = p.TableGkZone > 0 ? p.TableGkZone : (int)Math.Floor((xEast ? recs[0].x1 : recs[0].y1) / 1e6);
                if (zone < 1 || zone > 60) throw new InvalidDataException("Не удалось определить зону Гаусса–Крюгера: задайте TableGkZone.");
                var gk = Datum.GaussKruger(zone);
                bool shift = p.TableDatumShift;
                toGeo = (x, y) =>
                {
                    double e = xEast ? x : y, n = xEast ? y : x;
                    if (e >= 1e6) e -= Math.Floor(e / 1e6) * 1e6;
                    var (la, lo) = gk.Inverse(e, n);
                    return shift ? Datum.Sk42ToWgs84(la, lo) : (la, lo);
                };
                crsName = $"СК-42, Гаусс–Крюгер, зона {zone}" + (shift ? " → WGS 84 (ГОСТ 32453-2017)" : "");
                break;
            }
            default:
            {
                if (p.ProjectedCrs <= 0)
                {
                    // проекция неизвестна — координаты используются как есть (без привязки карт)
                    var dl = new InputData();
                    Fill(dl, recs.Select(r => (r.name, xEast ? r.y1 : r.x1, xEast ? r.x1 : r.y1, xEast ? r.y3 : r.x3, xEast ? r.x3 : r.y3, r.z1, r.z3)), p);
                    dl.SetTable(0, Path.GetFileName(path), "местные координаты (без проекции: карты КИН и OSM недоступны)");
                    return dl;
                }
                var utm = TransverseMercator.FromEpsg(p.ProjectedCrs);
                toGeo = (x, y) => utm.Inverse(xEast ? x : y, xEast ? y : x);
                crsName = $"EPSG:{p.ProjectedCrs}";
                break;
            }
        }

        var geo = recs.Select(r => (r.name, g1: toGeo(r.x1, r.y1), g3: toGeo(r.x3, r.y3), r.z1, r.z3)).ToList();
        double lonC = geo.Average(g => g.g1.lon), latC = geo.Average(g => g.g1.lat);
        int epsg = p.ProjectedCrs > 0 && crs != "gk" && crs != "wgs84" ? p.ProjectedCrs
                 : (latC >= 0 ? 32600 : 32700) + (int)Math.Floor((lonC + 180) / 6) + 1;
        var tm = TransverseMercator.FromEpsg(epsg);
        var data = new InputData();
        Fill(data, geo.Select(g =>
        {
            var (e1, n1) = tm.Forward(g.g1.lat, g.g1.lon);
            var (e3, n3) = tm.Forward(g.g3.lat, g.g3.lon);
            return (g.name, n1, e1, n3, e3, g.z1, g.z3);
        }), p);
        data.SetTable(epsg, Path.GetFileName(path), crsName);
        return data;
    }

    private static void Fill(InputData data, IEnumerable<(string name, double n1, double e1, double n3, double e3, double z1, double z3)> items, Parameters p)
    {
        foreach (var it in items)
        {
            int idx = data.Wells.Count;
            data.Wells.Add(new WellTarget
            {
                Index = idx, Name = it.name, Uid = it.name,
                T1 = new Vec3(it.n1, it.e1, it.z1), T3 = new Vec3(it.n3, it.e3, it.z3),
                Reversible = p.TableReversible,
            });
        }
    }

    private static bool HasZonePrefix(double v)
    {
        if (v < 1e6 || v >= 61e6) return false;
        double rem = v - Math.Floor(v / 1e6) * 1e6;
        return rem > 100000 && rem < 900000;
    }

    private static string Norm(string s) => (s ?? "").Trim().Replace(" ", "").Replace(".", "").ToLowerInvariant();
    private static string Cell(string[] r, int i) => i >= 0 && i < r.Length ? r[i] ?? "" : "";

    private static double Num(string[] r, int i)
    {
        string s = Cell(r, i).Trim().Replace(" ", "").Replace(' '.ToString(), "").Replace(',', '.');
        return s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    // ------------------------------------------------------------------ CSV

    private static List<string[]> ReadCsv(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1251).GetString(bytes);
        }
        text = text.TrimStart('﻿');
        var lines = text.Replace("\r\n", "\n").Split('\n');
        char sep = lines[0].Contains('\t') ? '\t' : lines[0].Contains(';') ? ';' : ',';
        return lines.Select(l => l.Split(sep).Select(c => c.Trim().Trim('"')).ToArray()).ToList();
    }

    // ------------------------------------------------------------------ XLSX (первый лист, без сторонних библиотек)

    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<string[]> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument LoadXml(string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return null;
            using var s = e.Open();
            return XDocument.Load(s);
        }
        // общие строки
        var shared = new List<string>();
        var ss = LoadXml("xl/sharedStrings.xml");
        if (ss != null)
            foreach (var si in ss.Root.Elements(Ns + "si"))
                shared.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        // первый лист по workbook.xml
        string sheetPath = "xl/worksheets/sheet1.xml";
        var wb = LoadXml("xl/workbook.xml");
        var rels = LoadXml("xl/_rels/workbook.xml.rels");
        var first = wb?.Root.Element(Ns + "sheets")?.Elements(Ns + "sheet").FirstOrDefault();
        if (first != null && rels != null)
        {
            string rid = (string)first.Attribute(RelNs + "id");
            var rel = rels.Root.Elements(PkgNs + "Relationship").FirstOrDefault(r => (string)r.Attribute("Id") == rid);
            if (rel != null)
            {
                string target = ((string)rel.Attribute("Target")).TrimStart('/');
                sheetPath = target.StartsWith("xl/") ? target : "xl/" + target;
            }
        }
        var sheet = LoadXml(sheetPath) ?? throw new InvalidDataException("В книге Excel не найден лист с данными.");
        var rows = new List<string[]>();
        foreach (var row in sheet.Root.Element(Ns + "sheetData")?.Elements(Ns + "row") ?? Enumerable.Empty<XElement>())
        {
            var cells = new SortedDictionary<int, string>();
            int next = 0;
            foreach (var c in row.Elements(Ns + "c"))
            {
                string r = (string)c.Attribute("r");
                int col = r != null ? ColIndex(r) : next;
                next = col + 1;
                string t = (string)c.Attribute("t") ?? "n";
                string v = c.Element(Ns + "v")?.Value;
                string val = t switch
                {
                    "s" => int.TryParse(v, out int k) && k >= 0 && k < shared.Count ? shared[k] : "",
                    "inlineStr" => string.Concat(c.Descendants(Ns + "t").Select(x => x.Value)),
                    "b" => v == "1" ? "1" : "0",
                    _ => v ?? "",
                };
                cells[col] = val;
            }
            int n = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
            var arr = new string[n];
            for (int i = 0; i < n; i++) arr[i] = cells.TryGetValue(i, out var s) ? s : "";
            rows.Add(arr);
        }
        return rows;
    }

    private static int ColIndex(string cellRef)
    {
        int col = 0;
        foreach (char ch in cellRef)
        {
            if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return col - 1;
    }
}
