# Пересчёт карты КИН (.bin, сетка в градусах WGS84) в растр ESRI ASCII в UTM (EPSG:326zz) — двойник KinMap.cs
import sys, struct, math, json, numpy as np
sys.path.insert(0, __import__('os').path.dirname(__file__))
from tm import inv
src, inp, out = sys.argv[1:4]
b = open(src, 'rb').read()
nrows, ncols = struct.unpack_from('<2i', b, 20)
lon0, lat0, lon1, lat1 = struct.unpack_from('<4d', b, 28)
nb = struct.unpack_from('<i', b, 96)[0]
a = np.frombuffer(b, '<f4', nb // 4, 100).reshape(nrows, ncols)   # строка 0 — юг
codes = {2.0: 1, 3.0: 2, 4.0: 3}
d = json.load(open(inp)); zone = d['mapDefinition']['projected_crs'] - 32600; lon_c = zone * 6 - 183
r = d['massiveDataVariants'][0]['trajBuildRequest']
E = [x for w in r for x in (w['T1'][1], w['T3'][1])]; N = [x for w in r for x in (w['T1'][0], w['T3'][0])]
mg = 1500 * 1.6 + 400 + 500; cell = 25.0
x0 = math.floor((min(E) - mg) / 1000) * 1000; y0 = math.floor((min(N) - mg) / 1000) * 1000
x1 = math.ceil((max(E) + mg) / 1000) * 1000; y1 = math.ceil((max(N) + mg) / 1000) * 1000
nc = int((x1 - x0) / cell); nr = int((y1 - y0) / cell)
g = np.full((nr, nc), 9, np.int16)
dlon = (lon1 - lon0) / ncols; dlat = (lat1 - lat0) / nrows
for rr in range(nr):
    n = y1 - (rr + 0.5) * cell
    for cc in range(nc):
        e = x0 + (cc + 0.5) * cell
        lat, lon = inv(e, n, lon_c)
        i = int((lat - lat0) / dlat); j = int((lon - lon0) / dlon)
        if 0 <= i < nrows and 0 <= j < ncols: g[rr, cc] = codes.get(float(a[i, j]), 9)
with open(out, 'w') as f:
    f.write(f"ncols {nc}\nnrows {nr}\nxllcorner {x0}\nyllcorner {y0}\ncellsize {cell}\nNODATA_value -9999\n")
    for row in g: f.write(' '.join(map(str, row)) + '\n')
u, c = np.unique(g, return_counts=True); print(nc, nr, dict(zip(u.tolist(), (c / c.sum()).round(3).tolist())))
