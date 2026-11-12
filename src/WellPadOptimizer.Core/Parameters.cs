using System.Text.Json;

namespace WellPadOptimizer;

/// <summary>Группа стоимости строительства куста (тыс. руб. на 1 скважину).</summary>
public sealed class PadCostGroup
{
    public int MaxWells { get; set; }
    public double CostPerWell { get; set; }
}

/// <summary>Класс местности на карте желательности.</summary>
public sealed class TerrainClass
{
    public int Code { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Высота отсыпки, м.</summary>
    public double FillHeight { get; set; }
    /// <summary>Дополнительные затраты на площадь куста (компенсация за вырубку, рекультивация и т.п.), тыс. руб./га.</summary>
    public double ExtraCostPerHa { get; set; }
    /// <summary>Размещение кустов на этом классе запрещено.</summary>
    public bool Forbidden { get; set; }
}

/// <summary>Соответствие значения ячейки карты КИН классу местности программы.</summary>
public sealed class KinCode
{
    public double Value { get; set; }
    public int Code { get; set; }
    public string Comment { get; set; } = "";
}

/// <summary>Источник карты желательности.</summary>
public static class MapSources
{
    public const string None = "None";   // без карты: вся поверхность — суходол
    public const string Asc = "Asc";     // растр ESRI ASCII Grid в проекции входных данных
    public const string Kin = "Kin";     // карта в формате КИН (.bin, сетка в градусах WGS84)
    public const string Osm = "Osm";     // открытые источники (OpenStreetMap): Overpass API или файл GeoJSON / Overpass JSON
}

/// <summary>
/// Параметры расчёта. Значения по умолчанию соответствуют окну настроек
/// (максимальный отход 1500 м, MD 4000 м, темпы набора 0,5 и 1,5 °/10 м и т.д.).
/// </summary>
public sealed class Parameters
{
    // --- пользовательские параметры ---
    public double MaxOffset { get; set; } = 1500;          // Максимальный отход от устья, м
    /// <summary>Минимальный отход точки T1 от устья для горизонтальных скважин, м (0 — не проверять).
    /// Исключает бурение ГС «под куст». Для наклонно-направленных скважин без ГС не применяется.</summary>
    public double MinOffsetT1 { get; set; } = 200;
    public double MaxMD { get; set; } = 4000;              // Максимальная длина по стволу, м
    public bool LimitsToBottomHole { get; set; } = false;  // «На забой»: ограничения до T3, иначе до T1
    public double Dls1 { get; set; } = 0.5;                // Темп первого набора, °/DlsBaseLength
    public double Dls2 { get; set; } = 1.5;                // Темп второго набора, °/DlsBaseLength
    public double DlsBaseLength { get; set; } = 10;        // База темпа, м
    public bool UseAzimuthTurn { get; set; } = true;       // Учитывать максимальный разворот
    // --- таблица Excel / CSV ---
    /// <summary>Система координат таблицы: Auto, GK (СК-42 Гаусс–Крюгер), UTM (ProjectedCrs), WGS84 (градусы).</summary>
    public string TableCrs { get; set; } = "Auto";
    /// <summary>Столбец X таблицы — восточная координата (если по значениям это не определить).</summary>
    public bool TableXIsEast { get; set; } = true;
    /// <summary>Зона Гаусса–Крюгера (0 — из префикса восточной координаты).</summary>
    public int TableGkZone { get; set; } = 0;
    /// <summary>Пересчёт СК-42 → WGS 84 (ГОСТ 32453-2017) для привязки к картам.</summary>
    public bool TableDatumShift { get; set; } = true;
    /// <summary>Глубина целей (TVD), если в таблице нет столбца глубины, м.</summary>
    public double DefaultTargetTvd { get; set; } = 2400;
    /// <summary>ГС из таблицы можно бурить с любого конца (при ReverseHorizontal = Auto).</summary>
    public bool TableReversible { get; set; } = true;

