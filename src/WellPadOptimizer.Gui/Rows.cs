using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace WellPadOptimizer.Gui;

/// <summary>Строка таблицы параметров (скалярные свойства Parameters).</summary>
public sealed class ParamRow : INotifyPropertyChanged
{
    private string _value = "";
    [DisplayName("Раздел")] public string Group { get; set; } = "";
    [DisplayName("Параметр")] public string Title { get; set; } = "";
    [DisplayName("Значение")]
    public string Value
    {
        get => _value;
        set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
    }
    [DisplayName("Ключ в settings.json")] public string Key { get; set; } = "";
    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>Перевод скалярных параметров в строки таблицы и обратно.</summary>
public static class ParamTable
{
    /// <summary>Подписи и разделы параметров. Параметры карты редактируются на вкладке «Карта».</summary>
    private static readonly (string key, string group, string title)[] Known =
    {
        ("MaxOffset", "Ограничения", "Максимальный отход от устья, м"),
        ("MinOffsetT1", "Ограничения", "Минимальный отход T1 от устья для ГС, м (0 — не проверять)"),
        ("MaxMD", "Ограничения", "Максимальная длина по стволу, м"),
        ("LimitsToBottomHole", "Ограничения", "Ограничения «на забой» (до T3)"),
        ("Dls1", "Ограничения", "Темп первого набора зенита, °/10 м"),
        ("Dls2", "Ограничения", "Темп второго набора зенита, °/10 м"),
        ("DlsBaseLength", "Ограничения", "База темпа набора, м"),
        ("UseAzimuthTurn", "Ограничения", "Учитывать максимальный разворот по азимуту"),
        ("MaxAzimuthTurn", "Ограничения", "Максимальный разворот по азимуту, °"),
        ("VerticalLength", "Ограничения", "Длина вертикального участка, м"),
        ("MaxHookLoad", "Ограничения", "Максимальная нагрузка на крюке, тс"),
        ("MaxTorque", "Ограничения", "Максимальный момент, кН·м"),
        ("MinWellsPerPad", "Кусты", "Минимальное число скважин на кусте"),
        ("StartMaxWellsPerPad", "Кусты", "Начальное максимальное число скважин на кусте"),
        ("EndMaxWellsPerPad", "Кусты", "Конечное максимальное число скважин на кусте"),
        ("VariantStep", "Кусты", "Шаг перебора (пропуск вариантов)"),
        ("WellsInGroup", "Кусты", "Скважин в группе"),
        ("DistanceInGroup", "Кусты", "Расстояние между скважинами в группе, м"),
        ("DistanceBetweenGroups", "Кусты", "Расстояние между группами, м"),
        ("PadWidth", "Кусты", "Ширина площадки, м"),
        ("PadEndMargin", "Кусты", "Запас площадки с торцов, м"),
        ("PadMinLength", "Кусты", "Минимальная длина площадки, м"),
        ("EmbankmentSlope", "Кусты", "Заложение откоса отсыпки 1:m"),
        ("ExtraPadGap", "Кусты", "Доп. расстояние между кустами, м"),
        ("ForbiddenBuffer", "Кусты", "Мин. расстояние до запретной зоны, м"),
        ("PadPrefix", "Кусты", "Префикс имени куста"),
        ("PadLayout", "Z-кусты", "Схема куста: Single — однорядный (1 НДС), Z — Z-куст (2 НДС), Auto — выбор по затратам"),
        ("ZMinWells", "Z-кусты", "Мин. число скважин для Z-куста"),
        ("ZRowSpacing", "Z-кусты", "Расстояние между рядами устьев Z-куста, м"),
        ("ZRowShift", "Z-кусты", "Смещение рядов Z-куста вдоль НДС, м"),
        ("ZPadBuildCostFactor", "Z-кусты", "Коэффициент стоимости строительства Z-куста"),
        ("ZPadSavingPerWell", "Z-кусты", "Эффект Z-куста (два станка) на скважину, тыс. руб."),
        ("ReverseHorizontal", "Траектории", "Направление ГС: Auto — с любого конца для проектов КИН, Always — всегда, Never — T1→T3"),
        ("CompareZLayout", "Z-кусты", "Дополнительно считать сценарий с Z-кустами для сравнения"),
        ("StartPadNumber", "Кусты", "Начальный номер куста"),
        ("CostNNS", "Стоимость", "Стоимость метра проходки ННС, тыс. руб."),
        ("CostGS", "Стоимость", "Стоимость метра проходки ГС, тыс. руб."),
        ("SandCostPer1000m3", "Стоимость", "Стоимость за тыс. м³ песка отсыпки, тыс. руб."),
        ("SoftRadiusFactor", "Оптимизация", "Радиус поиска площадок, доля макс. отхода"),
        ("PenaltyViolation", "Оптимизация", "Штраф за нарушение ограничения, тыс. руб."),
        ("PenaltyOffsetPerM", "Оптимизация", "Штраф за 1 м превышения отхода, тыс. руб."),
        ("PenaltyMinOffsetPerM", "Оптимизация", "Штраф за 1 м недостающего отхода до T1, тыс. руб."),
        ("PenaltyAzimuthPerDeg", "Оптимизация", "Штраф за 1° превышения разворота, тыс. руб."),
        ("PenaltyMdPerM", "Оптимизация", "Штраф за 1 м превышения MD, тыс. руб."),
        ("SiteGridStep", "Оптимизация", "Шаг сетки площадок-кандидатов, м"),
        ("FootprintSampleStep", "Оптимизация", "Шаг опробования площадки по карте, м"),
        ("RunScenarioWithoutAzimuthTurn", "Оптимизация", "Считать сценарий без ограничения разворота"),
        ("RunPadCountSweep", "Оптимизация", "Считать варианты с заданным числом кустов"),
        ("TrajectoryStep", "Траектории", "Шаг инклинометрии, м"),
        ("AcMinSF", "Антистолкновение", "Минимальный коэффициент разделения SF (меньше — нарушение)"),
        ("AcWarnSF", "Антистолкновение", "SF предупреждения о сближении"),
        ("AcSurfaceError", "Антистолкновение", "Радиус неопределённости на устье, м"),
        ("AcErrorPerKm", "Антистолкновение", "Рост радиуса неопределённости на 1000 м MD, м"),
        ("AntiCollisionMaxMD", "Антистолкновение", "Глубина учёта сближения при раскладке по слотам, м"),
        ("AcOptimizationStep", "Антистолкновение", "Шаг точек при проверке с существующими скважинами в оптимизации, м"),
        ("ExistingWellsZShift", "Антистолкновение", "Поправка глубин существующих скважин, м"),
        ("PenaltyAntiCollision", "Оптимизация", "Штраф за недостающую единицу SF, тыс. руб."),
        ("PenaltyHard", "Оптимизация", "Максимальный штраф за нарушение в жёстком режиме, тыс. руб."),
        ("PipeWeightAir", "Нагрузки", "Вес 1 м бурильной колонны в воздухе, кН/м"),
        ("MudDensity", "Нагрузки", "Плотность раствора, г/см³"),
        ("SteelDensity", "Нагрузки", "Плотность стали, г/см³"),
        ("FrictionCased", "Нагрузки", "Коэффициент трения в обсаженном стволе"),
        ("FrictionOpenHole", "Нагрузки", "Коэффициент трения в открытом стволе"),
        ("PipeRadius", "Нагрузки", "Радиус колонны по замкам, м"),
        ("TableCrs", "Таблица Excel/CSV", "Координаты: Auto, GK (СК-42 Гаусс–Крюгер), UTM, WGS84"),
        ("TableGkZone", "Таблица Excel/CSV", "Зона Гаусса–Крюгера (0 — из префикса координаты)"),
        ("TableDatumShift", "Таблица Excel/CSV", "Пересчёт СК-42 → WGS 84 (ГОСТ 32453-2017)"),
        ("DefaultTargetTvd", "Таблица Excel/CSV", "Глубина целей, если её нет в таблице (TVD), м"),
        ("TableReversible", "Таблица Excel/CSV", "ГС из таблицы можно бурить с любого конца"),
        ("ForbiddenCode", "Карта", "Код запретной зоны"),
        ("DefaultTerrainCode", "Карта", "Класс местности без карты (1 — суходол)"),
        ("ProjectedCrs", "Карта", "EPSG координат скважин (0 — из входного файла)"),
    };

