using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WellPadOptimizer.Gui;

public partial class MainWindow : Window
{
    private readonly bool _ready;
    private Parameters _p = new();
    private ObservableCollection<TerrainClass> _terrain = new();
    private ObservableCollection<KinCode> _kin = new();
    private List<ParamRow> _paramRows = new();
    private CancellationTokenSource _cts;
    private RunResult _result;
    private TerrainMap _map;          // карта для отображения (после предпросмотра или расчёта)
    private InputData _data;
    private BitmapSource _mapBitmap;
    private Parameters _mapParams;    // параметры, с которыми построена карта (для цветов)
    private Scene3D _scene;
    private List<(string name, double e0, double n0, double e1, double n1)> _regions = new();

    public MainWindow()
    {
        InitializeComponent();
        _scene = new Scene3D(vp3d);
        string baseDir = AppContext.BaseDirectory;
        tbInput.Text = Path.Combine(baseDir, "data", "input_mass.json");
        tbSettings.Text = Path.Combine(baseDir, "settings.json");
        tbOut.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WellPadOptimizer", "results");
        _p = File.Exists(tbSettings.Text) ? Parameters.Load(tbSettings.Text) : new Parameters();
        LoadParamsToUi(_p);
        _ready = true;
        UpdateMapControls();
        Log("WellPadOptimizer: выберите входной файл, источник карты и нажмите «Рассчитать».");
    }

    // ================================================================ параметры <-> интерфейс

