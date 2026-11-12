using System.Text;

namespace WellPadOptimizer;

/// <summary>
/// Консольная программа.
/// WellPadOptimizer --input data/input_mass.json [--settings settings.json] [--out results]
///                  [--map-source None|Asc|Kin|Osm] [--map файл] [--osm-mode Overpass|File]
///                  [--pad-layout Single|Z|Auto] [--project-settings] [--synthetic-map] [--no-pause]
///                  [--existing файл] [--constraints Soft|Hard|Both] [--no-anticollision] [--no-existing]
/// Входной файл: input_mass.json (rndrilling) или проект кустования КИН (*.json с props / wrappers / assets).
/// Без ключей карты используется источник из settings.json (по умолчанию — без карты, всё суходол).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int code;
        try { code = Run(args); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Расчёт прерван."); code = 3; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            code = 1;
        }
        Pause(args, code);
        return code;
    }

    /// <summary>Окно не закрывается после расчёта; ключ --no-pause, перенаправленный ввод и CI=true отключают паузу.</summary>
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
        string input = Arg(args, "--input") ?? Path.Combine(AppContext.BaseDirectory, "data", "input_mass.json");
        string settings = Arg(args, "--settings") ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
        string outDir = Arg(args, "--out") ?? "results";

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Ошибка: не найден входной файл {Path.GetFullPath(input)}.");
            Console.Error.WriteLine("Укажите его ключом --input <путь к input_mass.json> или положите в папку data/.");
            return 2;
        }
        if (!File.Exists(settings)) Console.WriteLine($"Файл настроек {settings} не найден — используются параметры по умолчанию.");
        var p = Parameters.Load(settings);

        // ключи командной строки имеют приоритет над settings.json
        string map = Arg(args, "--map"), src = Arg(args, "--map-source"), osmMode = Arg(args, "--osm-mode");
        if (!string.IsNullOrWhiteSpace(map))
        {
            p.MapFile = map;
            if (string.IsNullOrWhiteSpace(src))
            {
                string ext = Path.GetExtension(map).ToLowerInvariant();
                src = ext == ".asc" ? MapSources.Asc : ext == ".bin" ? MapSources.Kin : (ext == ".json" || ext == ".geojson") ? MapSources.Osm : MapSources.Asc;
                if (src == MapSources.Osm) p.OsmMode = "File";
            }
        }
        if (!string.IsNullOrWhiteSpace(src)) p.MapSource = src;
        if (!string.IsNullOrWhiteSpace(osmMode)) p.OsmMode = osmMode;
        string layout = Arg(args, "--pad-layout");
        if (!string.IsNullOrWhiteSpace(layout)) p.PadLayout = layout;
        string existing = Arg(args, "--existing"), constraints = Arg(args, "--constraints");
        if (!string.IsNullOrWhiteSpace(existing)) p.ExistingWellsFile = existing;
        if (!string.IsNullOrWhiteSpace(constraints)) p.ConstraintMode = constraints;
        if (args.Contains("--no-anticollision")) p.AntiCollisionCheck = false;
        if (args.Contains("--no-existing")) p.UseExistingWells = false;

        var runner = new Runner(Console.WriteLine);
        runner.Run(new RunOptions
        {
            InputPath = input,
            OutDir = outDir,
            BaseDir = Path.GetDirectoryName(Path.GetFullPath(settings)) ?? AppContext.BaseDirectory,
            Parameters = p,
            SyntheticMap = args.Contains("--synthetic-map"),
            UseProjectSettings = args.Contains("--project-settings"),
        });
        return 0;
    }

    private static string Arg(string[] a, string key)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1];
        return null;
    }
}
