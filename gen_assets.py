#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成《星界像素狂潮》M3 资源：像素精灵(PNG) 与 音效(WAV)。
纯标准库实现，无需 Pillow / 音频库。输出到 Assets/Resources。
"""
import os, zlib, struct, wave, math, random

BASE = r"C:/Users/VOS-User/WorkBuddy/2026-10-18-14-15-53/StarPixelRogue/Assets/Resources"
SPRITE_DIR = os.path.join(BASE, "Sprites")
AUDIO_DIR = os.path.join(BASE, "Audio")
os.makedirs(SPRITE_DIR, exist_ok=True)
os.makedirs(AUDIO_DIR, exist_ok=True)

# ---------------- PNG 编码 ----------------
def write_png(path, w, h, buf):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(buf[y*w*4:(y+1)*w*4])
    comp = zlib.compress(bytes(raw), 9)
    def chunk(typ, data):
        return struct.pack(">I", len(data)) + typ + data + struct.pack(">I", zlib.crc32(typ+data) & 0xffffffff)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)))
        f.write(chunk(b"IDAT", comp))
        f.write(chunk(b"IEND", b""))

class Canvas:
    def __init__(self, w, h):
        self.w, self.h, self.buf = w, h, bytearray(w*h*4)
    def set(self, x, y, c):
        if 0 <= x < self.w and 0 <= y < self.h and c[3] > 0:
            i = (y*self.w + x)*4
            a = c[3]/255.0
            for k in range(3):
                self.buf[i+k] = int(self.buf[i+k]*(1-a) + c[k]*a)
            self.buf[i+3] = max(self.buf[i+3], c[3])
    def rect(self, x0, y0, x1, y1, c):
        for y in range(int(y0), int(y1)+1):
            for x in range(int(x0), int(x1)+1):
                self.set(x, y, c)
    def disc(self, cx, cy, r, c):
        for y in range(int(cy-r-1), int(cy+r+2)):
            for x in range(int(cx-r-1), int(cx+r+2)):
                if (x-cx)**2 + (y-cy)**2 <= r*r:
                    self.set(x, y, c)
    def ring(self, cx, cy, r, th, c):
        for y in range(int(cy-r-1), int(cy+r+2)):
            for x in range(int(cx-r-1), int(cx+r+2)):
                d = ((x-cx)**2 + (y-cy)**2)**0.5
                if r-th <= d <= r:
                    self.set(x, y, c)
    def tri(self, p0, p1, p2, c):
        xs = [p0[0], p1[0], p2[0]]; ys = [p0[1], p1[1], p2[1]]
        minx, maxx = int(min(xs))-1, int(max(xs))+2
        miny, maxy = int(min(ys))-1, int(max(ys))+2
        def sign(a, b, p): return (p[0]-b[0])*(a[1]-b[1]) - (a[0]-b[0])*(p[1]-b[1])
        for y in range(miny, maxy):
            for x in range(minx, maxx):
                p = (x, y)
                d1 = sign(p0, p1, p); d2 = sign(p1, p2, p); d3 = sign(p2, p0, p)
                neg = (d1 < 0) or (d2 < 0) or (d3 < 0)
                pos = (d1 > 0) or (d2 > 0) or (d3 > 0)
                if not (neg and pos):
                    self.set(x, y, c)
    def save(self, name):
        write_png(os.path.join(SPRITE_DIR, name), self.w, self.h, self.buf)

def gen_player():
    c = Canvas(32, 32)
    c.disc(16, 17, 9, (30, 200, 225, 255))
    c.ring(16, 17, 9, 1.4, (8, 110, 140, 255))
    c.disc(16, 15, 3, (190, 255, 255, 255))          # 驾驶舱
    c.disc(16, 15, 1.4, (20, 120, 150, 255))
    c.rect(14, 2, 17, 9, (150, 160, 175, 255))       # 炮管
    c.rect(13, 8, 18, 10, (90, 100, 115, 255))
    c.save("player.png")

def gen_chaser():
    c = Canvas(32, 32)
    c.disc(16, 16, 10, (225, 50, 50, 255))
    c.ring(16, 16, 10, 1.4, (150, 20, 20, 255))
    for ex in (12, 20):
        c.disc(ex, 13, 2.6, (255, 255, 255, 255))
        c.disc(ex, 14, 1.2, (30, 30, 30, 255))
    c.rect(12, 22, 20, 23, (120, 15, 15, 255))       # 嘴
    c.save("enemy_chaser.png")

def gen_runner():
    c = Canvas(32, 32)
    c.tri((7, 7), (26, 16), (7, 25), (235, 215, 50, 255))      # 右指三角
    # 描边：再画略小深色
    c.tri((9, 9), (23, 16), (9, 23), (235, 215, 50, 255))
    c.disc(18, 16, 2, (255, 255, 255, 255))
    c.disc(18, 16, 1, (60, 55, 10, 255))
    c.save("enemy_runner.png")

def gen_tank():
    c = Canvas(32, 32)
    pts = []
    import math as _m
    for k in range(6):
        a = _m.pi/2 + k*_m.pi/3
        pts.append((16 + 11*_m.cos(a), 16 + 11*_m.sin(a)))
    # 六边形填充（用三角剖分近似）
    cx, cy = 16, 16
    for y in range(2, 30):
        for x in range(2, 30):
            inside = True
            for i in range(6):
                a, b = pts[i], pts[(i+1) % 6]
                if (b[0]-a[0])*(y-a[1]) - (b[1]-a[1])*(x-a[0]) > 0:
                    inside = False; break
            if inside:
                c.set(x, y, (50, 175, 70, 255))
    for p in pts:
        c.disc(p[0], p[1], 2, (20, 110, 35, 255))     # 角装甲
    c.rect(10, 12, 22, 20, (35, 150, 50, 255))        # 装甲板
    c.ring(16, 16, 11, 1.4, (20, 110, 35, 255))
    c.save("enemy_tank.png")

def gen_boss():
    import math as _m
    c = Canvas(48, 48)
    c.disc(24, 26, 18, (200, 45, 200, 255))
    c.ring(24, 26, 18, 1.6, (120, 20, 120, 255))
    for k in range(8):                                  # 尖刺
        a = k*_m.pi/4
        sx, sy = 24 + 20*_m.cos(a), 26 + 20*_m.sin(a)
        c.disc(sx, sy, 3, (160, 30, 160, 255))
    for kx in (12, 24, 36):                             # 王冠
        c.tri((kx-4, 10), (kx, 2), (kx+4, 10), (255, 215, 60, 255))
    for ex in (17, 31):                                 # 眼
        c.disc(ex, 23, 4, (255, 255, 255, 255))
        c.disc(ex, 25, 2, (20, 20, 20, 255))
        c.rect(ex-5, 17, ex+5, 19, (120, 20, 120, 255))  # 怒眉
    c.rect(15, 34, 33, 36, (120, 20, 120, 255))         # 嘴
    c.save("boss.png")

def gen_gem():
    c = Canvas(32, 32)
    c.tri((16, 4), (26, 16), (16, 28), (60, 130, 255, 255))
    c.tri((16, 4), (6, 16), (16, 28), (60, 130, 255, 255))
    c.ring(16, 16, 12, 1.2, (25, 70, 210, 255))
    c.tri((16, 4), (26, 16), (16, 16), (180, 220, 255, 255))  # 高光
    c.save("gem.png")

def gen_projectile():
    c = Canvas(16, 16)
    c.disc(8, 8, 6, (255, 230, 90, 255))
    c.disc(8, 8, 2.6, (255, 255, 225, 255))
    c.save("projectile.png")

def gen_elite():
    # 精英怪：金色装甲球 + 尖刺 + 怒眉，明显区别于普通敌人
    c = Canvas(32, 32)
    c.disc(16, 16, 11, (245, 190, 60, 255))
    c.ring(16, 16, 11, 1.6, (150, 110, 20, 255))
    import math as _m
    for k in range(8):
        a = k * _m.pi / 4
        sx, sy = 16 + 13 * _m.cos(a), 16 + 13 * _m.sin(a)
        c.disc(sx, sy, 2.6, (255, 220, 120, 255))
    for ex in (12, 20):
        c.disc(ex, 13, 2.8, (255, 255, 255, 255))
        c.disc(ex, 14, 1.2, (40, 30, 10, 255))
    c.rect(12, 22, 20, 23, (140, 100, 15, 255))
    c.save("enemy_elite.png")

def gen_health():
    # 回血球：红色像素心
    c = Canvas(32, 32)
    c.disc(11, 12, 6.5, (235, 45, 60, 255))
    c.disc(21, 12, 6.5, (235, 45, 60, 255))
    c.tri((5, 13), (27, 13), (16, 28), (235, 45, 60, 255))
    c.ring(16, 16, 11, 1.2, (170, 20, 35, 255))
    c.disc(11, 10, 2.4, (255, 150, 160, 255))
    c.rect(13, 8, 18, 9, (255, 150, 160, 255))
    c.save("health.png")

# ---------------- WAV 编码 ----------------
def write_wav(name, samples, rate=44100):
    with wave.open(os.path.join(AUDIO_DIR, name), "w") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        frames = bytearray()
        for s in samples:
            v = int(max(-1, min(1, s)) * 32767)
            frames += struct.pack("<h", v)
        w.writeframes(bytes(frames))

def tone(freq, dur, shape="sine", vol=0.3, slide=None, trem=0.0):
    n = int(44100*dur); out = []
    for i in range(n):
        t = i/44100
        f = freq + (slide-freq)*(i/n) if slide else freq
        if shape == "square": s = 1.0 if math.sin(2*math.pi*f*t) >= 0 else -1.0
        elif shape == "saw": s = 2*((f*t) % 1) - 1
        else: s = math.sin(2*math.pi*f*t)
        if trem > 0:
            s *= (0.6 + 0.4*math.sin(2*math.pi*trem*t))
        env = min(1, i/300) * max(0, 1 - (i/(n-1)))
        out.append(s*vol*env)
    return out

def noise(dur, vol=0.2):
    n = int(44100*dur); out = []
    for i in range(n):
        env = max(0, 1 - (i/(n-1)))
        out.append((random.random()*2-1)*vol*env)
    return out

def concat(*lists):
    out = []
    for l in lists: out += l
    return out

def gen_audio():
    write_wav("shoot.wav", tone(880, 0.08, "square", 0.16, slide=440))
    write_wav("hit.wav", concat(noise(0.05, 0.18), tone(180, 0.06, "square", 0.16)))
    write_wav("pickup.wav", tone(600, 0.10, "sine", 0.22, slide=1100))
    write_wav("levelup.wav", concat(tone(523,0.10,"sine",0.22), tone(659,0.10,"sine",0.22), tone(784,0.16,"sine",0.24)))
    write_wav("boss.wav", tone(90, 0.5, "saw", 0.30, slide=140, trem=14))
    write_wav("hurt.wav", tone(150, 0.09, "square", 0.20, slide=90))
    write_wav("gameover.wav", tone(400, 0.6, "saw", 0.28, slide=90))
    # M7：组合技激活提示音（上行琶音）
    write_wav("combo.wav", concat(
        tone(659,0.06,"square",0.16), tone(784,0.06,"square",0.16),
        tone(988,0.09,"square",0.18), tone(1319,0.14,"sine",0.22)))

# ---------------- BGM（M7：程序化合成循环背景音乐） ----------------
# 22050Hz 单声道、约 0.6-1MB/轨；三段均为整数小节循环，接缝做微淡出防爆音。
BGM_RATE = 22050

def nf(midi):  # MIDI 音高 → 频率
    return 440.0 * (2 ** ((midi - 69) / 12.0))

def _env(i, n, a, r):
    ai = max(1, int(a*BGM_RATE)); ri = max(1, int(r*BGM_RATE))
    if i < ai: return i/ai
    if i > n-ri: return max(0.0, (n-i)/ri)
    return 1.0

def mnote(midi, dur, shape="sine", vol=0.3, a=0.012, r=0.09):
    n = int(BGM_RATE*dur); f = nf(midi); out = []
    for i in range(n):
        t = i/BGM_RATE
        if shape == "square": s = 1.0 if math.sin(2*math.pi*f*t) >= 0 else -1.0
        elif shape == "saw":   s = 2*((f*t) % 1) - 1
        elif shape == "tri":   s = 4*abs((f*t) % 1 - 0.5) - 1
        else:                  s = math.sin(2*math.pi*f*t)
        out.append(s*vol*_env(i, n, a, r))
    return out

def kick(dur=0.14, vol=0.55):
    n = int(BGM_RATE*dur); out = []
    for i in range(n):
        f = 130 - 85*(i/n)
        out.append(math.sin(2*math.pi*f*i/BGM_RATE)*vol*max(0, 1-i/n))
    return out

def snare(dur=0.13, vol=0.30):
    n = int(BGM_RATE*dur); out = []
    for i in range(n):
        e = max(0, 1-i/n)**0.7
        out.append(((random.random()*2-1)*0.7 + math.sin(2*math.pi*195*i/BGM_RATE)*0.3)*vol*e)
    return out

def hat(dur=0.04, vol=0.10):
    n = int(BGM_RATE*dur); out = []
    for i in range(n):
        out.append((random.random()*2-1)*vol*max(0, 1-i/n))
    return out

def assemble(bpm, beats, events):
    """events: [(拍偏移, 样本)] → 整轨（软限幅 + 接缝微淡出）。"""
    spb = 60.0/bpm; total = int(BGM_RATE*spb*beats)
    buf = [0.0]*total
    for off, sig in events:
        start = int(BGM_RATE*spb*off)
        for i, v in enumerate(sig):
            j = start+i
            if 0 <= j < total: buf[j] += v
    tail = int(0.02*BGM_RATE)
    for i in range(tail): buf[total-1-i] *= i/tail
    return [max(-0.98, min(0.98, x)) for x in buf]

def gen_bgm():
    # —— 菜单乐：A 小调舒缓琶音（Am-F-C-G，84BPM，8 小节） ——
    prog = [[57,60,64,69], [53,57,60,65], [48,55,60,64], [55,59,62,67]]
    ev = []
    for bar in range(8):
        ch = prog[bar % 4]; b = bar*4
        ev.append((b+0.0, mnote(ch[0]-24, 1.9, "tri", 0.30, a=0.02, r=0.12)))
        ev.append((b+2.0, mnote(ch[0]-24, 1.9, "tri", 0.24, a=0.02, r=0.12)))
        for k in range(8):  # 八分音符琶音上行回落
            ev.append((b+k*0.5, mnote(ch[k % 3 + 1]+12, 0.40, "sine", 0.15, a=0.015, r=0.10)))
        ev.append((b+0.0, mnote(ch[2], 3.8, "tri", 0.07, a=0.30, r=0.20)))   # 柔和铺底
    write_wav("bgm_menu.wav", assemble(84, 32, ev), rate=BGM_RATE)

    # —— 战斗乐：D 小调驱动（Dm-Bb-F-C，132BPM，8 小节） ——
    prog = [[50,53,57], [46,50,53], [53,57,60], [48,52,55]]
    ev = []
    for bar in range(8):
        ch = prog[bar % 4]; b = bar*4
        for k in range(8):  # 低音八分音符（根/五交替）
            ev.append((b+k*0.5, mnote(ch[0] if k % 2 == 0 else ch[0]+7, 0.42, "square", 0.20, a=0.005, r=0.03)))
        ev.append((b+1.0, mnote(ch[1]+12, 0.30, "saw", 0.13, a=0.005, r=0.05)))  # 离拍刺音
        ev.append((b+3.0, mnote(ch[2]+12, 0.30, "saw", 0.13, a=0.005, r=0.05)))
        ev.append((b+0.0, mnote(ch[0]-12, 1.8, "saw", 0.10, a=0.02, r=0.10)))
    for bar in range(8):
        b = bar*4
        ev.append((b+0.0, kick())); ev.append((b+1.0, kick(vol=0.4)))
        ev.append((b+2.0, kick())); ev.append((b+3.0, kick(vol=0.4)))
        ev.append((b+1.0, snare())); ev.append((b+3.0, snare()))
        for k in range(8): ev.append((b+k*0.5, hat(vol=0.09 if k % 2 else 0.06)))
    write_wav("bgm_battle.wav", assemble(132, 32, ev), rate=BGM_RATE)

    # —— Boss 乐：E 小调紧张（Em-C-G-D，152BPM，8 小节，快速琶音 + 重鼓） ——
    prog = [[52,55,59], [48,52,55], [55,59,62], [50,54,57]]
    ev = []
    for bar in range(8):
        ch = prog[bar % 4]; b = bar*4
        for k in range(16):  # 十六分音符快速琶音
            ev.append((b+k*0.25, mnote(ch[k % 3]+12, 0.20, "saw", 0.085, a=0.004, r=0.04)))
        for k in range(4):
            ev.append((b+k, mnote(ch[0]-12, 0.9, "square", 0.22, a=0.004, r=0.04)))
        ev.append((b+2.5, mnote(ch[0]-12+6, 0.4, "square", 0.18, a=0.004, r=0.04)))  # 三全音张力
    for bar in range(8):
        b = bar*4
        ev.append((b+0.0, kick(vol=0.65))); ev.append((b+1.5, kick(vol=0.5)))
        ev.append((b+2.0, kick(vol=0.65))); ev.append((b+3.5, kick(vol=0.5)))
        ev.append((b+1.0, snare(vol=0.36))); ev.append((b+3.0, snare(vol=0.36)))
        for k in range(8): ev.append((b+k*0.5, hat(vol=0.10 if k % 2 else 0.07)))
    write_wav("bgm_boss.wav", assemble(152, 32, ev), rate=BGM_RATE)

if __name__ == "__main__":
    gen_player(); gen_chaser(); gen_runner(); gen_tank(); gen_boss(); gen_gem(); gen_projectile()
    gen_elite(); gen_health()
    gen_audio()
    gen_bgm()
    print("OK sprites:", sorted(os.listdir(SPRITE_DIR)))
    print("OK audio:", sorted(os.listdir(AUDIO_DIR)))
