using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace WellPadOptimizer.Gui;

/// <summary>Цвета классов местности и кустов (те же, что в report.html).</summary>
public static class Palette
{
    private static readonly Color[] ClassColors =
    {
        Color.FromRgb(0xE9, 0xDF, 0xC4), // 1 суходол
        Color.FromRgb(0xA9, 0xB9, 0x8A), // 2 болото
        Color.FromRgb(0x8F, 0xB7, 0xDC), // 3 водоём
        Color.FromRgb(0x5F, 0x8F, 0x5A), // 4 кедровник
        Color.FromRgb(0x9C, 0xC2, 0x8F), // 5 лес
        Color.FromRgb(0xC9, 0xB3, 0xD9),
        Color.FromRgb(0xD9, 0xC3, 0x8A),
    };
    public static readonly Color Forbidden = Color.FromRgb(0xD9, 0x8A, 0x84);
    public static readonly Color[] Series =
    {
        Color.FromRgb(0x2A, 0x78, 0xD6), Color.FromRgb(0xEB, 0x68, 0x34), Color.FromRgb(0x1B, 0xAF, 0x7A), Color.FromRgb(0xED, 0xA1, 0x00),
        Color.FromRgb(0xE8, 0x7B, 0xA4), Color.FromRgb(0x00, 0x83, 0x00), Color.FromRgb(0x62, 0x50, 0xD6), Color.FromRgb(0xE3, 0x49, 0x48),
    };

    public static Color ForClass(Parameters p, int code)
    {
        if (p.IsForbidden(code)) return Forbidden;
        int i = p.Terrain.FindIndex(t => t.Code == code);
        if (i < 0) i = Math.Max(0, p.Terrain.FindIndex(t => t.Code == p.DefaultTerrainCode));
        return ClassColors[Math.Min(i, ClassColors.Length - 1)];
    }
}

/// <summary>План: растр карты желательности и наложение кустов, траекторий и целей.</summary>
public static class PlanRenderer
{
    public static WriteableBitmap MapBitmap(TerrainMap map, Parameters p)
    {
        int w = map.NCols, h = map.NRows;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var px = new int[w * h];
        var cache = new Dictionary<int, int>();
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int code = map[r, c];
                if (!cache.TryGetValue(code, out int argb))
                {
                    var col = Palette.ForClass(p, code);
                    argb = (255 << 24) | (col.R << 16) | (col.G << 8) | col.B;
                    cache[code] = argb;
                }
                px[r * w + c] = argb;
            }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    public static Point ToPx(TerrainMap map, double n, double e) =>
        new Point((e - map.XLL) / map.CellSize, (map.YMax - n) / map.CellSize);

