"""Зеркало FilterGraphBuilder.cs на Python: проверяет, что генерируемые графы принимает настоящий FFmpeg.
Использование: python3 graph_mirror.py <workdir> <style> <highlight> <bgkind> <offset> > graph.txt"""
import sys, os, math
def N(v): s=f"{v:.6f}".rstrip('0').rstrip('.'); return s if s not in ('-0','') else '0'
def N3(v): s=f"{v:.3f}".rstrip('0').rstrip('.'); return s if s not in ('-0','') else '0'
def Even(v): return max(2, int(round(v/2.0))*2)
def ff(h): return '0x'+h.strip('#').upper()
def mixer(h):
    h=h.strip('#'); r,g,b=(int(h[i:i+2],16) for i in (0,2,4)); return f"rr={N3(r/255)}:gg={N3(g/255)}:bb={N3(b/255)}"
work,style,hlstyle,bgkind,offset=sys.argv[1],sys.argv[2],sys.argv[3],sys.argv[4],float(sys.argv[5])
W,H,F=1280,720,30
titles=["Cartographer","The Village Needed a Witch","Collector of Moths — «тест»"]
starts=[0,14,25]; total=35
T=f"(t+{N(offset)})" if offset>0.0005 else "t"; sc=H/1080
g=[]
fit=f"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},setsar=1"
if bgkind=='image':
    W2,H2=Even(W*1.5),Even(H*1.5); P=60*F
    on=f"(on+{N(round(offset*F))})" if offset>0.0005 else "on"; ph=f"2*PI*{on}/{N(P)}"
    z=f"1+0.10*(1-cos({ph}))/2"; x="iw/2-(iw/zoom/2)"; y="ih/2-(ih/zoom/2)"
    g.append(f"[0:v]scale={W2}:{H2}:force_original_aspect_ratio=increase,crop={W2}:{H2},setsar=1,zoompan=z='{z}':x='{x}':y='{y}':d=1:s={W}x{H}:fps={F},format=yuv420p")
else:
    g.append(f"[0:v]{fit},fps={F},format=yuv420p")
