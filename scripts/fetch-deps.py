import urllib.request, json, pathlib, time, zipfile, xml.etree.ElementTree as ET, re, concurrent.futures
ROOT=pathlib.Path(__file__).resolve().parents[1]
FEED=ROOT/'.tools/feed'; FEED.mkdir(parents=True,exist_ok=True)
def get(url):
    error=None
    for i in range(6):
        try:
            with urllib.request.urlopen(url,timeout=45) as r:return r.read()
        except Exception as e:error=e;time.sleep(1+i)
    raise error
def version(name,want):
    vs=json.loads(get(f'https://api.nuget.org/v3-flatcontainer/{name}/index.json'))['versions']
    if want in vs:return want
    stable=[v for v in vs if '-' not in v]
    major=want.split('.')[0]
    compatible=[v for v in stable if v.split('.')[0]==major]
    return (compatible or stable)[-1]
seen=set()
def fetch(name,v):
    name=name.lower();v=v.lower()
    if (name,v) in seen:return
    seen.add((name,v));p=FEED/f'{name}.{v}.nupkg'
    if not p.exists():
        print('fetch',name,v,flush=True)
        try:p.write_bytes(get(f'https://api.nuget.org/v3-flatcontainer/{name}/{v}/{name}.{v}.nupkg'))
        except Exception:
            actual=version(name,v)
            if actual!=v: print('VERSION',name,v,'->',actual,flush=True);return fetch(name,actual)
            raise
    with zipfile.ZipFile(p) as z:
        ns=next(n for n in z.namelist() if n.endswith('.nuspec'))
        root=ET.fromstring(z.read(ns))
        for e in root.iter():e.tag=e.tag.split('}')[-1]
        deps=root.find('.//dependencies')
        if deps is None:return
        direct=deps.findall('dependency');groups=deps.findall('group')
        def rank(g):
            f=g.attrib.get('targetFramework','').lower()
            for i,k in enumerate(['net10','net9','net8','net7','net6','net5','.netstandard2.1','netstandard2.1','.netstandard2.0','netstandard2.0','.netcoreapp3.1','netcoreapp3.1','.netstandard1.3','.netframework4.6.2','.netframework4.6.1','']):
                if k and k in f:return 100-i
            return 1 if not f else 0
        if groups:
            best=max(groups,key=rank);direct+=best.findall('dependency')
        for d in direct:
            ver=d.attrib.get('version','').strip('[]()').split(',')[0].strip()
            if ver:fetch(d.attrib['id'],ver)
if __name__=='__main__':
    import sys
    if len(sys.argv)>2:fetch(sys.argv[1],sys.argv[2])
    else:
        todo=[]
        for p in [*ROOT.glob('src/*/*.csproj'),*ROOT.glob('tests/*/*.csproj')]:
            for e in ET.parse(p).iter('PackageReference'):todo.append((e.attrib['Include'],e.attrib['Version']))
        # Separate calls are idempotent; preserve completed downloads across interruption.
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as ex:
            for f in [ex.submit(fetch,*t) for t in todo]:f.result()
