import pathlib,zipfile,xml.etree.ElementTree as ET,json,urllib.request,time
ROOT=pathlib.Path(__file__).resolve().parents[1];OUT=ROOT/'assets/licenses';OUT.mkdir(parents=True,exist_ok=True)
lock=json.loads((ROOT/'src/PlotPick.App/packages.lock.json').read_text())
needed={}
for framework,deps in lock['dependencies'].items():
    for name,info in deps.items():
        if 'resolved' in info:needed[name.lower()]=info['resolved']
inventory=[]
for name,version in needed.items():
    package=ROOT/'.tools/feed'/f'{name}.{version}.nupkg'
    if not package.exists():continue
    folder=OUT/f'{name}-{version}';folder.mkdir(exist_ok=True)
    with zipfile.ZipFile(package) as z:
        for item in z.namelist():
            base=pathlib.PurePosixPath(item).name
            if base and (any(k in base.lower() for k in ['license','licence','notice','copying','copyright']) or base.endswith('.nuspec')):
                (folder/base).write_bytes(z.read(item))
        spec=ET.fromstring(z.read(next(i for i in z.namelist() if i.endswith('.nuspec'))))
        for e in spec.iter():e.tag=e.tag.split('}')[-1]
        info={k:(spec.find('.//'+k).text if spec.find('.//'+k) is not None else '') for k in ['id','version','authors','copyright','license','projectUrl']};inventory.append(info)
(OUT/'inventory.json').write_text(json.dumps(inventory,indent=2),encoding='utf-8')
urls={
 'Tesseract-LICENSE':'https://raw.githubusercontent.com/tesseract-ocr/tesseract/main/LICENSE',
 'Tessdata-LICENSE':'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/LICENSE',
 'OpenCvSharp-LICENSE':'https://raw.githubusercontent.com/shimat/opencvsharp/4.11.0.20250507/LICENSE',
 'MathNet-LICENSE':'https://raw.githubusercontent.com/mathnet/mathnet-numerics/v5.0.0/LICENSE.md',
 'ClosedXML-LICENSE':'https://raw.githubusercontent.com/ClosedXML/ClosedXML/0.105.0/LICENSE',
 'SkiaSharp-LICENSE':'https://raw.githubusercontent.com/mono/SkiaSharp/main/LICENSE.md',
 'CommunityToolkit-LICENSE':'https://raw.githubusercontent.com/CommunityToolkit/dotnet/v8.4.0/License.md',
 'MSVC-REDIST-license.html':'https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/'
}
for name,url in urls.items():
    if (OUT/name).exists():continue
    for i in range(3):
        try:(OUT/name).write_bytes(urllib.request.urlopen(url,timeout=20).read());print('saved',name,flush=True);break
        except Exception as ex:
            if i==2:print('notice fetch failed',name,str(ex),flush=True)
            time.sleep(1)
for file in [ROOT/'.tools/dotnet/LICENSE.txt',ROOT/'.tools/dotnet/ThirdPartyNotices.txt']:
    (OUT/('dotnet-'+file.name)).write_bytes(file.read_bytes())
