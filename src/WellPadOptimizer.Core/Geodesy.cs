namespace WellPadOptimizer;

/// <summary>
/// Поперечная проекция Меркатора (ряды Крюгера до n^4, точность лучше 1 мм в пределах зоны).
/// Используется для перевода координат UTM (EPSG:326zz / 327zz) в широту/долготу WGS84 и обратно.
/// </summary>
public sealed class TransverseMercator
{
    private readonly double _k0, _fe, _fn, _lon0, _a, _n, _A;
    private readonly double[] _al = new double[4], _be = new double[4], _de = new double[4];

    public TransverseMercator(double lon0Deg, double k0, double falseEasting, double falseNorthing,
                              double semiMajor = 6378137.0, double invFlattening = 298.257223563)
    {
        _lon0 = lon0Deg; _k0 = k0; _fe = falseEasting; _fn = falseNorthing; _a = semiMajor;
        double f = 1 / invFlattening, n = f / (2 - f); _n = n;
        double n2 = n * n, n3 = n2 * n, n4 = n3 * n;
        _A = _a / (1 + n) * (1 + n2 / 4 + n4 / 64);
        _al[0] = n / 2 - 2 * n2 / 3 + 5 * n3 / 16 + 41 * n4 / 180;
        _al[1] = 13 * n2 / 48 - 3 * n3 / 5 + 557 * n4 / 1440;
        _al[2] = 61 * n3 / 240 - 103 * n4 / 140;
        _al[3] = 49561 * n4 / 161280;
        _be[0] = n / 2 - 2 * n2 / 3 + 37 * n3 / 96 - n4 / 360;
        _be[1] = n2 / 48 + n3 / 15 - 437 * n4 / 1440;
        _be[2] = 17 * n3 / 480 - 37 * n4 / 840;
        _be[3] = 4397 * n4 / 161280;
        _de[0] = 2 * n - 2 * n2 / 3 - 2 * n3 + 116 * n4 / 45;
        _de[1] = 7 * n2 / 3 - 8 * n3 / 5 - 227 * n4 / 45;
        _de[2] = 56 * n3 / 15 - 136 * n4 / 35;
        _de[3] = 4279 * n4 / 630;
    }

    /// <summary>UTM по коду EPSG (32601–32660 — север, 32701–32760 — юг).</summary>
    public static TransverseMercator FromEpsg(int epsg)
    {
        int zone; bool south;
        if (epsg >= 32601 && epsg <= 32660) { zone = epsg - 32600; south = false; }
        else if (epsg >= 32701 && epsg <= 32760) { zone = epsg - 32700; south = true; }
        else throw new NotSupportedException($"Проекция EPSG:{epsg} не поддерживается: ожидается WGS 84 / UTM (EPSG:326zz или 327zz). Задайте ProjectedCrs в настройках.");
        return new TransverseMercator(zone * 6 - 183, 0.9996, 500000, south ? 10000000 : 0);
    }

    /// <summary>(широта, долгота) → (восток E, север N).</summary>
    public (double e, double n) Forward(double latDeg, double lonDeg)
    {
        double phi = latDeg * Math.PI / 180, lam = (lonDeg - _lon0) * Math.PI / 180;
        double c = 2 * Math.Sqrt(_n) / (1 + _n);
        double t = Math.Sinh(Atanh(Math.Sin(phi)) - c * Atanh(c * Math.Sin(phi)));
        double xi = Math.Atan2(t, Math.Cos(lam));
        double eta = Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        double E = eta, N = xi;
        for (int j = 0; j < 4; j++)
        {
            int k = 2 * (j + 1);
            E += _al[j] * Math.Cos(k * xi) * Math.Sinh(k * eta);
            N += _al[j] * Math.Sin(k * xi) * Math.Cosh(k * eta);
        }
        return (_fe + _k0 * _A * E, _fn + _k0 * _A * N);
    }

    /// <summary>(восток E, север N) → (широта, долгота).</summary>
    public (double lat, double lon) Inverse(double e, double n)
    {
        double xi = (n - _fn) / (_k0 * _A), eta = (e - _fe) / (_k0 * _A);
        double xp = xi, ep = eta;
        for (int j = 0; j < 4; j++)
        {
            int k = 2 * (j + 1);
            xp -= _be[j] * Math.Sin(k * xi) * Math.Cosh(k * eta);
            ep -= _be[j] * Math.Cos(k * xi) * Math.Sinh(k * eta);
        }
        double chi = Math.Asin(Math.Sin(xp) / Math.Cosh(ep));
        double phi = chi;
        for (int j = 0; j < 4; j++) phi += _de[j] * Math.Sin(2 * (j + 1) * chi);
        double lam = Math.Atan2(Math.Sinh(ep), Math.Cos(xp));
        return (phi * 180 / Math.PI, _lon0 + lam * 180 / Math.PI);
    }

    private static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
}

/// <summary>Пересчёт между эллипсоидами (геодезические ↔ геоцентрические координаты, преобразование Гельмерта).</summary>
public static class Datum
{
    private const double As = Math.PI / 180 / 3600;

    private static (double x, double y, double z) ToEcef(double latDeg, double lonDeg, double h, double a, double invF)
    {
        double f = 1 / invF, e2 = f * (2 - f), la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        double n = a / Math.Sqrt(1 - e2 * Math.Sin(la) * Math.Sin(la));
        return ((n + h) * Math.Cos(la) * Math.Cos(lo), (n + h) * Math.Cos(la) * Math.Sin(lo), (n * (1 - e2) + h) * Math.Sin(la));
    }

    private static (double lat, double lon) FromEcef(double x, double y, double z, double a, double invF)
    {
        double f = 1 / invF, e2 = f * (2 - f), lon = Math.Atan2(y, x), p = Math.Sqrt(x * x + y * y);
        double lat = Math.Atan2(z, p * (1 - e2));
        for (int i = 0; i < 10; i++)
        {
            double n = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
            double h = p / Math.Cos(lat) - n;
            lat = Math.Atan2(z, p * (1 - e2 * n / (n + h)));
        }
        return (lat * 180 / Math.PI, lon * 180 / Math.PI);
    }

    /// <summary>СК-42 (эллипсоид Красовского) → WGS 84, параметры ГОСТ 32453-2017.</summary>
    public static (double lat, double lon) Sk42ToWgs84(double latDeg, double lonDeg)
    {
        const double dx = 23.57, dy = -140.95, dz = -79.8, wx = 0.0 * As, wy = -0.35 * As, wz = -0.79 * As, m = -0.22e-6;
        var (x, y, z) = ToEcef(latDeg, lonDeg, 0, 6378245.0, 298.3);
        double x2 = (1 + m) * (x + wz * y - wy * z) + dx;
        double y2 = (1 + m) * (-wz * x + y + wx * z) + dy;
        double z2 = (1 + m) * (wy * x - wx * y + z) + dz;
        return FromEcef(x2, y2, z2, 6378137.0, 298.257223563);
    }

    /// <summary>Проекция Гаусса–Крюгера СК-42 для зоны (центральный меридиан 6·зона − 3, ложный восток 500 км).</summary>
    public static TransverseMercator GaussKruger(int zone) => new(zone * 6 - 3, 1.0, 500000, 0, 6378245.0, 298.3);
}
