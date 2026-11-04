# Python-двойник: расчёт по проекту кустования КИН (props / wrappers / assets) с однорядными и Z-кустами.
import sys, os, json, math, base64, time, pickle, struct
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
import solver as S
from solver import P
from tm import fwd, inv

src, outdir = sys.argv[1], sys.argv[2]
os.makedirs(outdir, exist_ok=True)
d = json.load(open(src))
pr = d['props']
# ---- параметры проекта
P.MinWells = pr['MinWellsPerPad']; P.StartMaxWells = pr['InitialMaximumNumberWellsCluster']; P.EndMaxWells = pr['FinalMaximumNumberWellsCluster']
P.VariantStep = max(1, pr['SkippingSeedWells']); P.MaxOffset = float(pr['MaxWaste']); P.MaxMD = float(pr['MaxTrajLength'])
P.Dls1 = pr['RateFirstSet']; P.Dls2 = pr['RateSecondSet']; P.UseAzTurn = bool(pr['IsMaximumZenithAngle']); P.MaxAzTurn = float(pr['MaximumZenithAngle'])
P.VertLen = pr['MaximumZenithAngleStabilizationArea']; P.CostNNS = pr['CostPerMeterDrillingNNS']; P.CostGS = pr['CostPerMeterDrillingGS']
P.PadCost = [(12, pr['CostGroup1']), (19, pr['CostGroup2']), (24, pr['CostGroup3'])]; P.SandCostPer1000 = pr['SandCost']
P.MaxHookLoad = pr['MaxHookLoad']; P.MaxTorque = pr['MaxTorque']
# ---- цели
raw = []
for w in pr['Wells']:
    r = w['Rating'][0]; pts = (r.get('Hor') or {}).get('Trajectory', {}).get('Points') or [w['WGS']]
    raw.append((str(w['Uid']), w['Name'], pts[0]['x'], pts[0]['y'], pts[-1]['x'], pts[-1]['y'], r['WellBottom']['Depth']))
lonc = np.mean([x[2] for x in raw]); zone = int(math.floor((lonc + 180) / 6)) + 1; lon0 = zone * 6 - 183
req = []
for uid, nm, lo1, la1, lo3, la3, tvd in raw:
    e1, n1 = fwd(la1, lo1, lon0); e3, n3 = fwd(la3, lo3, lon0)
    req.append(dict(uid=uid, name=nm, T1=[n1, e1, tvd], T3=[n3, e3, tvd]))
uid2i = {r['uid']: i for i, r in enumerate(req)}
variants = []
for vi, wr in enumerate(d['wrappers']):
    rows = []
    for pi, o in enumerate(wr['Owner']):
        for x in o['Wells']:
            i = uid2i.get(str(x['Uid']))
            if i is None: continue
            rows.append(dict(req[i], pad_id=pi, wh_north=x['WellHeadUTM']['y'], wh_east=x['WellHeadUTM']['x']))
    if len(rows) == len(req): variants.append((vi, rows))
inp = os.path.join(outdir, 'project_input.json')
json.dump(dict(mapDefinition=dict(projected_crs=32600 + zone),
               massiveDataVariants=[dict(trajBuildRequest=rows) for _, rows in variants], trajRealWells=[]), open(inp, 'w'))
# ---- карта из проекта
name = os.path.basename(pr['TopologyMap'].replace('\\', '/'))
binf = os.path.join(outdir, 'map.bin'); open(binf, 'wb').write(base64.b64decode(d['assets']['project_assets/' + name]))
del d
os.system(f"{sys.executable} -I {os.path.join(os.path.dirname(__file__), 'kin2asc.py')} {binf} {inp} {os.path.join(outdir, 'map_utm.asc')}")
print('zone', zone, 'wells', len(req), 'variants', len(variants), 'P', P.MinWells, P.StartMaxWells, P.EndMaxWells, P.UseAzTurn, P.MaxOffset)

