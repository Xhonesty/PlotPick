"""Rasterize the existing PlotPick blue-square/white-curve mark into a Windows ICO.
Only used to regenerate committed branding assets; requires Pillow.
"""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
out = root / 'assets' / 'branding'
out.mkdir(parents=True, exist_ok=True)
svg = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<rect x="8" y="8" width="240" height="240" rx="58" fill="#2563EB"/>
<path d="M58 188 L93 139 L128 160 L163 90 L205 62" fill="none" stroke="white" stroke-width="16" stroke-linecap="round" stroke-linejoin="round"/>
</svg>'''
(out / 'PlotPick.svg').write_text(svg, encoding='utf-8')
scale = 4
im = Image.new('RGBA', (256*scale, 256*scale))
d = ImageDraw.Draw(im)
d.rounded_rectangle((8*scale,8*scale,248*scale,248*scale),radius=58*scale,fill='#2563EB')
points=[(58,188),(93,139),(128,160),(163,90),(205,62)]
d.line([(x*scale,y*scale) for x,y in points],fill='white',width=16*scale,joint='curve')
for x,y in points:
    d.ellipse(((x-8)*scale,(y-8)*scale,(x+8)*scale,(y+8)*scale),fill='white')
im=im.resize((256,256),Image.Resampling.LANCZOS)
im.save(out/'PlotPick.png')
im.save(out/'PlotPick.ico',sizes=[(s,s) for s in (16,20,24,32,40,48,64,128,256)])
print(out/'PlotPick.ico')