b="-"+N(0.1*0.35)+f"+0.025*sin(2*PI*{T}/2.7)+0.012*sin(2*PI*{T}/0.83)"
g.append(f",eq=brightness='{b}':eval=frame[bg0];")
hw,hh=Even(W/2),Even(H/2)
g.append(f"color=c=gray:s=192x108:r={F},noise=alls=100:allf=u,format=gray,lutyuv=y='if(gt(val\\,184)\\,255\\,0)',scroll=v=-0.0015:h=0.0005,scale={hw}x{hh}:flags=bicubic,gblur=sigma={N3(1.25*H/720)},scale={W}x{H}:flags=bilinear,format=yuv420p,lutyuv=y='min(255\\,max(0\\,val-16)*0.6)':u=128:v=128[pm];")
g.append(f"color=c=0xF5E6D0:s={W}x{H}:r={F},format=yuv420p[pw];[bg0][pw][pm]maskedmerge=planes=1[bg1];")
# visualizer
col="#F17C8A"; Wv=Even(W*0.9); Hv=Even(H*0.18); k=3; avg=1+round(0.4*6)
isWave=style in('laser','pulse')
s=f"[1:a]aformat=sample_fmts=fltp:channel_layouts=stereo,volume=1.2,bass=g=4:f=100"
if isWave: s+=f",lowpass=f={int(9000-0.4*7500)}"
s+=','; iw,ih=Even(Wv/k),Even(Hv/k); vw=Wv
if style=='laser': s+=f"showwaves=s={iw}x{ih}:mode=p2p:rate={F}:colors=white:scale=sqrt:draw=full,format=rgba,scale={Wv}x{Hv}:flags=bilinear"
elif style=='pulse': s+=f"showwaves=s={iw}x{ih}:mode=cline:rate={F}:colors=white:scale=sqrt:draw=full,format=rgba,scale={Wv}x{Hv}:flags=bilinear"
elif style=='bars':
    nb=max(16,min(128,round(Wv/(24*sc*(k/3+0.5))))); bw=max(4,Wv//nb); vw=bw*nb; gap=max(2,bw//4)
    s+=f"showfreqs=s={nb}x{max(16,Hv//4)}:mode=bar:ascale=log:fscale=log:win_size=2048:averaging={avg}:cmode=combined:colors=white|white,format=rgba,scale={vw}x{Hv}:flags=neighbor,drawgrid=w={bw}:h={Hv+10}:t={gap}:c=black@0:replace=1"
elif style=='line': s+=f"showfreqs=s={iw}x{ih}:mode=line:ascale=log:fscale=log:win_size=2048:averaging={avg}:cmode=combined:colors=white|white,format=rgba,scale={Wv}x{Hv}:flags=bilinear"
else:
    dw,dh=Even(Wv/(k+2)),Even(Hv/(k+2)); s+=f"showfreqs=s={dw}x{dh}:mode=dot:ascale=log:fscale=log:win_size=2048:averaging={avg}:cmode=combined:colors=white|white,format=rgba,scale={Wv}x{Hv}:flags=neighbor"
s+=(f",gblur=sigma={N3(max(0.5,0.9*sc))},format=rgba" if style in ("laser","pulse","line") else "")+f",colorchannelmixer={mixer(col)},tmix=frames=2"
sig=(3+0.6*12)*sc; gain=1+0.6*5
s+=f",split[vz1][vz2];[vz2]alphaextract,gblur=sigma={N3(sig)},lutyuv=y='min(255\\,val*{N3(gain)})'[vza];color=c={ff(col)}:s={vw}x{Hv}:r={F},format=rgba[vzc];[vzc][vza]alphamerge[vzg];[vzg][vz1]overlay=format=auto"
s+=",colorchannelmixer=aa=0.9[viz];"
g.append(s); g.append(f"[bg1][viz]overlay=x={round(W*0.05)}:y={round(H*0.74)}:format=auto[v0];")
# texts
def wt(name,text): open(os.path.join(work,name),'w',encoding='utf-8').write(text); return name
sh=f":shadowx={max(1,round(2*sc))}:shadowy={max(1,round(2*sc))}:shadowcolor=black@0.55"
def DT(font,file,x,y,size,color,extra=""): return f"drawtext=fontfile={font}:textfile={file}:expansion=none:x={N3(x)}:y={N3(y)}:fontsize={max(6,round(size))}:fontcolor={color}{sh}{extra}"
L=[]; hl='0xF17C8A'
tx,ty=W*0.05,H*0.06
L.append(DT('ft.ttf',wt('album.txt','THE ATLAS OF UNSPOKEN THINGS'),tx,ty,54*sc,'0xE8E6EF')); ty+=54*sc*1.3
L.append(DT('fb.ttf',wt('artist.txt','ELARA MORVAYNE'),tx,ty,30*sc,hl+'@0.95'))
open(os.path.join(work,'marker.txt'),'w',encoding='utf-8').write('●')
ts=26*sc; lineH=ts*1.55; x0,y0=W*0.05,H*0.22
for i,tt in enumerate(titles):
    x,y=x0,y0+i*lineH; label=f"{i+1:02d}.  {tt}"; f=wt(f"tl_{i}.txt",label)
    s0=starts[i]; e0=starts[i+1] if i+1<len(starts) else total
    btw=f"between({T},{N3(s0)},{N3(e0)})"
    L.append(DT('fb.ttf',f,x,y,ts,'0xD9D6E2@0.82',f":enable='not({btw})'"))
    if hlstyle=='glow': L.append(DT('fb.ttf',f,x,y,ts,hl,f":borderw={max(1,round(3*sc))}:bordercolor={hl}@0.35:enable='{btw}'"))
    elif hlstyle=='underline':
        L.append(DT('fb.ttf',f,x,y,ts,hl,f":enable='{btw}'"))
        L.append(f"drawbox=x={int(x)}:y={int(y+ts*1.22)}:w=200:h={max(2,round(2*sc))}:color={hl}@0.9:t=fill:enable='{btw}'")
    elif hlstyle=='fade': L.append(DT('fb.ttf',f,x,y,ts,hl,f":alpha='min(1,max(0,({T}-{N3(s0)})/0.8))':enable='{btw}'"))
    elif hlstyle=='marker':
        L.append(DT('fb.ttf',f,x,y,ts,hl,f":enable='{btw}'"))
        mx=x-ts*1.05
        L.append(f"drawtext=fontfile=fb.ttf:textfile=marker.txt:expansion=none:x='{N3(mx)}+{N3(4*sc)}*sin(2*PI*{T})':y={N3(y+ts*0.18)}:fontsize={max(6,round(ts*0.7))}:fontcolor={hl}:enable='{btw}'")
    else: L.append(DT('fb.ttf',f,x,y,ts,hl,f":enable='{btw}'"))
for i,tt in enumerate(titles):
    s0=starts[i]; e0=starts[i+1] if i+1<len(starts) else total; S,E=N3(s0),N3(e0)
    a=f"if(lt({T}-{S},1),max(0,{T}-{S}),if(gt({T},{E}-1),max(0,{E}-{T}),1))"
    L.append(DT('ft.ttf',wt(f"cur_{i}.txt",tt),W*0.55,H*0.64,40*sc,'0xE8E6EF',f":alpha='{a}':enable='between({T},{S},{E})'"))
L.append(DT('fb.ttf',wt('custom.txt','Subscribe ✦ new album'),W*0.70,H*0.92,26*sc,'0xD9D6E2'))
g.append("[v0]"+",".join(L)+"[vt];")
g.append(f"[2:v]scale={Even(W*0.07)}:-1,format=rgba,colorchannelmixer=aa=0.9[logo];[vt][logo]overlay=x={round(W*0.9)}:y={round(H*0.05)}:format=auto[vl];[vl]format=yuv420p[vout]")
print("".join(g))