    /// <summary>Направление бурения ГС: Auto — с любого конца для целей из проектов КИН, Always — для всех, Never — строго T1→T3.</summary>
    public string ReverseHorizontal { get; set; } = "Auto";
    public double MaxAzimuthTurn { get; set; } = 90;       // Максимальный разворот по азимуту, °
    public double VerticalLength { get; set; } = 40;       // Длина вертикального участка, м
    public int VariantStep { get; set; } = 1;              // Пропуск вариантов
    public int StartMaxWellsPerPad { get; set; } = 10;     // Начальное макс. число скважин на кусте
    public int EndMaxWellsPerPad { get; set; } = 24;       // Конечное макс. число скважин на кусте
    public int MinWellsPerPad { get; set; } = 10;          // мин. число скважин на кусте (жёсткое ограничение)
    // Поиск площадок до SoftRadiusFactor·MaxOffset: технологические ограничения могут нарушаться со штрафом,
    // если при заданном размере куста иначе решения нет. Штрафы в тыс. руб.
    public double SoftRadiusFactor { get; set; } = 1.6;
    public double PenaltyViolation { get; set; } = 1.0e6;
    public double PenaltyOffsetPerM { get; set; } = 1000;
    public double PenaltyMinOffsetPerM { get; set; } = 1000;   // за 1 м недостающего отхода до T1

    /// <summary>Недостающий отход T1 от устья для ГС, м (0 — ограничение выполнено или не применяется).</summary>
    public double MinOffsetShortfall(Vec3 wh, WellTarget w)
    {
        if (MinOffsetT1 <= 0 || !w.HasHorizontal) return 0;
        double d = Geo.Hypot(w.T1.N - wh.N, w.T1.E - wh.E);
        return Math.Max(0, MinOffsetT1 - d);
    }
    public double PenaltyAzimuthPerDeg { get; set; } = 10000;
    public double PenaltyMdPerM { get; set; } = 1000;
    public double MaxHookLoad { get; set; } = 180;         // тс
    public double MaxTorque { get; set; } = 54;            // кН·м

    // --- стоимостные параметры (тыс. руб.) ---
    public double CostNNS { get; set; } = 65;              // за метр ННС (до T1)
    public double CostGS { get; set; } = 55;               // за метр ГС (T1–T3)
    public List<PadCostGroup> PadCostGroups { get; set; } = new()
    {
        new PadCostGroup { MaxWells = 12, CostPerWell = 16000 },
        new PadCostGroup { MaxWells = 19, CostPerWell = 13000 },
        new PadCostGroup { MaxWells = 24, CostPerWell = 12000 },
    };
    public double SandCostPer1000m3 { get; set; } = 1550;

    // --- расстановка устьев ---
    public int WellsInGroup { get; set; } = 4;
    public double DistanceInGroup { get; set; } = 6;
    public double DistanceBetweenGroups { get; set; } = 15;

    // --- схема куста: однорядный (1 НДС) или Z-куст (2 НДС) ---
    /// <summary>Single — однорядный куст (одно направление движения станка); Z — Z-куст (два ряда, два НДС);
    /// Auto — для каждого куста выбирается схема с меньшими затратами.</summary>
    public string PadLayout { get; set; } = "Single";
    /// <summary>Минимальное число скважин, при котором допускается Z-куст.</summary>
    public int ZMinWells { get; set; } = 12;
    /// <summary>Расстояние между рядами устьев Z-куста, м.</summary>
    public double ZRowSpacing { get; set; } = 50;
    /// <summary>Смещение рядов Z-куста вдоль НДС, м.</summary>
    public double ZRowShift { get; set; } = 0;
    /// <summary>Коэффициент к стоимости строительства Z-куста.</summary>
    public double ZPadBuildCostFactor { get; set; } = 1.0;
    /// <summary>Эффект Z-куста на скважину (одновременное бурение двумя станками: сокращение сроков, мобилизации), тыс. руб.</summary>
    public double ZPadSavingPerWell { get; set; } = 0;
    /// <summary>При однорядной схеме дополнительно посчитать сценарий с Z-кустами для сравнения.</summary>
    public bool CompareZLayout { get; set; } = true;