t0 = time.time()
terr = S.Terrain(os.path.join(outdir, 'map_utm.asc')); prob = S.Problem(inp, terr)
for k, w in enumerate(prob.wells): w['name'] = req[k]['name']; w['uid'] = req[k]['uid']; w['rev'] = True
prob.build_sites()
results, infeasible = [], []
sel = None
for (vi, rows) in variants:
    groups = {}; wh = {}
    for i, rr in enumerate(rows):
        idx = uid2i[rr['uid']]; groups.setdefault(rr['pad_id'], []).append(idx); wh[idx] = (rr['wh_north'], rr['wh_east'])
    pads_in = [dict(center=tuple(np.mean([wh[i] for i in g], 0)), wells=g) for _, g in sorted(groups.items())]
    res = S.evaluate(prob, pads_in, f"Вариант КИН {vi + 1}", 'input', fixed_wh=wh); res['scenario'] = 'Исходные данные'
    results.append(res); print(res['name'], round(res['total_cost'] / 1000, 1), res['violations'])
base_sc = 'Разворот ≤%g°' % P.MaxAzTurn if P.UseAzTurn else 'Без ограничения разворота'
for layout, tag in [('Single', ''), ('Z', f'; Z-кусты (2 НДС) при {P.ZMinWells} скв. и более')]:
    P.PadLayout = layout
    sand, _ = S.site_sand_table(terr, prob.sites, P.EndMaxWells)
    prob.build_matrix()
    sc = base_sc + tag; prev = None; best_prev = None
    for K in range(max(P.StartMaxWells, P.MinWells), P.EndMaxWells + 1, P.VariantStep):
        name = f"{sc}; {P.MinWells}–{K} скв./куст"
        pads, cost = S.optimize(prob, sand, K)
        if best_prev is not None and (pads is None or best_prev[1] < cost - 1e-6): pads, cost = [dict(s=p['s'], w=list(p['w'])) for p in best_prev[0]], best_prev[1]
        if pads is not None: best_prev = (pads, cost)
        if pads is None: infeasible.append(dict(name=name, scenario=sc, K=K, reason='нет разбиения')); continue
        sig = sorted((int(p['s']), tuple(sorted(p['w']))) for p in pads)
        if prev is not None and sig == prev[0]:
            res = dict(prev[1]); res['name'] = name; res['K'] = K; res['same_as'] = prev[1]['name']; results.append(res); continue
        pads_in = [dict(center=tuple(prob.sites[p['s']]), wells=sorted(p['w'])) for p in pads]
        res = S.evaluate(prob, pads_in, name, 'opt', K=K); res['scenario'] = sc; res['same_as'] = None; res['opt_estimate'] = cost
        if prev is not None and (res['violations'], res['total_cost']) > (prev[1]['violations'], prev[1]['total_cost'] + 1e-6):
            al = dict(prev[1]); al['name'] = name; al['K'] = K; al['same_as'] = prev[1]['name']; results.append(al); continue
        results.append(res); prev = (sig, res)
        print(name, [(p['n'], 'Z' if p['z'] else '') for p in res['pads']], round(res['total_cost'] / 1000, 1), 'viol', res['violations'], round(time.time() - t0))
    lo, hi = S.pad_count_range(prob, P.EndMaxWells)
    for npd in range(lo, hi + 1):
        name = f"{sc}; {npd} кустов по {P.MinWells}–{P.EndMaxWells} скв."
        pads, cost = S.optimize(prob, sand, P.EndMaxWells, n_pads=npd)
        if pads is None: infeasible.append(dict(name=name, scenario=sc, K=P.EndMaxWells, reason='нет разбиения на такое число кустов')); continue
        pads_in = [dict(center=tuple(prob.sites[p['s']]), wells=sorted(p['w'])) for p in pads]
        res = S.evaluate(prob, pads_in, name, 'opt', K=P.EndMaxWells); res['scenario'] = sc + ' (число кустов)'; res['same_as'] = None; res['opt_estimate'] = cost
        results.append(res)
        print(name, [(p['n'], 'Z' if p['z'] else '') for p in res['pads']], round(res['total_cost'] / 1000, 1), 'viol', res['violations'], round(time.time() - t0))
P.PadLayout = 'Single'
json.dump([[q['name'], q['uid']] for q in req], open(os.path.join(outdir, 'names.json'), 'w'), ensure_ascii=False)
pickle.dump((results, infeasible), open(os.path.join(outdir, 'results.pkl'), 'wb'))