    private void LoadParamsToUi(Parameters p)
    {
        switch ((p.MapSource ?? "").Trim().ToLowerInvariant())
        {
            case "kin": rbMapKin.IsChecked = true; break;
            case "osm": rbMapOsm.IsChecked = true; break;
            case "asc": rbMapAsc.IsChecked = true; break;
            default: rbMapNone.IsChecked = true; break;
        }
        tbMapFile.Text = p.MapFile ?? "";
        tbCellSize.Text = ParamTable.Format(p.MapCellSize);
        cbKinSouth.IsChecked = p.KinRowsFromSouth;
        cbKinNoData.IsChecked = p.KinNoDataForbidden;
        _kin = new ObservableCollection<KinCode>(p.KinCodes.Select(k => new KinCode { Value = k.Value, Code = k.Code, Comment = k.Comment }));
        dgKin.ItemsSource = _kin;
        rbOsmFile.IsChecked = string.Equals(p.OsmMode, "File", StringComparison.OrdinalIgnoreCase);
        rbOsmOverpass.IsChecked = rbOsmFile.IsChecked != true;
        tbOverpass.Text = p.OverpassUrl;
        cbWetlands.IsChecked = p.OsmWetlands; cbWater.IsChecked = p.OsmWater; cbRivers.IsChecked = p.OsmRivers;
        cbProtected.IsChecked = p.OsmProtected; cbCedar.IsChecked = p.OsmCedar; cbForest.IsChecked = p.OsmForest; cbBuilt.IsChecked = p.OsmBuiltUp;
        tbRiverW.Text = ParamTable.Format(p.RiverWidth); tbCanalW.Text = ParamTable.Format(p.CanalWidth);
        tbStreamW.Text = ParamTable.Format(p.StreamWidth); tbWaterBuffer.Text = ParamTable.Format(p.WaterProtectionBuffer);
        _terrain = new ObservableCollection<TerrainClass>(p.Terrain.Select(t => new TerrainClass
        { Code = t.Code, Name = t.Name, FillHeight = t.FillHeight, ExtraCostPerHa = t.ExtraCostPerHa, Forbidden = t.Forbidden }));
        dgTerrain.ItemsSource = _terrain;
        _paramRows = ParamTable.FromParameters(p);
        dgParams.ItemsSource = _paramRows;
        cbAntiCollision.IsChecked = p.AntiCollisionCheck;
        cbExistingWells.IsChecked = p.UseExistingWells;
        tbExisting.Text = p.ExistingWellsFile ?? "";
        string mode = (p.ConstraintMode ?? "Soft").Trim();
        cbConstraintMode.SelectedIndex = mode.Equals("Hard", StringComparison.OrdinalIgnoreCase) ? 1 : mode.Equals("Both", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }

    /// <summary>Собрать параметры из интерфейса. Возвращает null и показывает ошибки, если значения некорректны.</summary>
    private Parameters ReadParamsFromUi()
    {
        dgParams.CommitEdit(); dgTerrain.CommitEdit(); dgKin.CommitEdit();
        var p = _p.DeepClone();
        var errors = ParamTable.Apply(_paramRows, p);
        p.MapSource = rbMapKin.IsChecked == true ? MapSources.Kin : rbMapOsm.IsChecked == true ? MapSources.Osm
                    : rbMapAsc.IsChecked == true ? MapSources.Asc : MapSources.None;
        p.MapFile = tbMapFile.Text.Trim();
        p.AntiCollisionCheck = cbAntiCollision.IsChecked == true;
        p.UseExistingWells = cbExistingWells.IsChecked == true;
        p.ExistingWellsFile = tbExisting.Text.Trim();
        p.ConstraintMode = (cbConstraintMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Soft";
        p.KinRowsFromSouth = cbKinSouth.IsChecked == true;
        p.KinNoDataForbidden = cbKinNoData.IsChecked == true;
        p.KinCodes = _kin.Where(k => k != null).Select(k => new KinCode { Value = k.Value, Code = k.Code, Comment = k.Comment }).ToList();
        p.OsmMode = rbOsmFile.IsChecked == true ? "File" : "Overpass";
        p.OverpassUrl = tbOverpass.Text.Trim();
        p.OsmWetlands = cbWetlands.IsChecked == true; p.OsmWater = cbWater.IsChecked == true; p.OsmRivers = cbRivers.IsChecked == true;
        p.OsmProtected = cbProtected.IsChecked == true; p.OsmCedar = cbCedar.IsChecked == true; p.OsmForest = cbForest.IsChecked == true;
        p.OsmBuiltUp = cbBuilt.IsChecked == true;
        p.Terrain = _terrain.Where(t => t != null).Select(t => new TerrainClass
        { Code = t.Code, Name = t.Name ?? "", FillHeight = t.FillHeight, ExtraCostPerHa = t.ExtraCostPerHa, Forbidden = t.Forbidden }).ToList();
        void Num(TextBox tb, string title, Action<double> set)
        {
            try { set(ParamTable.ParseDouble(tb.Text)); }
            catch (Exception) { errors.Add($"«{title}»: не удалось прочитать значение «{tb.Text}»"); }
        }
        Num(tbCellSize, "Шаг растра", v => p.MapCellSize = v);
        Num(tbRiverW, "Ширина реки", v => p.RiverWidth = v);
        Num(tbCanalW, "Ширина канала", v => p.CanalWidth = v);
        Num(tbStreamW, "Ширина ручья", v => p.StreamWidth = v);
        Num(tbWaterBuffer, "Водоохранная полоса", v => p.WaterProtectionBuffer = v);
        if (p.MapSource != MapSources.None && string.IsNullOrWhiteSpace(p.MapFile) && !(p.MapSource == MapSources.Osm && p.OsmMode == "Overpass"))
            errors.Add("Не выбран файл карты.");
        if (p.Terrain.Count == 0) errors.Add("Не задан ни один класс местности.");
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Проверьте параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return p;
    }

    private void UpdateMapControls()
    {
        if (!_ready) return;
        bool kin = rbMapKin.IsChecked == true, osm = rbMapOsm.IsChecked == true, asc = rbMapAsc.IsChecked == true;
        gbKin.Visibility = kin ? Visibility.Visible : Visibility.Collapsed;
        gbOsm.Visibility = osm ? Visibility.Visible : Visibility.Collapsed;
        bool file = kin || asc || osm;
        lblMapFile.Visibility = dpMapFile.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        lblMapFile.Text = kin ? "Файл карты КИН (.bin)" : asc ? "Файл растра (.asc)" : "Файл OSM (.geojson / .json) — для режима «из файла»";
    }

    private void MapSource_Changed(object sender, RoutedEventArgs e) => UpdateMapControls();

    // ================================================================ файлы

    private void BtnInput_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Входные данные (*.json;*.xlsx;*.csv)|*.json;*.xlsx;*.xlsm;*.csv;*.txt|Проект КИН, input_mass (*.json)|*.json|Таблица Excel (*.xlsx)|*.xlsx;*.xlsm|CSV (*.csv;*.txt)|*.csv;*.txt|Все файлы|*.*", FileName = tbInput.Text };
        if (d.ShowDialog(this) == true) tbInput.Text = d.FileName;
    }

    private void BtnExisting_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Траектории существующих скважин (*.json;*.xlsx;*.csv)|*.json;*.xlsx;*.xlsm;*.csv;*.txt|Все файлы|*.*" };
        if (d.ShowDialog(this) == true) tbExisting.Text = d.FileName;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Настройки (*.json)|*.json|Все файлы|*.*" };
        if (d.ShowDialog(this) == true) { tbSettings.Text = d.FileName; BtnLoadSettings_Click(sender, e); }
    }