    // --- геометрия и отсыпка площадки ---
    public double PadWidth { get; set; } = 120;
    public double PadEndMargin { get; set; } = 60;
    public double PadMinLength { get; set; } = 100;
    public double EmbankmentSlope { get; set; } = 1.5;     // заложение откоса 1:m
    public List<TerrainClass> Terrain { get; set; } = new()
    {
        new TerrainClass { Code = 1, Name = "Суходол", FillHeight = 1.0 },
        new TerrainClass { Code = 2, Name = "Болото", FillHeight = 2.5 },
        new TerrainClass { Code = 3, Name = "Водоём", FillHeight = 4.0 },
        new TerrainClass { Code = 4, Name = "Кедровник", FillHeight = 1.0, ExtraCostPerHa = 1500 },
        new TerrainClass { Code = 5, Name = "Лес", FillHeight = 1.0, ExtraCostPerHa = 300 },
    };
    public int ForbiddenCode { get; set; } = 9;

    // --- источник карты желательности ---
    /// <summary>None — без карты (всё суходол), Asc — растр .asc, Kin — карта КИН (.bin), Osm — открытые источники.</summary>
    public string MapSource { get; set; } = MapSources.None;
    /// <summary>Файл карты: .asc для Asc, .bin для Kin, .geojson / .json (Overpass) для Osm в режиме File.</summary>
    public string MapFile { get; set; } = "";
    /// <summary>Размер ячейки растра, в который переводятся карты КИН и OSM, м.</summary>
    public double MapCellSize { get; set; } = 25;
    /// <summary>EPSG проекции координат скважин (0 — взять из входного файла, mapDefinition.projected_crs).</summary>
    public int ProjectedCrs { get; set; } = 0;
    // КИН
    public List<KinCode> KinCodes { get; set; } = new()
    {
        new KinCode { Value = 2, Code = 1, Comment = "суходол" },
        new KinCode { Value = 3, Code = 2, Comment = "болото" },
        new KinCode { Value = 4, Code = 3, Comment = "водоём, река" },
    };
    /// <summary>Строки сетки КИН идут от южного края к северному (как в Surfer).</summary>
    public bool KinRowsFromSouth { get; set; } = true;
    /// <summary>Ячейки без данных и область вне карты КИН — запретная зона (иначе — суходол).</summary>
    public bool KinNoDataForbidden { get; set; } = true;
    // OpenStreetMap
    /// <summary>Overpass — загрузить из интернета, File — взять из файла MapFile (GeoJSON или сохранённый ответ Overpass).</summary>
    public string OsmMode { get; set; } = "Overpass";
    public string OverpassUrl { get; set; } = "https://overpass-api.de/api/interpreter";
    public int OverpassTimeoutSec { get; set; } = 300;
    public bool OsmWetlands { get; set; } = true;          // болота → класс OsmWetlandCode
    public bool OsmWater { get; set; } = true;             // озёра, реки (площадные), водохранилища → OsmWaterCode
    public bool OsmRivers { get; set; } = true;            // линии рек и ручьёв с шириной → OsmWaterCode
    public bool OsmProtected { get; set; } = true;         // ООПТ, заповедники, нацпарки → запрет
    public bool OsmCedar { get; set; } = true;             // кедровники → OsmCedarCode
    public bool OsmForest { get; set; } = false;           // прочий лес → OsmForestCode
    public bool OsmBuiltUp { get; set; } = true;           // населённые пункты, промзоны, кладбища, военные объекты → запрет
    public int OsmWetlandCode { get; set; } = 2;
    public int OsmWaterCode { get; set; } = 3;
    public int OsmCedarCode { get; set; } = 4;
    public int OsmForestCode { get; set; } = 5;
    public double RiverWidth { get; set; } = 80;           // ширина линии waterway=river, м
    public double CanalWidth { get; set; } = 20;
    public double StreamWidth { get; set; } = 6;           // stream, ditch, drain
    /// <summary>Водоохранная полоса вокруг водоёмов, в которой запрещено размещение, м (0 — нет).</summary>
    public double WaterProtectionBuffer { get; set; } = 0;
    /// <summary>Файл карты желательности (.asc); пусто — карта не задана, вся территория считается DefaultTerrainCode.</summary>
    public string TerrainMapFile { get; set; } = "";
    /// <summary>Класс местности при отсутствии карты (1 — суходол).</summary>
    public int DefaultTerrainCode { get; set; } = 1;
    public double ForbiddenBuffer { get; set; } = 0;       // Мин. расстояние до запрещённой зоны, м
    public double ExtraPadGap { get; set; } = 0;           // Доп. расстояние между кустами, м

