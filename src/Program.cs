using System.Diagnostics;
using System.Text;

namespace WellPadOptimizer;

/// <summary>
/// Кустование скважин и расчёт траекторий.
/// Запуск:  WellPadOptimizer --input data/input_mass.json [--map data/terrain_map.asc] [--settings settings.json] [--out results]
/// </summary>
/// <summary>Вариант, для которого нет допустимого разбиения скважин на кусты.</summary>
public sealed record InfeasibleVariant(string Name, string Scenario, int K, string Reason);

public static class Program
{
    public static int Main(string[] args)
    {
        int code = Run(args);
        Pause(args, code);
        return code;
    }

    /// <summary>
    /// Окно не закрывается после расчёта: ждём нажатия клавиши.
    /// Пауза отключается ключом --no-pause, при перенаправленном вводе и в CI (переменная CI=true).
    /// </summary>
    private static void Pause(string[] args, int code)
    {
        if (args.Contains("--no-pause") || Console.IsInputRedirected ||
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            return;
        Console.WriteLine();
        Console.WriteLine(code == 0 ? "Расчёт завершён. Нажмите любую клавишу для выхода…" : "Расчёт завершён с ошибкой. Нажмите любую клавишу для выхода…");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { Console.ReadLine(); }
    }

    private static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string input = Arg(args, "--input") ?? Path.Combine(AppContext.BaseDirectory, "data", "input_mass.json");
        string mapPath = Arg(args, "--map");
        string settings = Arg(args, "--settings") ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
        string outDir = Arg(args, "--out") ?? "results";
        Directory.CreateDirectory(outDir);
        var sw = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Ошибка: не найден входной файл {Path.GetFullPath(input)}.");
                Console.Error.WriteLine("Укажите его ключом --input <путь к input_mass.json> или положите в папку data/.");
                return 2;
            }
            if (!File.Exists(settings))
                Console.WriteLine($"Файл настроек {settings} не найден — используются параметры по умолчанию.");
            var p = Parameters.Load(settings);
            var data = InputData.Load(input);
            Console.WriteLine($"Целей: {data.Wells.Count}, исходных вариантов: {data.Variants.Count}, фактических скважин: {data.RealWells.Count}");
            Console.WriteLine($"R1 = {p.R1:F0} м, R2 = {p.R2:F0} м (темпы {p.Dls1} и {p.Dls2} °/{p.DlsBaseLength} м)");