    /// <summary>Нарисовать вариант поверх карты (Canvas в координатах ячеек растра).</summary>
    public static void Draw(Canvas canvas, TerrainMap map, InputData data, VariantResult v, double zoom)
    {
        canvas.Children.Clear();
        canvas.Width = map.NCols; canvas.Height = map.NRows;
        double lw = Math.Max(0.4, 1.6 / Math.Max(zoom, 0.05));
        var padColor = new Dictionary<string, Color>();
        // существующие скважины — тонкие серые линии
        if (data?.Existing != null)
        {
            var exBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x6E));
            foreach (var ew in data.Existing.Wells)
            {
                var pl = new System.Windows.Shapes.Polyline
                {
                    Stroke = exBrush, StrokeThickness = lw * 0.6, StrokeDashArray = new DoubleCollection { 2, 2 },
                    ToolTip = $"{ew.Name} (существующая), MD {ew.MaxMd:F0} м",
                };
                for (int i = 0; i < ew.Count; i += 3) pl.Points.Add(ToPx(map, ew.Pts[i].N, ew.Pts[i].E));
                pl.Points.Add(ToPx(map, ew.Pts[^1].N, ew.Pts[^1].E));
                canvas.Children.Add(pl);
            }
        }
        if (v != null)
        {
            int k = 0;
            foreach (var pd in v.Pads) padColor[pd.Name] = Palette.Series[k++ % Palette.Series.Length];

            foreach (var w in v.Wells)
            {
                if (w.Survey == null || w.Survey.Count < 2) continue;
                var pl = new System.Windows.Shapes.Polyline
                {
                    Stroke = new SolidColorBrush(padColor.TryGetValue(w.Pad, out var c) ? c : Colors.Gray),
                    StrokeThickness = lw,
                    ToolTip = $"{w.Well} · {w.Pad}; отход {w.Offset:F0} м; MD до T1 {w.MdT1:F0} м" + (w.Violations.Count > 0 ? "; нарушения: " + string.Join(", ", w.Violations) : ""),
                };
                foreach (var s in w.Survey.Where((_, i) => i % 3 == 0 || i == w.Survey.Count - 1)) pl.Points.Add(ToPx(map, s.N, s.E));
                canvas.Children.Add(pl);
            }
            foreach (var pd in v.Pads)
            {
                double a = pd.Angle * Math.PI / 180, uN = Math.Cos(a), uE = Math.Sin(a), vN = -Math.Sin(a), vE = Math.Cos(a);
                double hl = pd.Length / 2, hw = pd.Width / 2;
                var poly = new System.Windows.Shapes.Polygon
                {
                    Fill = new SolidColorBrush(padColor[pd.Name]),
                    Stroke = Brushes.White, StrokeThickness = lw,
                    ToolTip = $"{pd.Name}: {pd.WellCount} скв.; отсыпка {pd.SandVolume / 1000:F1} тыс. м³" + (pd.Forbidden ? "; в запретной зоне" : ""),
                };
                foreach (var (s, ww) in new[] { (hl, hw), (hl, -hw), (-hl, -hw), (-hl, hw) })
                    poly.Points.Add(ToPx(map, pd.N + s * uN + ww * vN, pd.E + s * uE + ww * vE));
                canvas.Children.Add(poly);
                var lbl = new TextBlock { Text = pd.Name, FontWeight = FontWeights.SemiBold, FontSize = Math.Max(4, 12 / Math.Max(zoom, 0.05)), Foreground = Brushes.Black };
                var pt = ToPx(map, pd.N, pd.E);
                Canvas.SetLeft(lbl, pt.X + 6 / Math.Max(zoom, 0.05)); Canvas.SetTop(lbl, pt.Y - 8 / Math.Max(zoom, 0.05));
                canvas.Children.Add(lbl);
            }
        }
        foreach (var t in data.Wells)
        {
            var a = ToPx(map, t.T1.N, t.T1.E); var b = ToPx(map, t.T3.N, t.T3.E);
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Brushes.Black, StrokeThickness = lw * 2,
                ToolTip = $"{t.Name}: T1–T3, ГС {t.HorizontalLength:F0} м, TVD {t.T1.Z:F0} м",
            });
            var dot = new System.Windows.Shapes.Ellipse { Width = lw * 4, Height = lw * 4, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = lw * 0.8 };
            Canvas.SetLeft(dot, a.X - lw * 2); Canvas.SetTop(dot, a.Y - lw * 2);
            canvas.Children.Add(dot);
        }
    }
}