    // --- дискретизация поиска ---
    public double SiteGridStep { get; set; } = 100;
    public double FootprintSampleStep { get; set; } = 10;
    public List<double> PadOrientations { get; set; } = new() { 0, 30, 60, 90, 120, 150 };

    // --- модель «мягкой» колонны для нагрузки на крюке и момента ---
    public double PipeWeightAir { get; set; } = 0.284;     // кН/м (127 мм БТ)
    public double MudDensity { get; set; } = 1.20;         // г/см3
    public double SteelDensity { get; set; } = 7.85;
    public double FrictionCased { get; set; } = 0.25;
    public double FrictionOpenHole { get; set; } = 0.30;
    public double PipeRadius { get; set; } = 0.08;         // м (по замкам)

    // --- траектории ---
    public double TrajectoryStep { get; set; } = 10;

    // --- антистолкновение ---
    /// <summary>Проверять антистолкновение стволов (проектные между собой и с существующими скважинами).</summary>
    public bool AntiCollisionCheck { get; set; } = true;
    /// <summary>Минимальный коэффициент разделения SF = D / (r₁ + r₂); меньше — нарушение «антистолкновение».</summary>
    public double AcMinSF { get; set; } = 1.5;
    /// <summary>SF, ниже которого выдаётся предупреждение о сближении.</summary>
    public double AcWarnSF { get; set; } = 2.0;
    /// <summary>Радиус неопределённости положения ствола на устье, м.</summary>
    public double AcSurfaceError { get; set; } = 0.5;
    /// <summary>Рост радиуса неопределённости на 1000 м MD, м.</summary>
    public double AcErrorPerKm { get; set; } = 6;
    /// <summary>Шаг точек траектории при проверке с существующими скважинами в ходе оптимизации, м.</summary>
    public double AcOptimizationStep { get; set; } = 30;
    /// <summary>Глубина (MD), до которой учитывается сближение при раскладке скважин по слотам куста, м.</summary>
    public double AntiCollisionMaxMD { get; set; } = 1000;
    /// <summary>Штраф за недостающую единицу SF (сверх PenaltyViolation), тыс. руб.</summary>
    public double PenaltyAntiCollision { get; set; } = 1.0e6;
    /// <summary>Учитывать существующие скважины: trajRealWells входного файла и файл ExistingWellsFile.</summary>
    public bool UseExistingWells { get; set; } = true;
    /// <summary>Файл траекторий существующих скважин: JSON (trajRealWells), Excel или CSV (точки X, Y, Z или инклинометрия MD, зенит, азимут).</summary>
    public string ExistingWellsFile { get; set; } = "";
    /// <summary>Поправка глубин существующих скважин к системе глубин целей, м (прибавляется к Z).</summary>
    public double ExistingWellsZShift { get; set; } = 0;

    // --- режим учёта ограничений ---
    /// <summary>Soft — нарушения допускаются со штрафом; Hard — максимальный штраф: пара «площадка–скважина» с нарушением
    /// недопустима; Both — основной расчёт со штрафами и дополнительный сценарий с жёсткими ограничениями.</summary>
    public string ConstraintMode { get; set; } = "Soft";
    /// <summary>Максимальный штраф за нарушение в жёстком режиме, тыс. руб. (решения с нарушениями затем отбраковываются).</summary>
    public double PenaltyHard { get; set; } = 1.0e12;
    /// <summary>Штраф за нарушение для текущего режима.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double ViolationPenalty => HardConstraints ? PenaltyHard : PenaltyViolation;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HardConstraints => string.Equals((ConstraintMode ?? "").Trim(), "Hard", StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CompareHardConstraints => string.Equals((ConstraintMode ?? "").Trim(), "Both", StringComparison.OrdinalIgnoreCase);