            // Карта желательности: ключ --map или параметр TerrainMapFile в settings.json.
            // Если карта не задана или файл не найден, вся территория считается суходолом (DefaultTerrainCode).
            if (string.IsNullOrWhiteSpace(mapPath) && !string.IsNullOrWhiteSpace(p.TerrainMapFile))
            {
                mapPath = p.TerrainMapFile;
                if (!Path.IsPathRooted(mapPath))
                {
                    string baseDir = Path.GetDirectoryName(Path.GetFullPath(settings)) ?? AppContext.BaseDirectory;
                    mapPath = Path.Combine(baseDir, mapPath);
                }
            }
            TerrainMap map; string mapName;
            if (!string.IsNullOrWhiteSpace(mapPath) && File.Exists(mapPath))
            {
                map = TerrainMap.LoadAsc(mapPath);
                mapName = Path.GetFileName(mapPath);
                Console.WriteLine($"Карта желательности: {mapPath} ({map.NCols}×{map.NRows}, ячейка {map.CellSize} м)");
            }
            else if (args.Contains("--synthetic-map"))
            {
                map = TerrainMap.Synthetic(data.Wells);
                string synth = Path.Combine(outDir, "terrain_map_synthetic.asc");
                map.SaveAsc(synth);
                mapName = "синтетическая (" + Path.GetFileName(synth) + ")";
                Console.WriteLine("Построена синтетическая карта желательности: " + synth);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(mapPath))
                    Console.WriteLine($"Внимание: файл карты {mapPath} не найден.");
                var dry = p.Terrain.FirstOrDefault(t => t.Code == p.DefaultTerrainCode);
                string dryName = dry?.Name ?? ("класс " + p.DefaultTerrainCode);
                double margin = p.MaxOffset * p.SoftRadiusFactor + p.PadDiagonal(p.MaxPadCapacity) + 500;
                map = TerrainMap.Uniform(data.Wells, p.DefaultTerrainCode, margin);
                mapName = $"не задана — вся территория: {dryName.ToLowerInvariant()}";
                Console.WriteLine($"Карта желательности не задана — вся территория считается: {dryName.ToLowerInvariant()} " +
                                  $"(высота отсыпки {(dry?.FillHeight ?? 0):0.0} м, запретных зон нет)");
            }
            map.ForbiddenCode = p.ForbiddenCode;

            var opt = new Optimizer(p, data, map);
            opt.BuildSites();
            opt.BuildSandTable();
            Console.WriteLine($"Кандидатных площадок: {opt.NSites}; таблица отсыпки готова ({sw.Elapsed.TotalSeconds:F1} с)");

            var results = new List<VariantResult>();
            var evalMain = new Evaluator(p, data, map);

            // 1) исходные варианты из файла — расчёт по той же модели затрат
            foreach (var iv in data.Variants)
            {
                var pads = new List<((double, double), List<int>)>();
                var wh = new Dictionary<int, (double n, double e)>();
                foreach (var kv in iv.Pads)
                {
                    foreach (var x in kv.Value) wh[x.well] = (x.n, x.e);
                    pads.Add(((kv.Value.Average(x => x.n), kv.Value.Average(x => x.e)), kv.Value.Select(x => x.well).ToList()));
                }
                var r = evalMain.Evaluate($"Исходный вариант {iv.Id}", "input", "Исходные данные", null, pads, wh);
                results.Add(r);
                Console.WriteLine($"{r.Name}: {r.TotalCost / 1000:F1} млн руб, нарушений {r.Violations}");
            }

            // 2) перебор максимального числа скважин на кусте
            var scenarios = new List<(string name, Parameters prm)>
            {
                (p.UseAzimuthTurn ? $"Разворот ≤{p.MaxAzimuthTurn:0}°" : "Без ограничения разворота", p)
            };
            if (p.UseAzimuthTurn && p.RunScenarioWithoutAzimuthTurn)
            {
                var p2 = p.Clone(); p2.UseAzimuthTurn = false;
                scenarios.Add(("Без ограничения разворота", p2));
            }
            var infeasible = new List<InfeasibleVariant>();
            var sigs = new List<(List<(int, int[])> sig, VariantResult res)>();
            foreach (var (scName, prm) in scenarios)
            {
                var o = new Optimizer(prm, data, map) { SiteN = opt.SiteN, SiteE = opt.SiteE, Sand = opt.Sand };
                o.BuildCostMatrix();
                Console.WriteLine($"[{scName}] пар площадка–скважина: {o.FeasiblePairs}, из них без нарушений: {o.CleanPairs}");
                var ev = new Evaluator(prm, data, map);

                VariantResult AddVariant(string name, string scenario, int K, List<PadAssignment> pads, double est)
                {
                    var sig = pads.Select(x => (x.Site, x.Wells.OrderBy(w => w).ToArray())).OrderBy(x => x.Site).ToList();
                    foreach (var (s0, r0) in sigs)
                        if (SameSig(sig, s0)) { var al = r0.AliasOf(name, K); al.Scenario = scenario; results.Add(al); return al; }
                    var r = ev.Evaluate(name, "opt", scenario, K, ToPads(o, pads));
                    r.OptimizerEstimate = est;
                    results.Add(r); sigs.Add((sig, r));
                    Console.WriteLine($"{name}: кустов {r.PadCount} [{string.Join(",", r.Pads.Select(x => x.WellCount))}], " +
                        $"{r.TotalCost / 1000:F1} млн руб, скважин с нарушениями {r.Wells.Count(w => w.Violations.Count > 0)} ({sw.Elapsed.TotalSeconds:F1} с)");
                    return r;
                }

                // 2) перебор верхней границы размера куста K при нижней границе MinWellsPerPad
                for (int K = Math.Max(p.StartMaxWellsPerPad, p.MinWellsPerPad); K <= p.EndMaxWellsPerPad; K += Math.Max(1, p.VariantStep))
                {
                    string name = $"{scName}; {p.MinWellsPerPad}–{K} скв./куст";
                    var (pads, est) = o.Optimize(K);
                    if (pads == null)
                    {
                        infeasible.Add(new InfeasibleVariant(name, scName, K, $"скважины нельзя разбить на кусты по {p.MinWellsPerPad}–{K} скв."));
                        Console.WriteLine($"{name}: нет решения");
                        continue;
                    }
                    AddVariant(name, scName, K, pads, est);
                }

                // 3) варианты с заданным общим числом кустов
                if (p.RunPadCountSweep)
                {
                    var (lo, hi) = o.PadCountRange(p.EndMaxWellsPerPad);
                    for (int np = lo; np <= hi; np++)
                    {
                        string name = $"{scName}; {np} кустов по {p.MinWellsPerPad}–{p.EndMaxWellsPerPad} скв.";
                        var (pads, est) = o.Optimize(p.EndMaxWellsPerPad, np);
                        if (pads == null) { infeasible.Add(new InfeasibleVariant(name, scName, p.EndMaxWellsPerPad, "нет разбиения на такое число кустов")); continue; }
                        AddVariant(name, scName + " (число кустов)", p.EndMaxWellsPerPad, pads, est);
                    }
                }
            }

            // рекомендуемый вариант: сценарий с исходными параметрами, минимум скважин с нарушениями, затем затрат
            var best = results.Where(r => r.Kind == "opt" && r.SameAs == null && r.Scenario == scenarios[0].name)
                              .OrderBy(r => r.Violations).ThenBy(r => r.TotalCost).FirstOrDefault()
                       ?? results.OrderBy(r => r.Violations).ThenBy(r => r.TotalCost).First();

            Reporting.PrintSummary(results, best);
            foreach (var x in infeasible) Console.WriteLine($"{x.Name}: нет решения — {x.Reason}");
            Reporting.WriteCsv(outDir, results, p, infeasible);
            string json = Reporting.BuildJson(results, best, data, map, p, Path.GetFileName(input), mapName, infeasible);
            File.WriteAllText(Path.Combine(outDir, "results.json"), json, new UTF8Encoding(false));
            Reporting.WriteHtml(Path.Combine(outDir, "report.html"), json);
            Console.WriteLine(Reporting.PlotlyEmbedded
                ? "report.html: библиотека графиков встроена, отчёт открывается без интернета."
                : "report.html: библиотека графиков не встроена — для графиков нужен доступ к cdn.jsdelivr.net / cdnjs.cloudflare.com / unpkg.com " +
                  "или файл plotly.min.js рядом с report.html.");
            Console.WriteLine($"\nГотово за {sw.Elapsed.TotalSeconds:F1} с. Результаты: {Path.GetFullPath(outDir)} (report.html, summary.csv, pads.csv, wells.csv, trajectories/*.csv)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static List<((double n, double e) center, List<int> wells)> ToPads(Optimizer o, List<PadAssignment> pads) =>
        pads.Select(x => ((o.SiteN[x.Site], o.SiteE[x.Site]), x.Wells.OrderBy(w => w).ToList())).ToList();

    private static bool SameSig(List<(int, int[])> a, List<(int, int[])> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Item1 != b[i].Item1 || !a[i].Item2.SequenceEqual(b[i].Item2)) return false;
        return true;
    }


    private static string Arg(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1];
        return null;
    }
}
