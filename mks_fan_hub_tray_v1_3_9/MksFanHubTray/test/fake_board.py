#!/usr/bin/env python3
# Эмулятор платы MKS Fan Hub v0.3.15 по Serial-протоколу (для проверки трей-приложения)
import os, sys, time, select, termios, tty

CH = ["FUN_PWM_0","FUN_PWM_1","FUN_PWM_2","FUN_PWM_3","FUN_DC_1","FUN_DC_0","FUN_DC_2","FUN_PWM_4","FUN_PWM_5","FUN_DC_3"]
TH = ["CASE","TH1","TH2","TB","EXT"]
SER = ["P102","GTX","CPU","SSD"]
CURVE_A = [(28,15),(35,25),(42,40),(50,60),(55,100)]
CURVE_B = [(28,20),(35,35),(42,55),(48,80),(52,100)]
curves = {c: list(CURVE_A if "PWM" in c else CURVE_B) for c in CH}
curves["FUN_PWM_0"] = [(30,20),(50,35),(65,55),(75,80),(85,100)]
binds = {c: ("THERM","CASE") for c in CH}
binds["FUN_PWM_0"] = ("SERIAL","P102")
mode = 0
therm_state = {"CASE":("1","27.4","OK"),"TH1":("1","NaN","ABSENT"),"TH2":("1","NaN","FAULT"),"TB":("1","NaN","FAULT"),"EXT":("1","NaN","ABSENT")}
serial_vals = {k:0.0 for k in SER}
pct = {c: 30 for c in CH}
locked = {"FUN_PWM_0","FUN_PWM_1","FUN_DC_0"}
last_temp = time.time()
tachtest_at = None
TACH_NEEDS_FULL = [4,5,6,9]
TACHTEST_RESULT = {"FUN_DC_1":("63","OK",1110),"FUN_DC_0":("51","OK",1090),"FUN_DC_2":("64","NOSIG",0),"FUN_DC_3":("66","NOSIG",0)}
temp_count = 0
dcf = {c: 31 for c in CH if "DC" in c}   # частоты ШИМ DC-каналов (прошивка 0.3.22)
dcf_saved = dict(dcf)
NO_DCFREQ = os.environ.get("EMU_NODCFREQ") == "1"   # эмуляция старой прошивки: DCFREQ= не отвечает
def per(hz): return int((4000 + hz // 2) // hz)
spin_start = {c: 37 for c in CH if "DC" in c}
spin_state = {c: "OK" for c in CH if "DC" in c}
spin_param = [5, 5, 1000, 60, 3]     # boost, step, rampMs, probeS, tries
def spin_dump():
    for c in CH:
        if c in spin_start: w("SPIN=%s,%d,%s,%d" % (c, spin_start[c], spin_state[c], 3 if spin_state[c] == "ERR" else 0))
    w("SPIN=PARAM,%d,%d,%d,%d,%d" % tuple(spin_param)); w("SPIN=END")
corrupt_left = int(os.environ.get("EMU_CORRUPT", "0"))   # сколько первых CURVE= искажать (тест проверки отправки)
holds = {}                      # канал -> (проценты, до какого времени)
HB_PERIOD = float(os.environ.get("EMU_HB", "5"))
next_hb = time.time() + HB_PERIOD
t_start = time.time()

fd = os.open(sys.argv[1], os.O_RDWR | os.O_NOCTTY)
tty.setraw(fd)

def w(s):
    os.write(fd, (s + "\n").encode())

def status():
    w("STATUS=MODE," + ("TMP-SRL" if mode == 0 else "TMP-MAN"))
    for k in SER: w("STATUS=SERIAL,%s,%.1f" % (k, serial_vals[k]))
    for t in TH:
        en, v, st = therm_state[t]; w("STATUS=THERM,%s,%s,%s,%s" % (t, en, v, st))
    for c in CH:
        lk = "LOCKED" if (mode == 0 and c in locked) else "FREE"
        w("STATUS=CH,%s,%d,%s,%s,%s,ACTIVE" % (c, pct[c], binds[c][0], binds[c][1], lk))
    tach = {"FUN_PWM_0":("18","OK",1140),"FUN_PWM_1":("19","OK",1100),"FUN_PWM_2":("14","NOSIG",0)}
    for c in CH:
        t = tach.get(c, ("-","NONE",0)); w("STATUS=TACH,%s,%s,%s,%d" % (c, t[0], t[1], t[2]))
    for c in CH:
        if c in spin_state: w("STATUS=SPIN,%s,%s,%d" % (c, spin_state[c], 3 if spin_state[c] == "ERR" else 0))
    w("STATUS=WATCHDOG,OK"); w("STATUS=END")

dump_delay = 0.0

def dump(ch, bin_us, nb, kind):
    if dump_delay > 0: time.sleep(dump_delay)      # плата "занята" записью и не читает Serial
    w("TACHDUMP=BEGIN,%d,%s,%s,%d,%d,%d" % (CH.index(ch), ch, "63", pct[ch], bin_us, nb))
    import random
    random.seed(CH.index(ch) * 1000 + pct[ch])
    for i in range(0, nb, 40):
        hexs = ""
        for k in range(i, min(i + 40, nb)):
            # синтетика: "шумовые" пачки и чистые паузы
            noisy = ((k // 12) % 2 == 0)
            tr = random.randint(20, 60) if noisy else 0
            hi = random.randint(60, 200) if noisy else 255
            hexs += "%02X%02X" % (tr, hi)
        w("TACHDUMP=D,%d,%s" % (i, hexs))
    w("TACHDUMP=END,%d" % CH.index(ch))

def resolve(tok):
    if tok.isdigit():
        n = int(tok)
        return CH[n] if 0 <= n < len(CH) else None
    return tok if tok in CH else None

def handle(line):
    global mode, temp_count, curves, tachtest_at, corrupt_left
    line = line.strip()
    if not line.startswith("TEMP="):
        open("/tmp/emu_cmds.log","a").write("%.3f %s\n" % (time.time(), line))
    if line.startswith("EMU=SLOW,"):
        global dump_delay
        dump_delay = float(line[9:]); return
    if line.startswith("EMU=CORRUPT,"):  # тест: исказить первые n команд CURVE= (потеря символа)
        corrupt_left = int(line[12:]); w("EVENT=EMU_CORRUPT," + str(corrupt_left)); return
    if line.startswith("EMU="):          # тестовый хук: EMU=<текст события> -> EVENT=<текст>
        w("EVENT=" + line[4:]); return
    if line.startswith("SPIN"):
        if NO_DCFREQ: return
        if line.startswith("SPIN?"): spin_dump(); return
        a = line[5:].split(",")
        if a[0] == "SAVE": w("SPIN=SAVED"); return
        if a[0] == "RESET":
            for c in spin_start: spin_start[c] = 37
            spin_param[:] = [5, 5, 1000, 60, 3]; w("SPIN=RESET_OK"); return
        if a[0] == "PARAM":
            v = [int(x) for x in a[1:6]]
            if len(v) < 5: w("SPIN=ERR,args"); return
            spin_param[:] = v; w("SPIN=OK,PARAM"); return
        if a[0] in ("CLEAR", "TEST"):
            c = resolve(a[1]) if len(a) > 1 else None
            if c not in spin_start: w("SPIN=ERR,channel"); return
            if a[0] == "CLEAR": spin_state[c] = "OK"; w("SPIN=OK,CLEAR," + c)
            else: w("SPIN=OK,TEST," + c)
            return
        c = resolve(a[0]) if len(a) > 1 else None
        if c not in spin_start: w("SPIN=ERR,channel"); return
        spin_start[c] = int(a[1]); w("SPIN=OK,%s,%s" % (c, a[1])); return
    if line.startswith("DCFREQ"):
        if NO_DCFREQ: return
        if line.startswith("DCFREQ?"):
            for c in CH:
                if c in dcf: w("DCFREQ=%s,%d,%d" % (c, round(4000 / per(dcf[c])), per(dcf[c])))
            w("DCFREQ=END"); return
        a = line[7:].split(",")
        if a[0] == "SAVE": dcf_saved.update(dcf); w("DCFREQ=SAVED"); return
        if a[0] == "RESET":
            for c in dcf: dcf[c] = 31
            w("DCFREQ=RESET_OK"); return
        if len(a) < 2: w("DCFREQ=ERR,args"); return
        hz = int(a[1])
        if hz < 15 or hz > 300: w("DCFREQ=ERR,range"); return
        names = list(dcf) if a[0] == "ALL" else ([resolve(a[0])] if resolve(a[0]) in dcf else [])
        if not names: w("DCFREQ=ERR,channel"); return
        for c in names:
            dcf[c] = hz; w("DCFREQ=OK,%s,%d,%d" % (c, round(4000 / per(hz)), per(hz)))
        return
    if line.startswith("HOLD?"):
        now = time.time()
        for c, (p, until) in list(holds.items()): w("HOLD=%s,%d,%d" % (c, p, max(0, int(until - now))))
        w("HOLD=END"); return
    if line.startswith("HOLD="):
        a = line[5:].split(",")
        if len(a) < 2: w("HOLD=ERR,args"); return
        if a[0] == "ALL" and a[1] == "OFF": holds.clear(); w("HOLD=OFF,ALL"); return
        c = resolve(a[0])
        if c is None: w("HOLD=ERR,channel"); return
        if a[1] == "OFF": holds.pop(c, None); w("HOLD=OFF," + c); return
        p = max(0, min(100, int(a[1]))); sec = max(1, min(300, int(a[2]) if len(a) > 2 else 30))
        holds[c] = (p, time.time() + sec); pct[c] = p
        w("HOLD=OK,%s,%d,%d" % (c, p, sec)); return
    if line.startswith("TACHDUMP="):
        a = line[9:].split(",")
        c = resolve(a[0])
        if c is None: w("TACHDUMP=ERR,channel"); return
        dump(c, int(a[1]) if len(a) > 1 else 1000, int(a[2]) if len(a) > 2 else 1000, "slow"); return
    if line.startswith("TACHFAST="):
        c = resolve(line[9:])
        if c is None: w("TACHDUMP=ERR,channel"); return
        dump(c, 50, 1000, "fast"); return
    if line.startswith("STATUS?"): status()
    elif line.startswith("TEMP="):
        temp_count += 1
        open("/tmp/emu_temp.log","a").write(line + "\n")
        for tok in line[5:].split(","):
            if ":" in tok:
                k, v = tok.split(":"); 
                if k in serial_vals: serial_vals[k] = float(v)
    elif line.startswith("MODE?"): w("MODE=%d" % mode)
    elif line.startswith("MODE="): mode = int(line[5:])
    elif line.startswith("THERMS?"):
        for t in TH:
            en, v, st = therm_state[t]; w("THERM=%s,%s,%s,%s" % (t, en, v, st))
        w("THERM=END")
    elif line.startswith("TACH?"):
        for c in CH:
            if c == "FUN_DC_1": w("TACH=%s,63,PWM,0" % c)
            else: w("TACH=%s,-,NONE,0" % c)
        w("TACH=END")
    elif line.startswith("TACHRAW="):
        w("TACHRAW=%s,0,12980,12990,13001,12985,12991,12988,12999" % line[8:])
    elif line.startswith("TACHTEST"):
        if tachtest_at is None:
            w("TACHTEST=START"); tachtest_at = time.time() + 2.5
    elif line.startswith("SET="):
        a = line[4:].split(",")
        ch = int(a[0]); pct[CH[ch]] = max(0, min(100, int(a[1])))
    elif line.startswith("CURVES?"):
        for c in CH:
            for i,(t,p) in enumerate(curves[c]): w("CURVE=%s,%d,%d,%d" % (c,i,t,p))
        w("CURVE=END")
    elif line.startswith("CURVE="):
        a = line[6:]
        if a == "SAVE": w("CURVE=SAVED"); return
        if a == "RESET":
            curves = {c: list(CURVE_A if "PWM" in c else CURVE_B) for c in CH}
            curves["FUN_PWM_0"] = [(30,20),(50,35),(65,55),(75,80),(85,100)]
            w("CURVE=RESET_OK"); return
        p = a.split(",")
        if len(p) == 4 and corrupt_left > 0:       # тест: "потеря символа" - плата получает усечённый процент
            corrupt_left -= 1; p[3] = p[3][:-1] or "0"
        if len(p) == 4 and p[0] in curves and 0 <= int(p[1]) < 5:
            curves[p[0]][int(p[1])] = (int(p[2]), max(0,min(100,int(p[3]))))
            w("CURVE=OK,%s,%s,%s,%s" % (p[0], p[1], curves[p[0]][int(p[1])][0], curves[p[0]][int(p[1])][1]))
    elif line.startswith("BINDS?"):
        for c in CH:
            lk = "LOCKED" if (mode == 0 and c in locked) else "FREE"
            w("BIND=%s,%s,%s,%s" % (c, binds[c][0], binds[c][1], lk))
        w("BIND=END")
    elif line.startswith("BIND="):
        a = line[5:]
        if a == "SAVE": w("BIND=SAVED"); return
        if a == "RESET": w("BIND=RESET_OK"); return
        p = a.split(",")
        if len(p) == 3 and p[0] in binds:
            if mode == 0 and p[0] in locked: w("BIND=LOCKED," + p[0]); return
            if (p[1] == "THERM" and p[2] in TH) or (p[1] == "SERIAL" and p[2] in SER):
                binds[p[0]] = (p[1], p[2]); w("BIND=OK," + p[0])

buf = b""
open("/tmp/emu_ready","w").write("1")
while True:
    if time.time() >= next_hb:
        w("EVENT=HB,%d" % int(time.time() - t_start)); next_hb = time.time() + HB_PERIOD
    for c, (p, until) in list(holds.items()):
        if time.time() >= until:
            holds.pop(c); w("EVENT=HOLD,%s,END" % c)
    if tachtest_at is not None and time.time() >= tachtest_at:
        for i in TACH_NEEDS_FULL:
            c = CH[i]; t = TACHTEST_RESULT[c]
            w("TACHTEST=%s,%s,%s,%d" % (c, t[0], t[1], t[2]))
        w("TACHTEST=END"); tachtest_at = None
    r,_,_ = select.select([fd],[],[],0.2)
    if r:
        try: d = os.read(fd, 4096)
        except OSError: time.sleep(0.1); continue
        if not d: time.sleep(0.05); continue
        buf += d
        while b"\n" in buf:
            ln, buf = buf.split(b"\n", 1)
            try: handle(ln.decode(errors="ignore"))
            except Exception as ex: sys.stderr.write("ERR %s\n" % ex)
