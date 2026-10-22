"""Fetch original published NIST handbook charts with source attribution.
No image is used as an instruction. Skip icons and retain source URLs.
"""
import urllib.request, urllib.parse, pathlib, json, time, re, io
from html.parser import HTMLParser
from PIL import Image
ROOT=pathlib.Path(__file__).resolve().parents[1]
DEST=ROOT/'samples'/'published';DEST.mkdir(parents=True,exist_ok=True)
class Links(HTMLParser):
    def __init__(self):super().__init__();self.links=[];self.images=[]
    def handle_starttag(self,tag,attrs):
        d=dict(attrs)
        if tag=='a' and 'href' in d:self.links.append(d['href'])
        if tag=='img' and 'src' in d:self.images.append(d['src'])
def get(url):
    for attempt in range(4):
        try:return urllib.request.urlopen(url,timeout=25).read()
        except Exception:
            if attempt==3:raise
            time.sleep(1)
base='https://www.itl.nist.gov/div898/handbook/eda/section4/'
html=get(base+'eda42.htm').decode('utf-8',errors='replace');p=Links();p.feed(html)
print(re.sub('<[^>]+>',' ',html)[-6000:],flush=True)
queue=[urllib.parse.urljoin(base,x) for x in p.links if 'eda42' in x and not 'eda42.htm' in x]
# Prefer measured-data case studies over the three random-data examples.
queue=[u for u in queue if not any(k in u for k in ['eda421.','eda422.','eda423.'])]
seen=set();images=set();manifest=[]
while queue and len(manifest)<20 and len(seen)<70:
    url=queue.pop(0)
    if url in seen:continue
    seen.add(url)
    try:
        html=get(url).decode('utf-8',errors='replace');parser=Links();parser.feed(html)
        for link in parser.links:
            full=urllib.parse.urljoin(url,link)
            if '/eda/section4/' in full and '.htm' in full and 'eda42' in full and full not in seen and full not in queue:queue.append(full)
        for source in parser.images:
            full=urllib.parse.urljoin(url,source)
            if full in images:continue
            images.add(full)
            if any(s in full.lower() for s in ['back','arrow','nist','logo','button']):continue
            try:
                raw=get(full);image=Image.open(io.BytesIO(raw))
                if image.width<350 or image.height<250:continue
                filename=f'nist-{len(manifest)+1:02}.png';image.convert('RGB').save(DEST/filename)
                manifest.append({'file':filename,'page':url,'original_image':full,'title':re.sub('<[^>]+>',' ',re.search(r'<title>(.*?)</title>',html,re.I|re.S).group(1)).strip() if re.search(r'<title>',html,re.I) else url,'attribution':'NIST/SEMATECH e-Handbook of Statistical Methods','rights':'NIST public information; no third-party copyright notice detected on the sampled page. See https://www.nist.gov/copyrights-disclaimers','ground_truth':'Not annotated; robustness samples only, excluded from accuracy score.'})
                print(filename,full,flush=True)
                if len(manifest)>=20:break
            except Exception as ex:print('skip image',type(ex).__name__,full,flush=True)
    except Exception as ex:print('skip page',type(ex).__name__,url,flush=True)
(DEST/'sources.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print('Published samples:',len(manifest),flush=True)