    private void BtnLoadSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _p = Parameters.Load(tbSettings.Text);
            LoadParamsToUi(_p);
            UpdateMapControls();
            Log("Настройки загружены: " + tbSettings.Text);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка чтения настроек", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings(tbSettings.Text);

    private void BtnSaveSettingsAs_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "Настройки (*.json)|*.json", FileName = "settings.json" };
        if (d.ShowDialog(this) == true) { tbSettings.Text = d.FileName; SaveSettings(d.FileName); }
    }

    private void SaveSettings(string path)
    {
        var p = ReadParamsFromUi();
        if (p == null) return;
        try { p.Save(path); _p = p; Log("Настройки сохранены: " + path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка записи настроек", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void BtnOut_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Папка результатов" };
        if (d.ShowDialog(this) == true) tbOut.Text = d.FolderName;
    }

    private void BtnMapFile_Click(object sender, RoutedEventArgs e)
    {
        string filter = rbMapKin.IsChecked == true ? "Карта КИН (*.bin)|*.bin|Все файлы|*.*"
                      : rbMapAsc.IsChecked == true ? "ESRI ASCII Grid (*.asc)|*.asc|Все файлы|*.*"
                      : "GeoJSON / Overpass JSON (*.geojson;*.json)|*.geojson;*.json|Все файлы|*.*";
        var d = new OpenFileDialog { Filter = filter };
        if (d.ShowDialog(this) == true)
        {
            tbMapFile.Text = d.FileName;
            if (rbMapOsm.IsChecked == true) rbOsmFile.IsChecked = true;
        }
    }

    private string SettingsDir()
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(tbSettings.Text)) ?? AppContext.BaseDirectory; }
        catch (Exception) { return AppContext.BaseDirectory; }
    }

    // ================================================================ предпросмотр карты

    private async void BtnPreviewMap_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadParamsFromUi();
        if (p == null) return;
        string input = tbInput.Text, outDir = tbOut.Text, baseDir = SettingsDir();
        bool useProject = cbProjectSettings.IsChecked == true;
        SetBusy(true, "Построение карты…");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var (data, mb) = await Task.Run(() =>
            {
                var d = InputData.Load(input, p);
                if (d.Format == "project" && useProject) ProjectLoader.ApplyProjectSettings(d, p);
                var m = MapBuilder.Build(p, d, baseDir, outDir, s => Dispatcher.InvokeAsync(() => Log(s)), ct);
                return (d, m);
            });
            _data = data;
            ShowMap(mb.Map, p);
            _result = null;
            RefreshRegions();
            RedrawPlan(null);
            tabsRight.SelectedIndex = 1;
            tbStatus.Text = "Карта: " + mb.Description;
        }
        catch (OperationCanceledException) { Log("Построение карты прервано."); }
        catch (Exception ex) { Log("Ошибка: " + ex.Message); MessageBox.Show(this, ex.Message, "Карта желательности", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false, null); }
    }

    private void ShowMap(TerrainMap map, Parameters p)
    {
        _map = map; _mapParams = p;
        _mapBitmap = PlanRenderer.MapBitmap(map, p);
        imgMap.Source = _mapBitmap;
        imgMap.Width = map.NCols; imgMap.Height = map.NRows;
        wpLegend.Children.Clear();
        var items = p.Terrain.Select(t => (t.Name, Palette.ForClass(p, t.Code))).ToList();
        items.Add(("Запретная зона", Palette.Forbidden));
        foreach (var (name, color) in items)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            sp.Children.Add(new Border { Width = 12, Height = 12, Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(new TextBlock { Text = name });
            wpLegend.Children.Add(sp);
        }
        wpLegend.Children.Add(new TextBlock { Text = $"   ячейка {map.CellSize:0.#} м, {map.NCols}×{map.NRows}", Foreground = Brushes.Gray });
    }

    // ================================================================ расчёт

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadParamsFromUi();
        if (p == null) return;
        if (!File.Exists(tbInput.Text))
        {
            MessageBox.Show(this, "Не найден входной файл:\n" + tbInput.Text, "Расчёт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var opts = new RunOptions
        {
            InputPath = tbInput.Text, OutDir = tbOut.Text, BaseDir = SettingsDir(), Parameters = p,
            UseProjectSettings = cbProjectSettings.IsChecked == true,
        };
        tbLog.Clear();
        tabsRight.SelectedIndex = 0;
        SetBusy(true, "Идёт расчёт…");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var runner = new Runner(s => Dispatcher.InvokeAsync(() => Log(s)));
            var res = await Task.Run(() => runner.Run(opts, ct));
            _result = res; _data = res.Data;
            if (opts.UseProjectSettings && res.Data.Format == "project")
            {
                _p = res.Parameters; LoadParamsToUi(_p); UpdateMapControls();
                Log("Параметры проекта перенесены на вкладки «Карта» и «Параметры».");
            }
            ShowMap(res.Map, res.Parameters);
            RefreshRegions();
            cbVariant.ItemsSource = res.Variants.Select(v => v.Name + (ReferenceEquals(v, res.Best) ? "  ◄ рекомендуемый" : v.SameAs != null ? "  (совпадает)" : "")).ToList();
            cbVariant.SelectedIndex = Math.Max(0, res.Variants.IndexOf(res.Best));
            dgVariants.ItemsSource = res.Variants.Select(v => VariantRow.From(v, res.Best)).ToList();
            btnReport.IsEnabled = btnFolder.IsEnabled = true;
            tbStatus.Text = $"Готово за {res.Elapsed.TotalSeconds:F0} с. Рекомендуемый вариант: {res.Best.Name}, {res.Best.TotalCost / 1000:F1} млн руб.";
            tabsRight.SelectedIndex = 3;
        }
        catch (OperationCanceledException) { Log("Расчёт прерван."); tbStatus.Text = "Расчёт прерван."; }
        catch (Exception ex)
        {
            Log("Ошибка: " + ex.Message);
            tbStatus.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Ошибка расчёта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false, null); }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); tbStatus.Text = "Остановка…"; }

    private void BtnReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result != null && File.Exists(_result.ReportPath)) OpenShell(_result.ReportPath);
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_result != null && Directory.Exists(_result.OutDir)) OpenShell(_result.OutDir);
    }

    private static void OpenShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SetBusy(bool busy, string status)
    {
        btnRun.IsEnabled = !busy; btnPreviewMap.IsEnabled = !busy; btnStop.IsEnabled = busy;
        pb.IsIndeterminate = busy;
        if (status != null) tbStatus.Text = status;
        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
    }

    private void Log(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        tbLog.AppendText(s.TrimEnd() + Environment.NewLine);
        tbLog.ScrollToEnd();
    }

    // ================================================================ отображение результатов

    private VariantResult SelectedVariant()
    {
        if (_result == null || cbVariant.SelectedIndex < 0 || cbVariant.SelectedIndex >= _result.Variants.Count) return null;
        return _result.Variants[cbVariant.SelectedIndex];
    }

    private void RefreshRegions()
    {
        if (_data == null) return;
        _regions = Reporting.Regions(_data, _mapParams ?? _p);
        cbRegion.ItemsSource = _regions.Select(r => r.name).ToList();
        cbRegion.SelectedIndex = _regions.Count > 0 ? 0 : -1;
    }

    private void CbVariant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var v = SelectedVariant();
        if (v == null) return;
        dgPads.ItemsSource = v.Pads.Select(pd => PadRow.From(pd, _result.Parameters)).ToList();
        dgWells.ItemsSource = v.Wells.Select(WellRow.From).ToList();
        RedrawPlan(v);
        Rebuild3D();
    }

    private void DgVariants_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _result == null || dgVariants.SelectedItem is not VariantRow row) return;
        int i = _result.Variants.IndexOf(row.Source);
        if (i >= 0 && cbVariant.SelectedIndex != i) cbVariant.SelectedIndex = i;
    }

    private void CbRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        FitRegion();
        Rebuild3D();
    }

    private void CbZ_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Rebuild3D();
    }

    private void SlZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || scalePlan == null) return;
        scalePlan.ScaleX = scalePlan.ScaleY = slZoom.Value;
        RedrawPlan(SelectedVariant());
    }

    private void SvPlan_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;   // Ctrl + колесо — масштаб
        slZoom.Value = Math.Clamp(slZoom.Value * (e.Delta > 0 ? 1.2 : 1 / 1.2), slZoom.Minimum, slZoom.Maximum);
        e.Handled = true;
    }

    private void RedrawPlan(VariantResult v)
    {
        if (_map == null || _data == null) return;
        PlanRenderer.Draw(cvPlan, _map, _data, v, slZoom.Value);
    }

    /// <summary>Масштаб и прокрутка плана по выбранному участку.</summary>
    private void FitRegion()
    {
        if (_map == null || cbRegion.SelectedIndex < 0 || cbRegion.SelectedIndex >= _regions.Count) return;
        var r = _regions[cbRegion.SelectedIndex];
        double wPx = (r.e1 - r.e0) / _map.CellSize, hPx = (r.n1 - r.n0) / _map.CellSize;
        double vw = Math.Max(200, svPlan.ActualWidth - 30), vh = Math.Max(200, svPlan.ActualHeight - 30);
        double z = Math.Clamp(Math.Min(vw / wPx, vh / hPx), slZoom.Minimum, slZoom.Maximum);
        slZoom.Value = z;
        var tl = PlanRenderer.ToPx(_map, r.n1, r.e0);
        svPlan.UpdateLayout();
        svPlan.ScrollToHorizontalOffset(Math.Max(0, tl.X * z));
        svPlan.ScrollToVerticalOffset(Math.Max(0, tl.Y * z));
    }

    private void Rebuild3D()
    {
        if (_map == null || _data == null || cbRegion.SelectedIndex < 0 || cbRegion.SelectedIndex >= _regions.Count) return;
        var r = _regions[cbRegion.SelectedIndex];
        double z = cbZ.SelectedIndex switch { 0 => 1, 2 => 4, _ => 2 };
        _scene.Build(_map, _mapBitmap, _data, SelectedVariant(), (r.e0, r.n0, r.e1, r.n1), z);
    }

    private void Grid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e) => GridHeaders.Apply(sender, e);

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
