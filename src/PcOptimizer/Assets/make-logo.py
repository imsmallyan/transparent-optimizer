from PIL import Image, ImageDraw, ImageFilter
S=1024; SS=4; N=S*SS
def bez(p0,p1,p2,p3,n=60):
    return [tuple((1-t)**3*a+3*(1-t)**2*t*b+3*(1-t)*t*t*c+t**3*d for a,b,c,d in zip(p0,p1,p2,p3)) for t in [i/n for i in range(n+1)]]
def shield(scale=1.0):
    pts=[]
    pts+=bez((0.5,0.17),(0.60,0.24),(0.70,0.26),(0.775,0.26))
    pts+=bez((0.775,0.26),(0.79,0.48),(0.75,0.70),(0.5,0.84))
    pts+=bez((0.5,0.84),(0.25,0.70),(0.21,0.48),(0.225,0.26))
    pts+=bez((0.225,0.26),(0.30,0.26),(0.40,0.24),(0.5,0.17))
    c=0.5
    return [((x-c)*scale+c,(y-0.5)*scale+0.5) for x,y in pts]
def render(size, detail=True, small=None):
    if small is None: small=size<32
    n=size*SS
    img=Image.new("RGBA",(n,n),(0,0,0,0))
    grad=Image.new("RGBA",(n,n))
    top=(0x2B,0xD4,0x6A); bot=(0x0E,0x9A,0x4A)
    gd=ImageDraw.Draw(grad)
    for y in range(n):
        t=y/(n-1); gd.line([(0,y),(n,y)],fill=tuple(int(a+(b-a)*t) for a,b in zip(top,bot))+(255,))
    mask=Image.new("L",(n,n),0)
    m=0.0 if small else 0.04
    ImageDraw.Draw(mask).rounded_rectangle([n*m,n*m,n*(1-m),n*(1-m)],radius=n*0.22,fill=255)
    img.paste(grad,(0,0),mask)
    d=ImageDraw.Draw(img,"RGBA")
    sc=1.12 if small else 1.0
    P=[(x*n,y*n) for x,y in shield(sc)]
    if detail:
        sh=Image.new("RGBA",(n,n),(0,0,0,0)); ImageDraw.Draw(sh).polygon([(x,y+n*0.015) for x,y in P],fill=(0,60,20,70))
        img=Image.alpha_composite(img,sh.filter(ImageFilter.GaussianBlur(n*0.012))); d=ImageDraw.Draw(img,"RGBA")
    d.polygon(P,fill=(255,255,255,255))
    if detail:
        right=Image.new("RGBA",(n,n),(0,0,0,0)); rd=ImageDraw.Draw(right)
        rd.polygon(P,fill=(0x0E,0x9A,0x4A,40))
        cut=Image.new("L",(n,n),0); ImageDraw.Draw(cut).rectangle([n*0.5,0,n,n],fill=255)
        img.paste(Image.alpha_composite(img,right),(0,0),cut); d=ImageDraw.Draw(img,"RGBA")
    w=n*(0.10 if small else 0.075)
    ck=[(0.37*n,0.51*n),(0.47*n,0.61*n),(0.65*n,0.42*n)]
    if small: ck=[(0.35*n,0.51*n),(0.47*n,0.63*n),(0.67*n,0.41*n)]
    col=(0x12,0xA8,0x52,255)
    d.line(ck,fill=col,width=int(w),joint="curve")
    for x,y in (ck[0],ck[2]): d.ellipse([x-w/2,y-w/2,x+w/2,y+w/2],fill=col)
    return img.resize((size,size),Image.LANCZOS)
import sys
out=sys.argv[1]
sizes=[16,24,32,48,64,128,256]
imgs=[render(s, detail=s>=48) for s in sizes]
imgs[-1].save(out+"/app.ico",sizes=[(s,s) for s in sizes],append_images=imgs[:-1])
render(1024).save(out+"/logo-1024.png")
for s,i in zip(sizes,imgs): i.save(f"{out}/p{s}.png")

render(64, detail=False, small=True).save(out+"/mark.png")