    // --- набор вариантов ---
    public bool RunScenarioWithoutAzimuthTurn { get; set; } = true;
    public bool RunPadCountSweep { get; set; } = true;
    public string PadPrefix { get; set; } = "К-";
    public int StartPadNumber { get; set; } = 1;

    [System.Text.Json.Serialization.JsonIgnore] public double R1 => DlsBaseLength / (Dls1 * Math.PI / 180.0);
    [System.Text.Json.Serialization.JsonIgnore] public double R2 => DlsBaseLength / (Dls2 * Math.PI / 180.0);
    [System.Text.Json.Serialization.JsonIgnore] public int MaxPadCapacity => PadCostGroups.Max(g => g.MaxWells);

    public Parameters Clone() => (Parameters)MemberwiseClone();

    public static Parameters Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new Parameters();
        var opt = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var p = JsonSerializer.Deserialize<Parameters>(File.ReadAllText(path), opt) ?? new Parameters();
        // совместимость со старым параметром TerrainMapFile
        if (p.MapSource == MapSources.None && !string.IsNullOrWhiteSpace(p.TerrainMapFile) && string.IsNullOrWhiteSpace(p.MapFile))
        {
            p.MapSource = MapSources.Asc; p.MapFile = p.TerrainMapFile;
        }
        return p;
    }

    // ---------------- функции стоимости и геометрии площадки ----------------

    /// <summary>Стоимость строительства куста (тыс. руб.) в зависимости от числа скважин.</summary>
    public double PadBuildCost(int n)
    {
        if (n <= 0) return 0;
        foreach (var g in PadCostGroups.OrderBy(g => g.MaxWells))
            if (n <= g.MaxWells) return g.CostPerWell * n;
        return double.PositiveInfinity;
    }

    /// <summary>Длина ряда устьев: группы по WellsInGroup с шагом DistanceInGroup, между группами DistanceBetweenGroups.</summary>
    public double RowLength(int n)
    {
        if (n <= 1) return 0;
        int groups = (n - 1) / WellsInGroup;
        return (n - 1) * DistanceInGroup + groups * (DistanceBetweenGroups - DistanceInGroup);
    }

    /// <summary>Смещения слотов вдоль оси ряда относительно центра куста.</summary>
    public double[] SlotOffsets(int n)
    {
        var s = new double[n];
        for (int k = 0; k < n; k++)
            s[k] = k * DistanceInGroup + (k / WellsInGroup) * (DistanceBetweenGroups - DistanceInGroup);
        double c = n > 0 ? s[n - 1] / 2 : 0;
        for (int k = 0; k < n; k++) s[k] -= c;
        return s;
    }

    public double PadLength(int n) => Math.Max(PadMinLength, RowLength(n) + 2 * PadEndMargin);

    // ---------------- Z-кусты (два направления движения станка, НДС) ----------------

    /// <summary>Режим схемы куста: Single, Z или Auto.</summary>
    private string Mode => (PadLayout ?? "Single").Trim().ToLowerInvariant();

    /// <summary>Допустимые схемы для куста из n скважин: false — однорядный (1 НДС), true — Z-куст (2 НДС).</summary>
    public bool[] LayoutsFor(int n)
    {
        bool zOk = n >= Math.Max(2, ZMinWells);
        return Mode switch
        {
            "z" => zOk ? new[] { true } : new[] { false },
            "auto" => zOk ? new[] { false, true } : new[] { false },
            _ => new[] { false },
        };
    }

    /// <summary>Число скважин в рядах Z-куста: первый ряд (НДС 1) и второй ряд (НДС 2).</summary>
    public (int a, int b) ZRows(int n) => ((n + 1) / 2, n / 2);

    /// <summary>Размеры площадки (длина вдоль НДС, ширина).</summary>
    public (double L, double W) PadDims(int n, bool z)
    {
        if (!z) return (PadLength(n), PadWidth);
        var (a, b) = ZRows(n);
        double row = Math.Max(RowLength(a), RowLength(b)) + Math.Abs(ZRowShift);
        return (Math.Max(PadMinLength, row + 2 * PadEndMargin), PadWidth + ZRowSpacing);
    }

