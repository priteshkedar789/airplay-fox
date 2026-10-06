import sys, wave, numpy as np
w=wave.open(sys.argv[1]); n=w.getnframes(); a=np.frombuffer(w.readframes(n),dtype='<i2').reshape(-1,2).astype(float); L=a[:,0]
win=4410; rms=np.array([np.sqrt((L[i:i+win]**2).mean()) for i in range(0,n-win,win)])
t=np.where(rms>3000)[0]
if not len(t): print("no tone found"); sys.exit()
s,e=t[0]*win+win,t[-1]*win-win; x=L[s:e]
err=x[2:]-(2*np.cos(0.06)*x[1:-1]-x[:-2]); big=np.where(abs(err)>150)[0]
cl=[]
for b in big:
    if not cl or b-cl[-1][-1]>2000: cl.append([b])
    else: cl[-1].append(b)
print("tone %.2fs-%.2fs (%.1fs), err rms %.1f, glitch events: %d"%(s/44100,e/44100,(e-s)/44100,np.sqrt((err**2).mean()),len(cl)))
for c in cl: print("  event at %.3fs, %d samples, max err %.0f"%((c[0]+s)/44100,len(c),abs(err[c]).max()))
