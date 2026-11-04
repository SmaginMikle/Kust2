using System.Diagnostics;
using System.Text;

namespace WellPadOptimizer;

/// <summary>Вариант, для которого нет допустимого разбиения скважин на кусты.</summary>
public sealed record InfeasibleVariant(string Name, string Scenario, int K, string Reason);

/// <summary>Параметры запуска расчёта.</summary>
public sealed class RunOptions
{
    public string InputPath { get; set; } = "";
    public string OutDir { get; set; } = "results";
    /// <summary>Каталог, относительно которого ищутся файлы карты (обычно каталог settings.json).</summary>
    public string BaseDir { get; set; } = "";
    public Parameters Parameters { get; set; } = new();
    /// <summary>Построить синтетическую карту (для проверки), если источник карты не задан.</summary>
    public bool SyntheticMap { get; set; }
    /// <summary>Для проекта КИН: взять параметры расчёта и карту из проекта.</summary>
    public bool UseProjectSettings { get; set; }
}

/// <summary>Результаты расчёта.</summary>
public sealed class RunResult
{
    public InputData Data { get; init; }
    public Parameters Parameters { get; init; }
    public TerrainMap Map { get; init; }
    public string MapName { get; init; } = "";
    public List<VariantResult> Variants { get; init; } = new();
    public List<InfeasibleVariant> Infeasible { get; init; } = new();
    public VariantResult Best { get; init; }
    public string OutDir { get; init; } = "";
    public string ReportPath { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Полный расчёт: карта желательности → площадки-кандидаты → варианты (исходные из файла, перебор размера куста,
/// заданное число кустов; со штатным ограничением разворота и без него) → таблицы CSV, JSON и report.html.
/// Используется консольной программой и графическим интерфейсом.
/// </summary>
public sealed class Runner
{
    private readonly Action<string> _log;
    public Runner(Action<string> log) { _log = log ?? (_ => { }); }

    public RunResult Run(RunOptions o, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var p = o.Parameters ?? new Parameters();
        if (!File.Exists(o.InputPath))
            throw new FileNotFoundException($"Не найден входной файл {Path.GetFullPath(o.InputPath)}. Укажите путь к input_mass.json.");
        string outDir = string.IsNullOrWhiteSpace(o.OutDir) ? "results" : o.OutDir;
        Directory.CreateDirectory(outDir);

        _log("Загрузка " + Path.GetFileName(o.InputPath) + " …");
        var data = InputData.Load(o.InputPath, p);
        if (data.Format == "table")
            _log($"Таблица «{data.ProjectTitle}»: {data.Wells.Count} скважин, координаты: {data.SourceCrs}" +
                 (data.ProjectedCrs > 0 ? $", расчёт в EPSG:{data.ProjectedCrs}" : "") +
                 (data.Wells.All(w => Math.Abs(w.T1.Z - p.DefaultTargetTvd) < 1e-9) ? $"; глубины в таблице нет — принята {p.DefaultTargetTvd:0} м (DefaultTargetTvd)" : ""));
        if (data.Format == "project")
        {
            _log($"Проект КИН «{data.ProjectTitle}»: проекция EPSG:{data.ProjectedCrs}, встроенные файлы: {string.Join(", ", data.EmbeddedFiles.Keys)}");
            if (o.UseProjectSettings)
            {
                p = p.DeepClone();
                foreach (var line in ProjectLoader.ApplyProjectSettings(data, p)) _log("  из проекта: " + line);
            }
        }
        _log($"Целей: {data.Wells.Count}, исходных вариантов: {data.Variants.Count}, фактических скважин: {data.RealWells.Count}");
        _log($"R1 = {p.R1:F0} м, R2 = {p.R2:F0} м (темпы {p.Dls1} и {p.Dls2} °/{p.DlsBaseLength} м); размер куста {p.MinWellsPerPad}–{p.EndMaxWellsPerPad} скв.");

        // ---- карта желательности
        MapBuildResult mb;
        if (o.SyntheticMap && (string.IsNullOrWhiteSpace(p.MapSource) || p.MapSource == MapSources.None))
        {
            var sm = TerrainMap.Synthetic(data.Wells);
            string synth = Path.Combine(outDir, "terrain_map_synthetic.asc");
            sm.SaveAsc(synth);
            mb = new MapBuildResult(sm, "синтетическая", "синтетическая карта " + synth);
            _log("Построена синтетическая карта желательности: " + synth);
        }
        else mb = MapBuilder.Build(p, data, o.BaseDir, outDir, _log, ct);
        var map = mb.Map;
        map.ForbiddenCode = p.ForbiddenCode;
        if (!string.IsNullOrWhiteSpace(p.MapSource) && p.MapSource != MapSources.None && p.MapSource != MapSources.Asc)
        {
            string used = Path.Combine(outDir, "terrain_map_used.asc");
            map.SaveAsc(used);
            _log("Растр карты, использованный в расчёте: " + used);
        }
        ct.ThrowIfCancellationRequested();

        var opt = new Optimizer(p, data, map);
        opt.BuildSites();
        opt.BuildSandTable();
        _log($"Кандидатных площадок: {opt.NSites}; таблица отсыпки готова ({sw.Elapsed.TotalSeconds:F1} с)");

        var results = new List<VariantResult>();
        var evalMain = new Evaluator(p, data, map);

        // 1) исходные варианты из файла — по той же модели затрат
        foreach (var iv in data.Variants)
        {
            ct.ThrowIfCancellationRequested();
            var pads = new List<((double, double), List<int>)>();
            var wh = new Dictionary<int, (double n, double e)>();
            foreach (var kv in iv.Pads)
            {
                foreach (var x in kv.Value) wh[x.well] = (x.n, x.e);
                pads.Add(((kv.Value.Average(x => x.n), kv.Value.Average(x => x.e)), kv.Value.Select(x => x.well).ToList()));
            }
            var r = evalMain.Evaluate(iv.Name ?? $"Исходный вариант {iv.Id}", "input", "Исходные данные", null, pads, wh);
            results.Add(r);
            _log($"{r.Name}: {r.TotalCost / 1000:F1} млн руб, скважин с нарушениями {r.Wells.Count(w => w.Violations.Count > 0)}");
        }

        // 2) сценарии: с ограничением разворота и без него
        string zTag = LayoutTag(p);
        var scenarios = new List<(string name, Parameters prm)>
        {
            ((p.UseAzimuthTurn ? $"Разворот ≤{p.MaxAzimuthTurn:0}°" : "Без ограничения разворота") + zTag, p)
        };
        if (p.UseAzimuthTurn && p.RunScenarioWithoutAzimuthTurn)
        {
            var p2 = p.Clone(); p2.UseAzimuthTurn = false;
            scenarios.Add(("Без ограничения разворота" + zTag, p2));
        }
        if (p.CompareZLayout && (p.PadLayout ?? "Single").Trim().Equals("Single", StringComparison.OrdinalIgnoreCase))
        {
            // сравнение: те же ограничения, кусты от ZMinWells скважин — Z-кусты (2 НДС)
            var pz = p.Clone(); pz.PadLayout = "Z";
            scenarios.Add(((p.UseAzimuthTurn ? $"Разворот ≤{p.MaxAzimuthTurn:0}°" : "Без ограничения разворота") + LayoutTag(pz), pz));
        }
        var infeasible = new List<InfeasibleVariant>();
        var sigs = new List<(List<(int, int[])> sig, string layout, VariantResult res)>();
        foreach (var (scName, prm) in scenarios)
        {
            ct.ThrowIfCancellationRequested();
            var o2 = new Optimizer(prm, data, map) { SiteN = opt.SiteN, SiteE = opt.SiteE, Sand = opt.Sand };
            if (!string.Equals(prm.PadLayout, p.PadLayout, StringComparison.OrdinalIgnoreCase))
            {
                o2.BuildSandTable();   // другая схема кустов — своя таблица отсыпки
                _log($"[{scName}] таблица отсыпки для Z-кустов готова ({sw.Elapsed.TotalSeconds:F1} с)");
            }
            o2.BuildCostMatrix();
            _log($"[{scName}] пар площадка–скважина: {o2.FeasiblePairs}, из них без нарушений: {o2.CleanPairs}");
            var ev = new Evaluator(prm, data, map);

            VariantResult AddVariant(string name, string scenario, int K, List<PadAssignment> pads, double est)
            {
                var sig = pads.Select(x => (x.Site, x.Wells.OrderBy(w => w).ToArray())).OrderBy(x => x.Site).ToList();
                string lay = (prm.PadLayout ?? "Single").Trim().ToLowerInvariant();
                foreach (var (s0, l0, r0) in sigs)
                    if (l0 == lay && SameSig(sig, s0)) { var al = r0.AliasOf(name, K); al.Scenario = scenario; results.Add(al); return r0; }
                var r = ev.Evaluate(name, "opt", scenario, K, ToPads(o2, pads));
                r.OptimizerEstimate = est;
                results.Add(r);
                sigs.Add((sig, lay, r));
                int zc = r.Pads.Count(x => x.ZPad);
                if (zc > 0) _log($"   Z-кустов (2 НДС): {zc} из {r.PadCount}");
                _log($"{name}: кустов {r.PadCount} [{string.Join(",", r.Pads.Select(x => x.WellCount))}], " +
                     $"{r.TotalCost / 1000:F1} млн руб, скважин с нарушениями {r.Wells.Count(w => w.Violations.Count > 0)} ({sw.Elapsed.TotalSeconds:F1} с)");
                return r;
            }

            List<PadAssignment> prevPads = null; double prevEst = double.PositiveInfinity;
            VariantResult bestK = null;
            for (int K = Math.Max(p.StartMaxWellsPerPad, p.MinWellsPerPad); K <= p.EndMaxWellsPerPad; K += Math.Max(1, p.VariantStep))
            {
                ct.ThrowIfCancellationRequested();
                string name = $"{scName}; {p.MinWellsPerPad}–{K} скв./куст";
                var (pads, est) = o2.Optimize(K);
                // решение для меньшего K допустимо и при большем — берём лучшее (эвристика не гарантирует монотонность)
                if (prevPads != null && (pads == null || prevEst < est - 1e-6)) { pads = prevPads.Select(x => x.Clone()).ToList(); est = prevEst; }
                if (pads != null) { prevPads = pads; prevEst = est; }
                if (pads == null)
                {
                    infeasible.Add(new InfeasibleVariant(name, scName, K, $"скважины нельзя разбить на кусты по {p.MinWellsPerPad}–{K} скв."));
                    _log($"{name}: нет решения");
                    continue;
                }
                var rv = AddVariant(name, scName, K, pads, est);
                // вариант для меньшего K допустим и здесь: если он лучше после полного расчёта — используем его
                if (bestK != null && !ReferenceEquals(rv, bestK) &&
                    (rv.Violations > bestK.Violations || (rv.Violations == bestK.Violations && rv.TotalCost > bestK.TotalCost + 1e-6)))
                {
                    results.RemoveAt(results.Count - 1);
                    sigs.RemoveAll(x => ReferenceEquals(x.res, rv) && !results.Contains(rv));
                    var al = bestK.AliasOf(name, K); al.Scenario = scName; results.Add(al);
                    _log($"   вариант для меньшего K лучше — используется «{bestK.Name}»");
                }
                else bestK = rv;
            }

            if (p.RunPadCountSweep)
            {
                var (lo, hi) = o2.PadCountRange(p.EndMaxWellsPerPad);
                for (int np = lo; np <= hi; np++)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = $"{scName}; {np} кустов по {p.MinWellsPerPad}–{p.EndMaxWellsPerPad} скв.";
                    var (pads, est) = o2.Optimize(p.EndMaxWellsPerPad, np);
                    if (pads == null) { infeasible.Add(new InfeasibleVariant(name, scName, p.EndMaxWellsPerPad, "нет разбиения на такое число кустов")); continue; }
                    _ = AddVariant(name, scName + " (число кустов)", p.EndMaxWellsPerPad, pads, est);
                }
            }
        }

        // рекомендуемый: сценарий с исходными параметрами, минимум нарушений, затем затрат
        var best = results.Where(r => r.Kind == "opt" && r.SameAs == null && r.Scenario == scenarios[0].name)
                          .OrderBy(r => r.Violations).ThenBy(r => r.TotalCost).FirstOrDefault()
                   ?? results.OrderBy(r => r.Violations).ThenBy(r => r.TotalCost).First();

        _log(Reporting.FormatSummary(results, best, p));
        foreach (var x in infeasible) _log($"{x.Name}: нет решения — {x.Reason}");
        Reporting.WriteCsv(outDir, results, p, infeasible);
        string json = Reporting.BuildJson(results, best, data, map, p, Path.GetFileName(o.InputPath), mb.Name, infeasible);
        File.WriteAllText(Path.Combine(outDir, "results.json"), json, new UTF8Encoding(false));
        string report = Path.Combine(outDir, "report.html");
        Reporting.WriteHtml(report, json);
        _log(Reporting.PlotlyEmbedded
            ? "report.html: библиотека графиков встроена, отчёт открывается без интернета."
            : "report.html: библиотека графиков не встроена — для графиков нужен доступ к cdn.jsdelivr.net / cdnjs.cloudflare.com / unpkg.com или файл plotly.min.js рядом с report.html.");
        _log($"Готово за {sw.Elapsed.TotalSeconds:F1} с. Результаты: {Path.GetFullPath(outDir)} (report.html, summary.csv, pads.csv, wells.csv, trajectories/*.csv)");

        return new RunResult
        {
            Data = data, Parameters = p, Map = map, MapName = mb.Name, Variants = results, Infeasible = infeasible,
            Best = best, OutDir = Path.GetFullPath(outDir), ReportPath = Path.GetFullPath(report), Elapsed = sw.Elapsed,
        };
    }

    private static string LayoutTag(Parameters p) => (p.PadLayout ?? "Single").Trim().ToLowerInvariant() switch
    {
        "z" => $"; Z-кусты (2 НДС) при {p.ZMinWells} скв. и более",
        "auto" => $"; однорядные или Z-кусты (2 НДС) от {p.ZMinWells} скв.",
        _ => "",
    };

    private static List<((double n, double e) center, List<int> wells)> ToPads(Optimizer o, List<PadAssignment> pads) =>
        pads.Select(x => ((o.SiteN[x.Site], o.SiteE[x.Site]), x.Wells.OrderBy(w => w).ToList())).ToList();

    private static bool SameSig(List<(int, int[])> a, List<(int, int[])> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Item1 != b[i].Item1 || !a[i].Item2.SequenceEqual(b[i].Item2)) return false;
        return true;
    }
}