    public static List<ParamRow> FromParameters(Parameters p)
    {
        var rows = new List<ParamRow>();
        foreach (var (key, group, title) in Known)
        {
            var prop = typeof(Parameters).GetProperty(key);
            if (prop == null) continue;
            rows.Add(new ParamRow { Key = key, Group = group, Title = title, Value = Format(prop.GetValue(p)) });
        }
        return rows;
    }

    /// <summary>Записать значения из таблицы в параметры; возвращает список ошибок.</summary>
    public static List<string> Apply(IEnumerable<ParamRow> rows, Parameters p)
    {
        var errors = new List<string>();
        foreach (var r in rows)
        {
            var prop = typeof(Parameters).GetProperty(r.Key);
            if (prop == null || !prop.CanWrite) continue;
            string s = (r.Value ?? "").Trim();
            try
            {
                object v;
                if (prop.PropertyType == typeof(double)) v = ParseDouble(s);
                else if (prop.PropertyType == typeof(int)) v = int.Parse(s, CultureInfo.InvariantCulture);
                else if (prop.PropertyType == typeof(bool)) v = ParseBool(s);
                else v = s;
                prop.SetValue(p, v);
            }
            catch (Exception)
            {
                errors.Add($"«{r.Title}»: не удалось прочитать значение «{s}»");
            }
        }
        return errors;
    }

