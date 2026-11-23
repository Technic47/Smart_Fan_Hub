using System;
using System.Collections.Generic;
using System.Threading;
using MksFanHub;

static class LinkTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static Func<string, bool> Ends(string prefix) { return delegate (string x) { return x.StartsWith(prefix, StringComparison.Ordinal); }; }

    static int CountLines(string path, string startsWith)
    {
        try
        {
            int n = 0;
            foreach (string l in System.IO.File.ReadAllLines(path))
            {
                string t = l; int sp = t.IndexOf(' ');
                if (startsWith == "STATUS?") { if (sp > 0 && t.Substring(sp + 1).StartsWith("STATUS?")) n++; }
                else if (t.StartsWith(startsWith)) n++;
            }
            return n;
        }
        catch { return 0; }
    }

    static bool WaitFor(Func<bool> c, int ms) { DateTime u = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < u) { if (c()) return true; Thread.Sleep(50); } return false; }

    static int Main(string[] args)
    {
        Settings s = new Settings();
        s.ComPort = args[0];
        s.PollMs = 300; s.StatusMs = 500;
        s.LightStatusMs = 3000; s.LightPollMs = 1500;
        LhmSensorSource sensors = new LhmSensorSource();
        HubLink link = new HubLink(s, sensors);
        List<string> events = new List<string>();
        link.BoardEvent += delegate (string e) { lock (events) { events.Add(e); } };
        link.PortFactory = delegate (string n) { return new FilePortLink(n); };
        List<string> traffic = new List<string>();
        link.Traffic += delegate (bool o, string l) { lock (traffic) { traffic.Add((o ? "> " : "") + l); } };
        link.Start();

        Check(WaitFor(delegate { return link.State == ConnState.Connected && link.Last != null; }, 15000), "подключение к эмулятору и первый STATUS");
        if (fails > 0) return 1;
        Check(link.Last.Mode == "TMP-SRL" && link.Last.Channels.Count == 10, "STATUS: режим и 10 каналов");

        List<string> lines; string err;
        // CURVES?
        Check(link.Query("CURVES?", Ends("CURVE=END"), 3000, out lines, out err), "CURVES? ответил");
        Dictionary<string, CurvePoint[]> cur = ProtocolParsers.ParseCurves(lines);
        Check(cur.Count == 10 && cur["FUN_PWM_1"].Length == 5 && cur["FUN_PWM_1"][2].Temp == 42 && cur["FUN_PWM_1"][2].Percent == 40, "кривые разобраны: 10 каналов x 5 точек");

        // CURVE= (правка точки)
        Check(link.Query("CURVE=FUN_PWM_1,2,45,50", Ends("CURVE=OK,FUN_PWM_1,2,"), 1500, out lines, out err), "CURVE= подтверждён");
        link.Query("CURVES?", Ends("CURVE=END"), 3000, out lines, out err);
        cur = ProtocolParsers.ParseCurves(lines);
        Check(cur["FUN_PWM_1"][2].Temp == 45 && cur["FUN_PWM_1"][2].Percent == 50, "правка точки видна в CURVES?");
        Check(link.Query("CURVE=SAVE", Ends("CURVE=SAVED"), 2000, out lines, out err), "CURVE=SAVE подтверждён");

        // BIND в TMP-SRL: заблокировано для вдува
        // --- DCFREQ (прошивка 0.3.22) ---
        Check(link.Query("DCFREQ?", Ends("DCFREQ=END"), 1500, out lines, out err) && lines.Count == 5 && lines[0].StartsWith("DCFREQ=FUN_DC_") && lines[0].EndsWith(",31,129"), "DCFREQ? 4 канала по 31 Гц/129 тиков");
        Check(link.Query("DCFREQ=FUN_DC_1,100", delegate (string x) { return x.StartsWith("DCFREQ="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "DCFREQ=OK,FUN_DC_1,100,40", "DCFREQ=канал,100 -> 100 Гц, 40 тиков");
        Check(link.Query("DCFREQ=FUN_DC_1,5", delegate (string x) { return x.StartsWith("DCFREQ="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "DCFREQ=ERR,range", "DCFREQ вне диапазона -> ERR,range");
        Check(link.Query("DCFREQ=FUN_PWM_1,50", delegate (string x) { return x.StartsWith("DCFREQ="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "DCFREQ=ERR,channel", "DCFREQ для 4-pin -> ERR,channel");
        Check(link.Query("DCFREQ=RESET", delegate (string x) { return x.StartsWith("DCFREQ="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "DCFREQ=RESET_OK", "DCFREQ=RESET");
        Check(link.Query("SPIN?", Ends("SPIN=END"), 1500, out lines, out err) && lines.Count == 6 && lines[4] == "SPIN=PARAM,5,5,1000,60,3", "SPIN? 4 канала + параметры");
        Check(link.Query("SPIN=FUN_DC_2,42", delegate (string x) { return x.StartsWith("SPIN="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "SPIN=OK,FUN_DC_2,42", "SPIN=канал,% подтверждён");
        Check(link.Query("SPIN=PARAM,5,5,1000,60,3", delegate (string x) { return x.StartsWith("SPIN="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "SPIN=OK,PARAM", "SPIN=PARAM подтверждён");
        Check(link.Query("SPIN=RESET", delegate (string x) { return x.StartsWith("SPIN="); }, 1500, out lines, out err) && lines[lines.Count - 1].Trim() == "SPIN=RESET_OK", "SPIN=RESET");
        // --- CurveDialog.Apply: проверка отправки (эмулятор искажает первые 2 точки: усечённый процент) ---
        {
            link.ConsoleSend("EMU=CORRUPT,2", out err);
            Thread.Sleep(300);
            Usage ucd = new Usage(s);
            CurveDialog cd = new CurveDialog(link, ucd, s, "FUN_PWM_1");
            System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(CurveDialog).GetMethod("LoadFromBoard", bf).Invoke(cd, null);
            var wk = (Dictionary<string, CurvePoint[]>)typeof(CurveDialog).GetField("work", bf).GetValue(cd);
            wk["FUN_PWM_1"][1].Percent = 77; wk["FUN_PWM_1"][3].Percent = 88;
            typeof(CurveDialog).GetMethod("Apply", bf).Invoke(cd, new object[] { false });
            link.Query("CURVES?", Ends("CURVE=END"), 3000, out lines, out err);
            string joined = string.Join("|", lines.ToArray());
            Check(joined.Contains("CURVE=FUN_PWM_1,1,") && joined.Contains(",77") && joined.Contains(",88"), "CurveDialog.Apply: искажённые при передаче точки доотправлены и на плате верные (77/88)");
            cd.Dispose();
        }
        Check(link.Query("BINDS?", Ends("BIND=END"), 3000, out lines, out err), "BINDS? ответил");
        List<BindInfo> binds = ProtocolParsers.ParseBinds(lines);
        Check(binds.Count == 10 && binds[0].Locked && binds[0].SourceType == "SERIAL" && binds[0].SourceName == "P102", "привязки разобраны, GPU зафиксирован");
        Check(link.Query("BIND=FUN_PWM_1,THERM,EXT", delegate (string x) { return x.StartsWith("BIND=OK,FUN_PWM_1") || x.StartsWith("BIND=LOCKED,FUN_PWM_1"); }, 1500, out lines, out err)
              && lines[lines.Count - 1].StartsWith("BIND=LOCKED"), "BIND вдува в TMP-SRL -> LOCKED");

        // свободный канал в TMP-SRL
        Check(link.Query("BIND=FUN_PWM_2,THERM,TH1", Ends("BIND=OK,FUN_PWM_2"), 1500, out lines, out err), "BIND свободного канала -> OK");

        // MODE=1 без ответа, потом привязка вдува
        Check(link.Send("MODE=1", out err), "MODE=1 отправлен");
        Check(WaitFor(delegate { return link.Last != null && link.Last.Mode == "TMP-MAN"; }, 5000), "STATUS показал TMP-MAN");
        Check(link.Query("BIND=FUN_PWM_1,THERM,EXT", Ends("BIND=OK,FUN_PWM_1"), 1500, out lines, out err), "BIND вдува в TMP-MAN -> OK");
        link.Query("BINDS?", Ends("BIND=END"), 3000, out lines, out err);
        binds = ProtocolParsers.ParseBinds(lines);
        Check(binds[1].SourceName == "EXT" && !binds[1].Locked, "привязка вдува теперь EXT, не зафиксирована");

        // ответ на неизвестную команду - таймаут, но дальше всё работает
        DateTime t0 = DateTime.Now;
        bool r = link.Query("BOGUS?", Ends("NEVER"), 700, out lines, out err);
        double dt = (DateTime.Now - t0).TotalMilliseconds;
        Check(!r && dt < 2500 && err != null, "запрос без ответа завершается таймаутом (" + (int)dt + " мс): " + err);
        Check(link.Query("CURVES?", Ends("CURVE=END"), 3000, out lines, out err), "после таймаута запросы работают");

        // параллельные запросы из двух потоков под фоновым потоком STATUS
        int okCount = 0; object lk = new object();
        ThreadStart work = delegate {
            for (int i = 0; i < 15; i++)
            {
                List<string> ls; string e2;
                bool a = link.Query("BINDS?", Ends("BIND=END"), 3000, out ls, out e2);
                bool b = a && ProtocolParsers.ParseBinds(ls).Count == 10;
                if (b) lock (lk) okCount++;
            }
        };
        Thread t1 = new Thread(work), t2 = new Thread(work);
        t1.Start(); t2.Start(); t1.Join(); t2.Join();
        Check(okCount == 30, "30 параллельных запросов BINDS? без потерь (" + okCount + "/30)");

        // ---- консоль: трафик, быстрые запросы, TACHTEST ----
        Thread.Sleep(2500);   // фоновый опрос идёт, но в консоль попадать не должен
        lock (traffic)
        {
            bool noise = false;
            foreach (string l in traffic) if (l.StartsWith("STATUS=") || l.StartsWith("> TEMP=") || l.StartsWith("> STATUS?")) noise = true;
            Check(!noise, "фоновые STATUS=/TEMP=/STATUS? не попадают в консоль");
            traffic.Clear();
        }
        Check(link.ConsoleSend("THERMS?", out err), "ConsoleSend THERMS?");
        Check(WaitFor(delegate { lock (traffic) { return traffic.Contains("THERM=END"); } }, 3000), "ответ THERMS? дошёл до консоли");
        lock (traffic) { Check(traffic.Contains("> THERMS?") && traffic.Contains("THERM=CASE,1,27.4,OK"), "консоль показывает команду и строки ответа"); traffic.Clear(); }
        Check(link.ConsoleSend("STATUS?", out err), "ConsoleSend STATUS?");
        Check(WaitFor(delegate { lock (traffic) { return traffic.Contains("STATUS=END"); } }, 3000), "ручной STATUS? показывается в консоли");
        Thread.Sleep(3500);
        int statusAfter; lock (traffic) { traffic.Clear(); } Thread.Sleep(1500); lock (traffic) { statusAfter = traffic.FindAll(delegate (string l) { return l.StartsWith("STATUS="); }).Count; }
        Check(statusAfter == 0, "после окна ручного STATUS? фоновые STATUS= снова скрыты");
        lock (traffic) { traffic.Clear(); }
        Check(link.ConsoleSend("TACHTEST", out err), "ConsoleSend TACHTEST");
        Check(WaitFor(delegate { lock (traffic) { return traffic.Contains("TACHTEST=END"); } }, 10000), "TACHTEST дошёл до END");
        lock (traffic)
        {
            int rows = traffic.FindAll(delegate (string l) { return l.StartsWith("TACHTEST=FUN_"); }).Count;
            Check(traffic.Contains("TACHTEST=START") && rows == 4 && traffic.Contains("TACHTEST=FUN_DC_1,63,OK,1110"), "TACHTEST: START, 4 канала с результатами, END");
        }
        Check(link.ConsoleSend("SET=1,60", out err), "SET= отправляется без ответа");

        // температуры уходят в плату
        Check(WaitFor(delegate { return link.LastSent.ContainsKey("P102"); }, 3000), "TEMP= отправляется (P102)");

        // ---- v1.3: события платы, пульс, свёрнутый режим, сон, HOLD, TACHDUMP ----
        Check(WaitFor(delegate { return link.HbSeen; }, 4000), "пульс EVENT=HB замечен (HbSeen)");
        lock (traffic) { traffic.Clear(); }
        lock (events) { events.Clear(); }
        Check(link.ConsoleSend("EMU=FAILSAFE,ON", out err), "тестовый хук отправлен");
        Check(WaitFor(delegate { lock (events) { return events.Contains("FAILSAFE,ON"); } }, 3000), "BoardEvent: FAILSAFE,ON получен");
        Thread.Sleep(2300);
        lock (events) { Check(!events.Exists(delegate (string e) { return e.StartsWith("HB"); }), "пульс HB не попадает в BoardEvent"); }
        lock (traffic)
        {
            Check(traffic.Contains("EVENT=FAILSAFE,ON") && !traffic.Exists(delegate (string l) { return l.StartsWith("EVENT=HB"); }), "в консоль идут события, но не пульс");
        }
        // события посреди ответа не попадают в ответ на запрос
        int polluted = 0;
        for (int i = 0; i < 6; i++)
        {
            link.ConsoleSend("EMU=BTN,SHORT", out err);
            List<string> ls;
            if (link.Query("CURVES?", Ends("CURVE=END"), 3000, out ls, out err) && ls.Exists(delegate (string l) { return l.StartsWith("EVENT="); })) polluted++;
        }
        Check(polluted == 0, "строки EVENT= не попадают в ответы на запросы");

        // свёрнутый режим: меньше STATUS? и TEMP=
        link.SetLight(false);
        Thread.Sleep(600);
        try { System.IO.File.Delete("/tmp/emu_cmds.log"); System.IO.File.Delete("/tmp/emu_temp.log"); } catch { }
        Thread.Sleep(5000);
        int stNormal = CountLines("/tmp/emu_cmds.log", "STATUS?"), tpNormal = CountLines("/tmp/emu_temp.log", "TEMP=");
        link.SetLight(true);
        Thread.Sleep(500);
        try { System.IO.File.Delete("/tmp/emu_cmds.log"); System.IO.File.Delete("/tmp/emu_temp.log"); } catch { }
        Thread.Sleep(7000);
        int stLight = CountLines("/tmp/emu_cmds.log", "STATUS?"), tpLight = CountLines("/tmp/emu_temp.log", "TEMP=");
        Console.WriteLine("  обычный режим за 5 с: STATUS? " + stNormal + ", TEMP= " + tpNormal + "; свёрнутый за 7 с: STATUS? " + stLight + ", TEMP= " + tpLight);
        Check(stNormal >= 6 && stLight <= 4 && stLight < stNormal, "свёрнутый режим: STATUS? реже");
        Check(tpNormal >= 10 && tpLight <= 6 && tpLight >= 2, "свёрнутый режим: TEMP= реже, но идёт (watchdog платы не заснёт)");
        Check(link.State == ConnState.Connected && (DateTime.Now - link.LastStatusAt).TotalSeconds < link.StaleSeconds, "в свёрнутом режиме связь не считается потерянной");
        // событие в свёрнутом режиме вызывает немедленный STATUS?
        DateTime before = link.LastStatusAt;
        Thread.Sleep(300);
        before = link.LastStatusAt;
        link.ConsoleSend("EMU=MODE,0", out err);
        Check(WaitFor(delegate { return link.LastStatusAt > before; }, 1500), "событие в свёрнутом режиме -> немедленный STATUS?");
        // выход из свёрнутого режима -> немедленный STATUS?
        Thread.Sleep(300);
        before = link.LastStatusAt;
        link.SetLight(false);
        Check(WaitFor(delegate { return link.LastStatusAt > before; }, 1500), "выход из свёрнутого режима -> немедленный STATUS?");

        // HOLD
        Check(link.Query("HOLD=FUN_DC_1,50,20", Ends("HOLD="), 1500, out lines, out err) && lines[lines.Count - 1] == "HOLD=OK,FUN_DC_1,50,20", "HOLD= принят");
        Check(link.Query("HOLD?", Ends("HOLD=END"), 1500, out lines, out err) && (lines.Contains("HOLD=FUN_DC_1,50,19") || lines.Contains("HOLD=FUN_DC_1,50,20")), "HOLD? показывает удержание");
        Check(link.Query("HOLD=ALL,OFF", Ends("HOLD="), 1500, out lines, out err), "HOLD=ALL,OFF");
        lock (events) { events.Clear(); }
        link.Query("HOLD=FUN_DC_0,40,1", Ends("HOLD="), 1500, out lines, out err);
        Check(WaitFor(delegate { lock (events) { return events.Contains("HOLD,FUN_DC_0,END"); } }, 4000), "по таймауту приходит EVENT=HOLD,...,END");

        // во время ответа на TACHDUMP фоновые TEMP= не отправляются (плата в это время не читает Serial)
        link.ConsoleSend("EMU=SLOW,1.5", out err);
        Thread.Sleep(300);
        List<string> dl0 = null; string de0 = null; bool dq = false;
        Thread qt = new Thread(delegate () { dq = link.Query("TACHDUMP=FUN_DC_1", Ends("TACHDUMP=END"), 8000, out dl0, out de0); });
        qt.Start();
        Thread.Sleep(400);                       // запрос уже ушёл, плата "занята"
        DateTime sentAtStart = link.LastSentAt;
        Thread.Sleep(800);                       // всё ещё внутри ответа (плата отвечает ~1.5 с)
        bool quietHeld = link.LastSentAt == sentAtStart;
        qt.Join();
        Check(dq && dl0 != null && dl0.Count == 27, "TACHDUMP: ответ целиком (27 строк)");
        Check(quietHeld, "пока плата занята TACHDUMP, TEMP= не уходят в плату");
        Check(WaitFor(delegate { return link.LastSentAt > sentAtStart; }, 2000), "после ответа TEMP= снова идут");
        link.ConsoleSend("EMU=SLOW,0", out err);

        // TACHDUMP через диагностику
        string dumpFile = "/tmp/traybuild/tachdump_test.log";
        try { System.IO.File.Delete(dumpFile); } catch { }
        TachDiag.PathOverride = dumpFile;
        string derr;
        List<string> prog = new List<string>();
        bool capOk = TachDiag.Capture(link, "FUN_DC_1", false, "тест: крутится", new int[] { 30, 100 }, 0.3,
            delegate (string m) { prog.Add(m); }, delegate { return false; }, out derr);
        Check(capOk && derr == null, "диагностика: Capture завершился без ошибок " + derr);
        string[] dl = System.IO.File.Exists(dumpFile) ? System.IO.File.ReadAllLines(dumpFile) : new string[0];
        int sessions = 0, captures = 0, drows = 0, begins = 0, ends = 0;
        foreach (string l in dl)
        {
            if (l.StartsWith("### SESSION") && l.Contains("channel=FUN_DC_1") && l.Contains("state=RUN")) sessions++;
            if (l.StartsWith("### CAPTURE duty=")) captures++;
            if (l.StartsWith("TACHDUMP=D,")) drows++;
            if (l.StartsWith("TACHDUMP=BEGIN,")) begins++;
            if (l.StartsWith("TACHDUMP=END,")) ends++;
        }
        Check(sessions == 1 && captures == 4 && begins == 4 && ends == 4 && drows == 100, "файл дампа: 1 сессия, 4 записи (30%/100% x медленная/быстрая), 100 строк данных (" + sessions + "/" + captures + "/" + begins + "/" + ends + "/" + drows + ")");
        Check(dl.Length > 0 && System.Array.Exists(dl, delegate (string l) { return l.StartsWith("### CAPTURE duty=30 kind=SLOW fw_tach=TACH=FUN_DC_1,63,PWM,0"); }), "в файле рядом записан tach, который видит прошивка");
        Check(link.Query("HOLD?", Ends("HOLD=END"), 1500, out lines, out err) && lines.Count == 1, "после диагностики удержание снято");
        bool capBad = TachDiag.Capture(link, "FUN_NOPE", true, "", new int[] { 50 }, 0.1, delegate (string m) { }, delegate { return false; }, out derr);
        Check(!capBad && derr != null && derr.Contains("не приняла"), "диагностика: плохой канал -> понятная ошибка: " + derr);
        bool capCancel = TachDiag.Capture(link, "FUN_DC_1", true, "заблокирован", new int[] { 50 }, 0.1, delegate (string m) { }, delegate { return true; }, out derr);
        int cRun, cBlk; TachDiag.CountSessions(out cRun, out cBlk);
        Check(capCancel && cRun == 1 && cBlk == 0, "диагностика: неудачные и отменённые запуски в файл не попадают; сессий " + cRun + "/" + cBlk + " (крутится/заблокирован)");
        TachDiag.PathOverride = null;

        // сон Windows: пауза -> порт закрыт, возобновление -> связь и сброс датчиков
        int resetsBefore = sensors.Resets;
        link.Pause();
        Check(WaitFor(delegate { return link.State != ConnState.Connected; }, 4000), "Pause: связь закрыта");
        Check(!link.Query("CURVES?", Ends("CURVE=END"), 500, out lines, out err) && err != null, "Pause: запросы сразу отвечают ошибкой");
        Thread.Sleep(2000);
        Check(link.State != ConnState.Connected && link.IsPaused, "Pause: плату не ищем, пока спим");
        link.Resume();
        Check(WaitFor(delegate { return link.State == ConnState.Connected && link.Last != null; }, 25000), "Resume: связь восстановилась");
        Check(WaitFor(delegate { return sensors.Resets > resetsBefore; }, 3000), "Resume: датчики ПК переоткрыты");
        Check(link.Query("CURVES?", Ends("CURVE=END"), 2000, out lines, out err), "Resume: запросы снова работают");

        // смена порта: переподключение
        link.RequestReconnect();
        Check(WaitFor(delegate { return link.State == ConnState.Searching || link.State == ConnState.Connected; }, 3000), "RequestReconnect отработал");
        Check(WaitFor(delegate { return link.State == ConnState.Connected && link.Query("CURVES?", Ends("CURVE=END"), 1500, out lines, out err); }, 15000), "после переподключения связь восстановилась");

        Console.WriteLine("EMU_STATE_DONE");
        if (args.Length > 1 && args[1] == "wait-kill")
        {
            // эмулятор будет убит снаружи: проверяем реакцию
            Console.WriteLine("WAIT_KILL");
            Thread.Sleep(100);
            Check(WaitFor(delegate { return link.State != ConnState.Connected; }, 20000), "обрыв связи обнаружен");
            Check(!link.Query("CURVES?", Ends("CURVE=END"), 1000, out lines, out err) && err != null, "Query без связи сразу возвращает ошибку: " + err);
        }
        link.Stop();
        Console.WriteLine(fails == 0 ? "ALL OK" : fails + " FAILED");
        return fails;
    }
}