/// <summary>Трёхмерная сцена (WPF Viewport3D): траектории, кусты, цели и карта на поверхности; вращение мышью.</summary>
public sealed class Scene3D
{
    private readonly Viewport3D _vp;
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 45, UpDirection = new Vector3D(0, 0, 1) };
    private double _yaw = -50, _pitch = 30, _dist = 10000;
    private Point3D _target = new(0, 0, 0);
    private Point? _drag;

    public Scene3D(Viewport3D vp)
    {
        _vp = vp;
        _vp.Camera = _cam;
        _vp.MouseLeftButtonDown += (s, e) => { _drag = e.GetPosition(_vp); _vp.CaptureMouse(); };
        _vp.MouseLeftButtonUp += (s, e) => { _drag = null; _vp.ReleaseMouseCapture(); };
        _vp.MouseMove += (s, e) =>
        {
            if (_drag == null) return;
            var p = e.GetPosition(_vp);
            _yaw -= (p.X - _drag.Value.X) * 0.4;
            _pitch = Math.Clamp(_pitch + (p.Y - _drag.Value.Y) * 0.3, -5, 89);
            _drag = p; UpdateCamera();
        };
        _vp.MouseWheel += (s, e) => { _dist *= e.Delta > 0 ? 0.85 : 1.18; UpdateCamera(); };
    }

    private void UpdateCamera()
    {
        double y = _yaw * Math.PI / 180, p = _pitch * Math.PI / 180;
        var dir = new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p));
        _cam.Position = _target + dir * _dist;
        _cam.LookDirection = -dir * _dist;
        _cam.NearPlaneDistance = Math.Max(1, _dist / 1000);
        _cam.FarPlaneDistance = _dist * 20;
    }

    /// <summary>Построить сцену для варианта в границах участка (e0, n0, e1, n1).</summary>
    public void Build(TerrainMap map, BitmapSource mapBitmap, InputData data, VariantResult v,
                      (double e0, double n0, double e1, double n1) region, double zScale)
    {
        double cx = (region.e0 + region.e1) / 2, cy = (region.n0 + region.n1) / 2;
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.3, 0.4, -1)));
        Point3D P(double n, double e, double tvd) => new(e - cx, n - cy, -tvd * zScale);

        // поверхность с картой
        if (mapBitmap != null)
        {
            var mesh = new MeshGeometry3D();
            var corners = new[] { (region.n0, region.e0), (region.n0, region.e1), (region.n1, region.e1), (region.n1, region.e0) };
            foreach (var (n, e) in corners)
            {
                mesh.Positions.Add(P(n, e, 0));
                mesh.TextureCoordinates.Add(new Point((e - map.XLL) / (map.XMax - map.XLL), (map.YMax - n) / (map.YMax - map.YLL)));
            }
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(i);
            var brush = new ImageBrush(mapBitmap) { Opacity = 0.9 };
            var mat = new DiffuseMaterial(brush);
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        bool In(double n, double e) => e >= region.e0 && e <= region.e1 && n >= region.n0 && n <= region.n1;
        double r = Math.Max(region.e1 - region.e0, region.n1 - region.n0) / 900;
        if (v != null)
        {
            var colors = new Dictionary<string, Color>();
            int k = 0;
            foreach (var pd in v.Pads) colors[pd.Name] = Palette.Series[k++ % Palette.Series.Length];
            foreach (var w in v.Wells.Where(w => In(w.WhN, w.WhE) && w.Survey != null && w.Survey.Count > 1))
            {
                var pts = w.Survey.Where((_, i) => i % 2 == 0 || i == w.Survey.Count - 1).Select(s => P(s.N, s.E, s.TVD)).ToList();
                group.Children.Add(Tube(pts, r, colors.TryGetValue(w.Pad, out var c) ? c : Colors.Gray));
            }
            foreach (var pd in v.Pads.Where(q => In(q.N, q.E)))
                group.Children.Add(Box(P(pd.N, pd.E, 0), pd.Width / 2, pd.Width / 2, r * 6, colors[pd.Name]));
        }
        foreach (var t in data.Wells.Where(t => In(t.T1.N, t.T1.E)))
            group.Children.Add(Tube(new List<Point3D> { P(t.T1.N, t.T1.E, t.T1.Z), P(t.T3.N, t.T3.E, t.T3.Z) }, r * 2.2, Color.FromRgb(30, 30, 30)));

        _vp.Children.Clear();
        _vp.Children.Add(new ModelVisual3D { Content = group });
        _target = new Point3D(0, 0, -1300 * zScale);
        _dist = Math.Max(region.e1 - region.e0, region.n1 - region.n0) * 1.6;
        UpdateCamera();
    }

    /// <summary>Трубка квадратного сечения вдоль ломаной.</summary>
    private static GeometryModel3D Tube(List<Point3D> pts, double r, Color color)
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            var d = b - a; if (d.Length < 1e-6) continue;
            d.Normalize();
            var up = Math.Abs(d.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
            var u = Vector3D.CrossProduct(d, up); u.Normalize();
            var w = Vector3D.CrossProduct(d, u); w.Normalize();
            var off = new[] { u * r, w * r, -u * r, -w * r };
            int baseIdx = mesh.Positions.Count;
            foreach (var o in off) mesh.Positions.Add(a + o);
            foreach (var o in off) mesh.Positions.Add(b + o);
            for (int s = 0; s < 4; s++)
            {
                int s2 = (s + 1) % 4;
                int a0 = baseIdx + s, a1 = baseIdx + s2, b0 = baseIdx + 4 + s, b1 = baseIdx + 4 + s2;
                mesh.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(b0); mesh.TriangleIndices.Add(b1);
                mesh.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(b1); mesh.TriangleIndices.Add(a1);
            }
        }
        var mat = new DiffuseMaterial(new SolidColorBrush(color));
        return new GeometryModel3D(mesh, mat) { BackMaterial = mat };
    }

    private static GeometryModel3D Box(Point3D c, double hx, double hy, double h, Color color)
    {
        var mesh = new MeshGeometry3D();
        double[] xs = { -hx, hx }, ys = { -hy, hy }, zs = { 0, h };
        foreach (var z in zs) foreach (var y in ys) foreach (var x in xs) mesh.Positions.Add(new Point3D(c.X + x, c.Y + y, c.Z + z));
        int[] idx = { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 };
        foreach (var i in idx) mesh.TriangleIndices.Add(i);
        var mat = new DiffuseMaterial(new SolidColorBrush(color));
        return new GeometryModel3D(mesh, mat) { BackMaterial = mat };
    }
}