    public static string Format(object v) => v switch
    {
        null => "",
        bool b => b ? "да" : "нет",
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    public static double ParseDouble(string s) =>
        double.Parse(s.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static bool ParseBool(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s is "да" or "true" or "1" or "yes" or "+") return true;
        if (s is "нет" or "false" or "0" or "no" or "-") return false;
        throw new FormatException(s);
    }
}

/// <summary>Строка таблицы вариантов.</summary>
public sealed class VariantRow
{
    [Browsable(false)] public VariantResult Source { get; init; }
    [DisplayName("Вариант")] public string Name { get; init; }
    [DisplayName("Кустов")] public int Pads { get; init; }
    [DisplayName("Скважин с нарушениями")] public int Violating { get; init; }
    [DisplayName("Σ MD, км")] public double MdKm { get; init; }
    [DisplayName("Бурение, млн руб")] public double Drill { get; init; }
    [DisplayName("Кусты, млн руб")] public double PadBuild { get; init; }
    [DisplayName("Отсыпка, млн руб")] public double Sand { get; init; }
    [DisplayName("Итого, млн руб")] public double Total { get; init; }
    [DisplayName("Примечание")] public string Note { get; init; }

    public static VariantRow From(VariantResult v, VariantResult best) => new()
    {
        Source = v, Name = v.Name, Pads = v.PadCount, Violating = v.Wells.Count(w => w.Violations.Count > 0),
        MdKm = Math.Round(v.MdTotal / 1000, 1), Drill = Math.Round(v.DrillCost / 1000, 1),
        PadBuild = Math.Round(v.PadBuildCost / 1000, 1), Sand = Math.Round(v.SandCost / 1000, 1),
        Total = Math.Round(v.TotalCost / 1000, 1),
        Note = ReferenceEquals(v, best) ? "рекомендуемый" : v.SameAs != null ? "совпадает с: " + v.SameAs : v.Kind == "input" ? "из входного файла" : "",
    };
}

public sealed class PadRow
{
    [DisplayName("Куст")] public string Name { get; init; }
    [DisplayName("N, м")] public double N { get; init; }
    [DisplayName("E, м")] public double E { get; init; }
    [DisplayName("Скважин")] public int Wells { get; init; }
    [DisplayName("Схема")] public string Layout { get; init; }
    [DisplayName("НДС, °")] public string Nds { get; init; }
    [DisplayName("Местность")] public string Terrain { get; init; }
    [DisplayName("Отсыпка, тыс. м³")] public double SandVolume { get; init; }
    [DisplayName("Отсыпка, млн руб")] public double SandCost { get; init; }
    [DisplayName("Строительство, млн руб")] public double BuildCost { get; init; }
    [DisplayName("Запретная зона")] public string Forbidden { get; init; }
    [DisplayName("Скважины")] public string WellList { get; init; }

