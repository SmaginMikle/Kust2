# Поперечная проекция Меркатора (формулы Крюгера, серия до n^4) — прямое и обратное преобразование.
import math
WGS84 = (6378137.0, 1 / 298.257223563)
KRASS = (6378245.0, 1 / 298.3)
def _consts(ell):
    a, f = ell; n = f / (2 - f)
    A = a / (1 + n) * (1 + n**2 / 4 + n**4 / 64)
    al = [n/2 - 2*n**2/3 + 5*n**3/16 + 41*n**4/180, 13*n**2/48 - 3*n**3/5 + 557*n**4/1440, 61*n**3/240 - 103*n**4/140, 49561*n**4/161280]
    be = [n/2 - 2*n**2/3 + 37*n**3/96 - n**4/360, n**2/48 + n**3/15 - 437*n**4/1440, 17*n**3/480 - 37*n**4/840, 4397*n**4/161280]
    de = [2*n - 2*n**2/3 - 2*n**3 + 116*n**4/45, 7*n**2/3 - 8*n**3/5 - 227*n**4/45, 56*n**3/15 - 136*n**4/35, 4279*n**4/630]
    return n, A, al, be, de
def fwd(lat, lon, lon0, k0=0.9996, fe=500000.0, fn=0.0, ell=WGS84):
    n, A, al, be, de = _consts(ell)
    phi = math.radians(lat); lam = math.radians(lon - lon0)
    t = math.sinh(math.atanh(math.sin(phi)) - 2*math.sqrt(n)/(1+n)*math.atanh(2*math.sqrt(n)/(1+n)*math.sin(phi)))
    xi = math.atan2(t, math.cos(lam)); eta = math.atanh(math.sin(lam) / math.sqrt(1 + t*t))
    E = eta + sum(al[j]*math.cos(2*(j+1)*xi)*math.sinh(2*(j+1)*eta) for j in range(4))
    N = xi + sum(al[j]*math.sin(2*(j+1)*xi)*math.cosh(2*(j+1)*eta) for j in range(4))
    return fe + k0*A*E, fn + k0*A*N
def inv(E, N, lon0, k0=0.9996, fe=500000.0, fn=0.0, ell=WGS84):
    n, A, al, be, de = _consts(ell)
    xi = (N - fn) / (k0*A); eta = (E - fe) / (k0*A)
    xp = xi - sum(be[j]*math.sin(2*(j+1)*xi)*math.cosh(2*(j+1)*eta) for j in range(4))
    ep = eta - sum(be[j]*math.cos(2*(j+1)*xi)*math.sinh(2*(j+1)*eta) for j in range(4))
    chi = math.asin(math.sin(xp)/math.cosh(ep))
    phi = chi + sum(de[j]*math.sin(2*(j+1)*chi) for j in range(4))
    lam = math.atan2(math.sinh(ep), math.cos(xp))
    return math.degrees(phi), lon0 + math.degrees(lam)