    /// <summary>Положение слотов устьев в осях куста: s — вдоль НДС, w — поперёк. Для Z-куста два ряда со встречными НДС.</summary>
    public List<(double s, double w)> SlotPositions(int n, bool z)
    {
        var res = new List<(double s, double w)>();
        if (!z)
        {
            foreach (var x in SlotOffsets(n)) res.Add((x, 0));
            return res;
        }
        var (a, b) = ZRows(n);
        // ряд 1: станок идёт в направлении НДС 1; ряд 2 — встречно (НДС 2 = НДС 1 + 180°), ряды смещены на ZRowShift
        foreach (var x in SlotOffsets(a)) res.Add((x - ZRowShift / 2, ZRowSpacing / 2));
        var ob = SlotOffsets(b);
        for (int k = ob.Length - 1; k >= 0; k--) res.Add((ob[k] + ZRowShift / 2, -ZRowSpacing / 2));
        return res;
    }

    /// <summary>Стоимость строительства с учётом коэффициента Z-куста.</summary>
    public double PadBuildCost(int n, bool z) => z ? PadBuildCost(n) * ZPadBuildCostFactor - ZPadSavingPerWell * n : PadBuildCost(n);

    /// <summary>Наибольшая диагональ площадки из n скважин среди допустимых схем.</summary>
    public double PadDiagonal(int n)
    {
        double d = 0;
        foreach (var z in new[] { false, true })
        {
            if (z && Mode == "single") continue;
            var (L, W) = PadDims(n, z);
            d = Math.Max(d, Math.Sqrt(L * L + W * W));
        }
        return d;
    }

    /// <summary>Объём отсыпки (м3) по долям классов местности под площадкой L×W (призматоид с откосами).</summary>
    public double SandVolume(IReadOnlyDictionary<int, double> fractions, double L, double W)
    {
        double m = EmbankmentSlope, v = 0;
        foreach (var t in Terrain)
        {
            fractions.TryGetValue(t.Code, out double f);
            double h = t.FillHeight;
            v += f * h * (L + m * h) * (W + m * h);
        }
        return v;
    }

    public double SandCost(double volume) => volume / 1000.0 * SandCostPer1000m3;

    /// <summary>Дополнительные затраты по классам местности (тыс. руб.) для куста из n скважин.</summary>
    public double TerrainExtraCost(IReadOnlyDictionary<int, double> fractions, double L, double W)
    {
        double areaHa = L * W / 10000.0, c = 0;
        foreach (var t in Terrain)
            if (t.ExtraCostPerHa != 0 && fractions.TryGetValue(t.Code, out double f)) c += f * areaHa * t.ExtraCostPerHa;
        return c;
    }

    /// <summary>Можно ли бурить горизонтальный участок цели w от T3 к T1.</summary>
    public bool CanReverse(WellTarget w)
    {
        if (!w.HasHorizontal) return false;
        string m = (ReverseHorizontal ?? "Auto").Trim().ToLowerInvariant();
        return m == "always" || (m == "auto" && w.Reversible);
    }

    /// <summary>Запрещено ли размещение куста на классе code.</summary>
    public bool IsForbidden(int code)
    {
        if (code == ForbiddenCode) return true;
        foreach (var t in Terrain) if (t.Code == code) return t.Forbidden;
        return false;
    }

    /// <summary>Класс для расчёта отсыпки: неизвестные коды считаются классом по умолчанию.</summary>
    public int NormalizeCode(int code)
    {
        foreach (var t in Terrain) if (t.Code == code) return code;
        return DefaultTerrainCode;
    }

    public string TerrainName(int code)
    {
        if (code == ForbiddenCode) return "Запретная зона";
        foreach (var t in Terrain) if (t.Code == code) return t.Name;
        return "Класс " + code;
    }

    public void Save(string path)
    {
        var opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opt));
    }

    /// <summary>Глубокая копия через JSON (списки классов и групп копируются).</summary>
    public Parameters DeepClone() => JsonSerializer.Deserialize<Parameters>(JsonSerializer.Serialize(this));
}
