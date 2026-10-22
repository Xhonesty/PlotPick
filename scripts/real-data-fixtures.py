"""20 chart fixtures derived from real, CC BY 4.0 UCI measurements.
These are newly rendered charts, not scans of published papers.
"""
import pathlib, urllib.request, zipfile, io, json, time
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
ROOT=pathlib.Path(__file__).resolve().parents[1]
DEST=ROOT/'samples'/'real-data';DEST.mkdir(parents=True,exist_ok=True)
def download(url):
    for i in range(5):
        try:return urllib.request.urlopen(url,timeout=30).read()
        except Exception:
            if i==4:raise
            time.sleep(1)
datasets=[('iris','https://archive.ics.uci.edu/static/public/53/iris.zip','iris.data','Fisher, R. (1936). Iris. DOI:10.24432/C56C76'),('wine','https://archive.ics.uci.edu/static/public/109/wine.zip','wine.data','Aeberhard, S. & Forina, M. (1992). Wine. DOI:10.24432/C5PC7J')]
manifest=[]
for name,url,filename,citation in datasets:
    path=DEST/(name+'.zip')
    if not path.exists():path.write_bytes(download(url))
    with zipfile.ZipFile(path) as z:text=z.read(filename).decode()
    lines=[line.split(',') for line in text.strip().splitlines() if line.strip()]
    data=np.array([[float(x) for x in (row[:4] if name=='iris' else row[1:])] for row in lines])
    for i in range(10):
        fig,ax=plt.subplots(figsize=(8,5),dpi=150)
        color='#2563EB';kind='scatter';truth=[]
        if i<4:
            x=data[:,i%data.shape[1]];y=data[:,(i+1)%data.shape[1]];ax.scatter(x,y,s=18,color=color);ax.set_xlabel('Feature '+str(i%data.shape[1]+1));ax.set_ylabel('Feature '+str((i+1)%data.shape[1]+1));truth=np.column_stack([x,y]).tolist()
        elif i<7:
            kind='curve';x=np.arange(1,51);y=data[:50,i%data.shape[1]];ax.plot(x,y,color=color,linewidth=1.6,linestyle='--' if i==6 else '-');ax.set_xlabel('Sample index');ax.set_ylabel('Measured feature');truth=np.column_stack([x,y]).tolist()
        else:
            kind='bars';x=np.arange(1,5);means=data[:,:4].mean(axis=0);sd=data[:,:4].std(axis=0,ddof=1);ax.bar(x,means,color=color,yerr=sd if i==9 else None,capsize=4);ax.set_xlabel('Feature');ax.set_ylabel('Mean');truth=np.column_stack([x,means]).tolist()
        ax.set_title(name.title()+' measurements / '+str(i+1));ax.grid(i%2==0,color='#DDDDDD',alpha=.6);ax.set_axisbelow(True);fig.tight_layout();fig.canvas.draw()
        x0,y0,x1,y1=ax.get_window_extent().extents;w,h=fig.canvas.get_width_height();file=f'{name}-{i:02}.png';fig.savefig(DEST/file)
        meta={'file':file,'kind':kind,'source':url,'citation':citation,'license':'CC BY 4.0 https://creativecommons.org/licenses/by/4.0/','modifications':'Data plotted with Matplotlib; selected columns/subsets or means, optional standard-deviation bars. Not a reproduction of a publication.','roi':{'X':float(x0+2),'Y':float(h-y1+2),'Width':float(x1-x0-4),'Height':float(y1-y0-4)},'data':truth,'xlim':list(ax.get_xlim()),'ylim':list(ax.get_ylim()),'test_use':'Real-data robustness fixtures; dense/overlapping markers are not included in clean-synthetic accuracy scores.'}
        (DEST/file.replace('.png','.json')).write_text(json.dumps(meta,indent=2),encoding='utf-8');manifest.append(meta);plt.close(fig);print(file,flush=True)
(DEST/'sources.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