    public static PadRow From(PadResult p, Parameters prm) => new()
    {
        Name = p.Name, N = Math.Round(p.N, 1), E = Math.Round(p.E, 1), Wells = p.WellCount, Layout = p.Layout,
        Nds = p.Nds2.HasValue ? $"{p.Nds1:F0} / {p.Nds2.Value:F0}" : $"{p.Nds1:F0}",
        Terrain = string.Join(", ", p.TerrainFractions.Where(kv => kv.Value > 0.005).OrderByDescending(kv => kv.Value)
                                   .Select(kv => $"{prm.TerrainName(kv.Key)} {kv.Value:P0}")),
        SandVolume = Math.Round(p.SandVolume / 1000, 1), SandCost = Math.Round(p.SandCost / 1000, 1),
        BuildCost = Math.Round(p.BuildCost / 1000, 1), Forbidden = p.Forbidden ? "да" : "нет",
        WellList = string.Join(" ", p.Wells),
    };
}

public sealed class WellRow
{
    [DisplayName("Скважина")] public string Well { get; init; }
    [DisplayName("Куст")] public string Pad { get; init; }
    [DisplayName("Слот")] public int Slot { get; init; }
    [DisplayName("Отход до T1, м")] public double Offset { get; init; }
    [DisplayName("Разворот, °")] public double Turn { get; init; }
    [DisplayName("MD до T1, м")] public double? MdT1 { get; init; }
    [DisplayName("MD до T3, м")] public double? MdT3 { get; init; }
    [DisplayName("ГС, м")] public double Hz { get; init; }
    [DisplayName("Зенит тангенса, °")] public double? Inc { get; init; }
    [DisplayName("Крюк, тс")] public double? Hook { get; init; }
    [DisplayName("Момент, кН·м")] public double? Torque { get; init; }
    [DisplayName("Мин. SF")] public double? Sf { get; init; }
    [DisplayName("Сближение с")] public string AcWith { get; init; }
    [DisplayName("Расстояние, м")] public double? Sep { get; init; }
    [DisplayName("Бурение, млн руб")] public double? Cost { get; init; }
    [DisplayName("Нарушения")] public string Violations { get; init; }
    [DisplayName("Предупреждения")] public string Warnings { get; init; }

    private static double? R(double? v, int d = 0) => v.HasValue ? Math.Round(v.Value, d) : null;

    public static WellRow From(WellResult w) => new()
    {
        Well = w.Well, Pad = w.Pad, Slot = w.Slot, Offset = Math.Round(w.Offset), Turn = Math.Round(w.AzimuthTurn),
        MdT1 = R(w.MdT1), MdT3 = R(w.MdT3), Hz = Math.Round(w.Horizontal), Inc = R(w.TangentInc, 1),
        Hook = R(w.HookLoad), Torque = R(w.Torque, 1), Sf = R(w.MinSf, 2), AcWith = w.AcWith, Sep = R(w.MinSeparation, 1),
        Cost = w.Cost.HasValue ? Math.Round(w.Cost.Value / 1000, 1) : null,
        Violations = string.Join(", ", w.Violations), Warnings = string.Join(", ", w.Warnings),
    };
}

/// <summary>Подписи столбцов DataGrid по атрибуту DisplayName.</summary>
public static class GridHeaders
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["Code"] = "Код класса", ["Name"] = "Название", ["FillHeight"] = "Высота отсыпки, м",
        ["ExtraCostPerHa"] = "Доп. затраты, тыс. руб./га", ["Forbidden"] = "Запрет размещения",
        ["Value"] = "Значение в карте КИН", ["Comment"] = "Комментарий",
        ["Layout"] = "Схема", ["Nds1"] = "НДС 1, °", ["Nds2"] = "НДС 2, °",
    };

    public static void Apply(object sender, System.Windows.Controls.DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyDescriptor is PropertyDescriptor pd)
        {
            if (pd.Attributes[typeof(BrowsableAttribute)] is BrowsableAttribute b && !b.Browsable) { e.Cancel = true; return; }
            if (pd.Attributes[typeof(DisplayNameAttribute)] is DisplayNameAttribute dn && !string.IsNullOrEmpty(dn.DisplayName)
                && dn.DisplayName != pd.Name)
            {
                e.Column.Header = dn.DisplayName;
                return;
            }
        }
        if (Names.TryGetValue(e.PropertyName, out var h)) e.Column.Header = h;
    }
}
