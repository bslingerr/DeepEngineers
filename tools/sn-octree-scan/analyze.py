"""Terrain statistics for the world-mapping probe (docs/recon/probe-world-mapping.md).

Reads (read-only) Subnautica's biomeMap.bin / biomes.csv and the 4 m column maps written by the C# scan
in this folder (dotnet run -c Release -- <...\\Build18\\CompiledOctreesCache> <outdir>).
Usage: python -I analyze.py <...\\SNUnmanagedData\\Build18> <outdir>
Nothing derived from game files belongs in this repository: keep <outdir> in a scratch folder.
"""
import numpy as np, sys
B=sys.argv[1].rstrip("/\\")+"/"  #...\Subnautica_Data\StreamingAssets\SNUnmanagedData\Build18
OUT=sys.argv[2].rstrip("/\\")+"/"  # output folder of the C# scan (keep it outside the repo)
names=[l.strip() for l in open(B+"biomes.csv").read().splitlines()[1:] if l.strip()]
bm=np.frombuffer(open(B+"biomeMap.bin","rb").read()[:-2],dtype=np.uint8).reshape(1024,1024)  # [z,x]
top=np.fromfile(OUT+"top.i16",dtype=np.int16).reshape(1024,1024).astype(np.int32)
lf=np.fromfile(OUT+"lowfloor.i16",dtype=np.int16).reshape(1024,1024).astype(np.int32)
ls=np.fromfile(OUT+"lowsolid.i16",dtype=np.int16).reshape(1024,1024).astype(np.int32)
NONE=-32768
has=top!=NONE
print("columns with any solid: %.1f%%"%(100*has.mean()))
print("global top max", top[has].max(), "min top", top[has].min())
print("global lowest solid", ls[ls!=32767].min(), "lowest floor", lf[lf!=32767].min())
xs=np.arange(1024)*4-2048+2; X,Z=np.meshgrid(xs,xs)
i=np.unravel_index(np.argmax(np.where(has,top,-99999)),top.shape); print("highest point at x=%d z=%d y=%d biome=%s"%(X[i],Z[i],top[i],names[bm[i]]))
i=np.unravel_index(np.argmin(np.where(lf!=32767,lf,99999)),lf.shape); print("deepest floor at x=%d z=%d y=%d biome=%s"%(X[i],Z[i],lf[i],names[bm[i]]))
print("land above sea (top>0): %.3f km2"%((top>0).sum()*16/1e6))
# islands: connected regions with top>0
from collections import deque
lab=np.zeros_like(top); n=0; comps=[]
above=top>0
for z in range(1024):
    for x in range(1024):
        if above[z,x] and lab[z,x]==0:
            n+=1; q=deque([(z,x)]); lab[z,x]=n; cells=[]
            while q:
                a,b=q.popleft(); cells.append((a,b))
                for da,db in ((1,0),(-1,0),(0,1),(0,-1)):
                    c,d=a+da,b+db
                    if 0<=c<1024 and 0<=d<1024 and above[c,d] and lab[c,d]==0: lab[c,d]=n; q.append((c,d))
            cz=np.array([c[0] for c in cells]); cx=np.array([c[1] for c in cells])
            hmax=top[cz,cx].max()
            comps.append((len(cells)*16, cx.mean()*4-2048, cz.mean()*4-2048, hmax, cx.min()*4-2048,cx.max()*4-2048,cz.min()*4-2048,cz.max()*4-2048))
comps.sort(reverse=True)
for c in comps[:8]: print("island area %d m2 centre (%.0f,%.0f) top %d bbox x[%d,%d] z[%d,%d]"%c)
print("per biome: top-surface y percentiles (p1,p50,p99), deepest floor, lowest solid")
for k in range(len(names)):
    m=(bm==k)&has
    if m.sum()==0:
        if (bm==k).sum(): print(names[k],"no solid in", (bm==k).sum());
        continue
    t=top[m]; f=lf[m&(lf!=32767)]; s=ls[m&(ls!=32767)]
    print("%-18s cells %6d  top p1 %5d p50 %5d p99 %5d max %5d | floor min %5d | lowest solid %5d"%(names[k],m.sum(),np.percentile(t,1),np.percentile(t,50),np.percentile(t,99),t.max(),f.min() if f.size else 0,s.min()))
vm=(bm==names.index('void'))
print("void cells", vm.sum(), "void cells with no solid at all: %.1f%%"%(100*(vm&~has).sum()/vm.sum()))
# radial profile of seabed from the crater: median top by ring
r=np.hypot(X,Z)
for r0 in range(0,2900,200):
    m=(r>=r0)&(r<r0+200)
    mm=m&has
    print("ring %4d-%4d: solid %.0f%%  top p10 %s p50 %s p90 %s  void %.0f%%"%(r0,r0+200,100*mm.sum()/max(1,m.sum()), np.percentile(top[mm],10) if mm.sum() else '-', np.percentile(top[mm],50) if mm.sum() else '-', np.percentile(top[mm],90) if mm.sum() else '-', 100*(m&vm).sum()/max(1,m.sum())))
# floor histogram
h=np.loadtxt(OUT+"floorhist.csv",delimiter=",",skiprows=1)
tot=h[:,1].sum()
for lo in range(-2000,200,100):
    m=(h[:,0]>=lo)&(h[:,0]<lo+100)
    print("floor voxels y in [%d,%d): %.2f%%"%(lo,lo+100,100*h[m,1].sum()/tot))
for p in [(0,0),(-832,-991),(300,1100),(-390,76),(1100,-100),(-2000,0),(1900,1900)]:
    zi=(p[1]+2048)//4; xi=(p[0]+2048)//4
    print(p, names[bm[zi,xi]], "top",top[zi,xi],"lowfloor",lf[zi,xi],"lowsolid",ls[zi,xi])
