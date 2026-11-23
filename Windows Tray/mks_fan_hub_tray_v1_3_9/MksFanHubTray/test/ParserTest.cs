using System;
using System.Collections.Generic;
using MksFanHub;

static class ParserTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static int Main()
    {
        // Реальный формат dumpStatus() из прошивки v0.3.14
        string[] lines = new string[] {
            "STATUS=MODE,TMP-SRL",
            "STATUS=SERIAL,P102,45.5",
            "STATUS=SERIAL,GTX,0.0",
            "STATUS=SERIAL,CPU,38.0",
            "STATUS=SERIAL,SSD,31.0",
            "STATUS=THERM,CASE,1,27.4,OK",
            "STATUS=THERM,TH1,1,NaN,ABSENT",
            "STATUS=THERM,TB,1,NaN,FAULT",
            "STATUS=THERM,EXT,0,NaN,OFF",
            "STATUS=CH,FUN_PWM_0,35,THERM,CASE,FREE,ACTIVE",
            "STATUS=CH,FUN_DC_0,55,SERIAL,P102,LOCKED,ACTIVE",
            "STATUS=CH,FUN_DC_1,20,THERM,TB,FREE,INACTIVE",
            "STATUS=TACH,FUN_PWM_0,18,OK,1140",
            "STATUS=TACH,FUN_DC_0,51,PWM,0",
            "STATUS=TACH,FUN_DC_1,63,STALL,0",
            "STATUS=WATCHDOG,OK",
            "STATUS=END"
        };
        StatusParser p = new StatusParser();
        HubState st = null;
        foreach (string l in lines) { HubState r = p.Feed(l); if (r != null) st = r; }
        // мусор между блоками не ломает разбор
        Check(p.Feed("TACH=END") == null, "посторонняя строка игнорируется");
        Check(p.Feed("") == null && p.Feed(null) == null, "пустые строки игнорируются");

        Check(st != null, "блок собран по STATUS=END");
        Check(st.Mode == "TMP-SRL", "режим");
        Check(st.SerialValues.Count == 4 && st.SerialValues[0].Value == 45.5f, "serial-значения");
        Check(st.Therms.Count == 4, "4 термистора");
        Check(st.Therms[0].Value.HasValue && Math.Abs(st.Therms[0].Value.Value - 27.4f) < 0.01f, "CASE 27.4");
        Check(!st.Therms[1].Value.HasValue && st.Therms[1].State == "ABSENT", "NaN -> null");
        Check(st.Therms[2].State == "FAULT" && st.Therms[3].Enabled == false, "FAULT / выключен");
        Check(st.Channels.Count == 3 && st.Channels[1].Locked && st.Channels[1].SourceName == "P102", "каналы");
        Check(!st.Channels[2].Active, "INACTIVE");
        Check(st.Tachs.Count == 3 && st.FindTach("FUN_PWM_0").Rpm == 1140, "тахо RPM");
        Check(st.FindTach("FUN_DC_1").State == "STALL", "тахо STALL");
        Check(!st.WatchdogTripped, "watchdog OK");

        // второй блок независим от первого
        p.Feed("STATUS=MODE,TMP-MAN"); p.Feed("STATUS=WATCHDOG,TRIPPED");
        HubState st2 = p.Feed("STATUS=END");
        Check(st2.Mode == "TMP-MAN" && st2.WatchdogTripped && st2.Therms.Count == 0, "второй блок чистый, FAILSAFE");

        Settings cfg = new Settings(); Usage usage = new Usage(cfg);
        // Health
        var sent = new Dictionary<string, float>(); sent["P102"] = 45f;
        HealthResult h = HealthEvaluator.Evaluate(ConnState.Connected, st, DateTime.Now, sent, usage);
        Check(h.Level == Health.Ok, "Health: FAULT пустого (не подключённого) термистора TB НЕ даёт предупреждения (" + h.Reason + ")");
        usage.SetConn(Usage.ThKey("TB"), 1);
        h = HealthEvaluator.Evaluate(ConnState.Connected, st, DateTime.Now, sent, usage);
        Check(h.Level == Health.Warn && h.Reason.Contains("датчик TB потерян"), "Health: WARN по FAULT термистора, отмеченного как подключённый (" + h.Reason + ")");
        Check(!h.Reason.Contains("FUN_DC_1"), "неактивный канал со STALL не считается");
        usage.SetConn(Usage.ThKey("TB"), 0);
        h = HealthEvaluator.Evaluate(ConnState.Connected, st2, DateTime.Now, sent, usage);
        Check(h.Level == Health.Bad && h.Reason.StartsWith("FAILSAFE"), "Health: BAD при FAILSAFE");
        h = HealthEvaluator.Evaluate(ConnState.Lost, st, DateTime.Now, sent, usage);
        Check(h.Level == Health.Bad, "Health: BAD при потере связи");
        h = HealthEvaluator.Evaluate(ConnState.Connected, st, DateTime.Now.AddSeconds(-20), sent, usage);
        Check(h.Level == Health.Bad && h.Reason.Contains("не отвечает"), "Health: BAD при устаревших данных");
        var empty = new Dictionary<string, float>();
        HubState ok = new HubState(); ok.Mode = "TMP-SRL";
        ChannelInfo ci = new ChannelInfo(); ci.Name = "X"; ci.SourceType = "SERIAL"; ci.SourceName = "P102"; ci.Active = true; ok.Channels.Add(ci);
        ok.SerialValues.Add(new KeyValuePair<string, float>("P102", 0f));
        usage.SetConn(Usage.ChKey("X"), 1);   // канал отмечен как подключённый
        h = HealthEvaluator.Evaluate(ConnState.Connected, ok, DateTime.Now, empty, usage);
        Check(h.Level == Health.Warn && h.Reason.Contains("P102"), "Health: WARN если нет температуры P102");
        h = HealthEvaluator.Evaluate(ConnState.Connected, ok, DateTime.Now, sent, usage);
        Check(h.Level == Health.Ok, "Health: OK");

        // nvidia-smi
        var g = new Dictionary<string, float>();
        NvidiaSmi.ParseGpuLines("0, NVIDIA P102-100, 47\n1, NVIDIA GeForce GTX 1060 6GB, 39\n", g);
        Check(g["P102"] == 47f && g["GTX"] == 39f, "разбор nvidia-smi");

        // ---- Usage: что считается подключённым ----
        Settings s2 = new Settings(); Usage u2 = new Usage(s2);
        ThermInfo thOk = st.Therms[0], thFault = st.Therms[2], thAbs = st.Therms[1];
        Check(!u2.ThermUsed(thOk), "авто: термистор OK, но стабильности ещё нет -> не подключён (защита от одного моргнувшего отсчёта)");
        Check(!u2.ThermUsed(thFault) && !u2.ThermUsed(thAbs), "авто: пустые термисторы не подключены");
        u2.SetName(Usage.ThKey("CASE"), "Корпус");
        Check(u2.ThermUsed(thOk) && u2.ThermShort("CASE") == "Корпус" && u2.ThermTitle("CASE") == "Корпус", "переименование = подключён, имя применяется");
        Check(u2.ThermTitle("TH1") == "Термистор TH1" && u2.ThermShort("TH1") == "TH1", "стандартные подписи: 'Термистор X'");
        u2.SetConn(Usage.ThKey(thFault.Name), 1);
        Check(u2.ThermUsed(thFault), "ручная отметка 'подключён' работает");
        u2.SetConn(Usage.ThKey("CASE"), 2);
        Check(!u2.ThermUsed(thOk), "ручная отметка 'не подключён' сильнее имени");
        u2.SetConn(Usage.ThKey("CASE"), 0);
        u2.SetName(Usage.ThKey("CASE"), "");
        Check(!u2.HasCustomName(Usage.ThKey("CASE")), "пустое имя возвращает стандартное");
        ChannelInfo c0 = st.Channels[0]; TachInfo t0 = st.FindTach(c0.Name);
        Check(!u2.ChannelUsed(c0, t0), "авто: канал без своего имени и без стабильного тахо не подключён");
        u2.SetName(Usage.ChKey(c0.Name), "P102 GPU");
        Check(u2.ChannelUsed(c0, t0) && u2.ChannelName(c0.Name) == "P102 GPU", "канал с именем считается подключённым");
        Check(!u2.ShouldShow(false, UnusedMode.Hide) && u2.ShouldShow(false, UnusedMode.Dim) && u2.ShouldShow(false, UnusedMode.AsIs) && u2.ShouldShow(true, UnusedMode.Hide), "режимы показа: скрыть / серым / как есть");
        u2.ShowAllTemporarily = true;
        Check(u2.ShouldShow(false, UnusedMode.Hide) && u2.IsDimmed(false, UnusedMode.Hide), "'показать скрытые' временно показывает серым");
        u2.ShowAllTemporarily = false;

        // ---- Settings: сохранение/чтение (имена с кириллицей, = и ;) ----
        string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        string ini = System.IO.Path.Combine(dir, "MksFanHubTray.ini");
        if (System.IO.File.Exists(ini)) System.IO.File.Delete(ini);
        Settings s3 = new Settings();
        s3.UnusedFans = UnusedMode.Dim; s3.UnusedTherms = UnusedMode.AsIs; s3.PollMs = 2500; s3.ComPort = "COM7";
        s3.Names["CH:FUN_DC_0"] = "Выдув 2,3 = нормально";
        s3.Conn["TH:EXT"] = 2; s3.Seen.Add("CH:FUN_PWM_1"); s3.Seen.Add("TH:CASE");
        s3.ShowLog = false; s3.NotifyWarnings = false;
        s3.Save();
        Settings s4 = Settings.Load();
        Check(s4.UnusedFans == UnusedMode.Dim && s4.UnusedTherms == UnusedMode.AsIs && s4.PollMs == 2500 && s4.ComPort == "COM7" && !s4.ShowLog && !s4.NotifyWarnings, "ini: простые настройки");
        Check(s4.Names["CH:FUN_DC_0"] == "Выдув 2,3 = нормально" && s4.Conn["TH:EXT"] == 2 && s4.Seen.Contains("CH:FUN_PWM_1") && s4.Seen.Contains("TH:CASE"), "ini: имена (юникод, '='), подключение, Seen");
        System.IO.File.Delete(ini);

        // ---- миграция имён DC-каналов (прошивка 0.3.17) ----
        Check(Settings.MapDcName("CH:FUN_DC_1") == "CH:FUN_DC_0" && Settings.MapDcName("CH:FUN_DC_2") == "CH:FUN_DC_1" &&
              Settings.MapDcName("CH:FUN_DC_3") == "CH:FUN_DC_2" && Settings.MapDcName("CH:FUN_DC_0") == "CH:FUN_DC_3" &&
              Settings.MapDcName("FUN_DC_1") == "FUN_DC_0" && Settings.MapDcName("CH:FUN_PWM_1") == "CH:FUN_PWM_1" && Settings.MapDcName("TH:CASE") == "TH:CASE", "MapDcName");
        System.IO.File.WriteAllLines(ini, new string[] {
            "ComPort=COM5", "Name.CH:FUN_DC_1=Exhaust 1", "Name.CH:FUN_DC_0=Exhaust 2,3", "Name.CH:FUN_PWM_1=Intake 1",
            "Conn.CH:FUN_DC_2=2", "Link.FUN_DC_3=A", "Seen=CH:FUN_DC_1;CH:FUN_PWM_0" });
        Settings sm5 = Settings.Load();
        Check(sm5.Names["CH:FUN_DC_0"] == "Exhaust 1" && sm5.Names["CH:FUN_DC_3"] == "Exhaust 2,3" && sm5.Names["CH:FUN_PWM_1"] == "Intake 1" &&
              sm5.Conn["CH:FUN_DC_1"] == 2 && sm5.CurveGroups["FUN_DC_2"] == "A" && sm5.Seen.Contains("CH:FUN_DC_0") && !sm5.Seen.Contains("CH:FUN_DC_1") && sm5.ComPort == "COM5", "ini старой схемы: имена/связи/подключение перенесены");
        Check(System.IO.File.Exists(ini + ".bak_1.3.2"), "ini: копия старого файла");
        Settings sm6 = Settings.Load();       // повторная загрузка - уже новая схема, второй раз не переносим
        Check(sm6.Names["CH:FUN_DC_0"] == "Exhaust 1" && sm6.Names["CH:FUN_DC_3"] == "Exhaust 2,3" && sm6.ChannelMap == Settings.ChannelMapCurrent, "ini: повторная загрузка не переносит второй раз");
        System.IO.File.Delete(ini); System.IO.File.Delete(ini + ".bak_1.3.2");

        // ---- порядок каналов: PWM по номеру, затем DC по номеру (прошивка отдаёт в порядке выходов) ----
        StatusParser sp9 = new StatusParser();
        HubState st9 = null;
        foreach (string l in new string[] {
            "STATUS=CH,FUN_PWM_0,10,THERM,CASE,FREE,ACTIVE", "STATUS=CH,FUN_PWM_1,10,THERM,CASE,FREE,ACTIVE",
            "STATUS=CH,FUN_DC_0,10,THERM,CASE,FREE,ACTIVE", "STATUS=CH,FUN_DC_3,10,THERM,CASE,FREE,ACTIVE",
            "STATUS=CH,FUN_DC_1,10,THERM,CASE,FREE,ACTIVE", "STATUS=CH,FUN_PWM_4,10,THERM,CASE,FREE,ACTIVE",
            "STATUS=CH,FUN_DC_2,10,THERM,CASE,FREE,ACTIVE", "STATUS=END" })
            { HubState r = sp9.Feed(l); if (r != null) st9 = r; }
        string ord = ""; foreach (ChannelInfo c9 in st9.Channels) ord += c9.Name.Substring(4) + " ";
        Check(ord == "PWM_0 PWM_1 PWM_4 DC_0 DC_1 DC_2 DC_3 ", "порядок каналов по названиям: " + ord);
        Check(st9.Channels[3].Index == 2 && st9.Channels[6].Index == 3, "Index (для SET=) сохраняет порядок выходов платы");

        // ---- разбор CURVES? / BINDS? ----
        var cl = new List<string>(new string[] { "CURVE=FUN_PWM_1,0,28,15", "CURVE=FUN_PWM_1,1,35,25", "CURVE=OK,FUN_PWM_1,1,35,25", "CURVE=FUN_PWM_1,4,55,100", "CURVE=FUN_DC_0,9,1,1", "мусор", "CURVE=END" });
        var cv = ProtocolParsers.ParseCurves(cl);
        Check(cv.Count == 1 && cv["FUN_PWM_1"][1].Temp == 35 && cv["FUN_PWM_1"][4].Percent == 100, "CURVES?: мусор, OK-ответы и индекс >4 игнорируются");
        var bl = new List<string>(new string[] { "BIND=FUN_PWM_0,SERIAL,P102,LOCKED", "BIND=OK,FUN_PWM_1", "BIND=LOCKED,FUN_PWM_1", "BIND=FUN_DC_1,THERM,EXT,FREE", "BIND=END" });
        var bv = ProtocolParsers.ParseBinds(bl);
        Check(bv.Count == 2 && bv[0].Locked && bv[1].SourceName == "EXT" && !bv[1].Locked, "BINDS?: служебные ответы пропускаются");

        // ---- v1.3: группы кривых ----
        Settings gs = new Settings();
        List<string> order = new List<string>(new string[] { "FUN_PWM_0", "FUN_PWM_1", "FUN_PWM_2", "FUN_DC_1", "FUN_DC_0" });
        string gid = CurveGroups.Link(gs, "FUN_PWM_1", new string[] { "FUN_PWM_2" });
        Check(gid == "A" && CurveGroups.GroupOf(gs, "FUN_PWM_2") == "A" && CurveGroups.Label(gs, "FUN_PWM_1") == "гр. A" && CurveGroups.Label(gs, "FUN_DC_0") == "—", "группы: Link, GroupOf, Label");
        string gid2 = CurveGroups.Link(gs, "FUN_DC_1", new string[] { "FUN_DC_0" });
        Check(gid2 == "B", "группы: вторая группа получает следующую букву");
        CurveGroups.Link(gs, "FUN_PWM_1", new string[] { "FUN_PWM_2", "FUN_DC_0" });
        CurveGroups.Cleanup(gs, order);
        Check(CurveGroups.GroupOf(gs, "FUN_DC_0") == "A" && CurveGroups.GroupOf(gs, "FUN_DC_1") == "A" && CurveGroups.GroupOf(gs, "FUN_PWM_2") == "A" && CurveGroups.GroupOf(gs, "FUN_PWM_1") == "A", "группы: связь с каналом из другой группы сливает группы, прежние соседи не теряются");
        CurveGroups.Unlink(gs, "FUN_DC_1");   // вернуть прежний состав для проверок ниже
        {
            Settings g3 = new Settings();
            CurveGroups.Link(g3, "FUN_PWM_0", new string[] { "FUN_PWM_1" });                    // гр. A: 0+1
            CurveGroups.Link(g3, "FUN_PWM_2", new string[] { "FUN_PWM_0" });                    // 2 присоединяется к A, 1 не теряется
            Check(CurveGroups.GroupOf(g3, "FUN_PWM_0") == "A" && CurveGroups.GroupOf(g3, "FUN_PWM_1") == "A" && CurveGroups.GroupOf(g3, "FUN_PWM_2") == "A" && g3.CurveGroups.Count == 3, "группы: три канала в одной группе (сценарий пользователя)");
            CurveGroups.Link(g3, "FUN_DC_0", new string[] { "FUN_DC_1" });                       // отдельная гр. B
            CurveGroups.Link(g3, "FUN_PWM_1", new string[] { "FUN_DC_1" });                      // слияние A и B -> всё в A
            Check(CurveGroups.GroupOf(g3, "FUN_DC_0") == "A" && CurveGroups.GroupOf(g3, "FUN_DC_1") == "A" && g3.CurveGroups.Count == 5, "группы: слияние двух групп");
        }
        var work = new Dictionary<string, CurvePoint[]>();
        foreach (string c in order) work[c] = new CurvePoint[] { new CurvePoint(30, 20), new CurvePoint(40, 40), new CurvePoint(50, 60), new CurvePoint(60, 80), new CurvePoint(70, 100) };
        work["FUN_PWM_1"][2] = new CurvePoint(50, 77);
        List<string> changedTo = CurveGroups.Propagate(gs, work, "FUN_PWM_1");
        Check(changedTo.Count == 2 && work["FUN_PWM_2"][2].Percent == 77 && work["FUN_DC_0"][2].Percent == 77 && work["FUN_PWM_0"][2].Percent == 60, "группы: правка уходит всем членам, остальные не тронуты");
        work["FUN_PWM_2"][2].Percent = 1;     // у разных каналов - независимые копии
        Check(work["FUN_PWM_1"][2].Percent == 77, "группы: кривые членов - независимые копии");
        var board = new Dictionary<string, CurvePoint[]>();
        foreach (string c in order) board[c] = CurveGroups.CloneCurve(work[c]);
        Check(CurveGroups.Diverged(gs, board).Count == 1 && CurveGroups.Diverged(gs, board)[0] == "A", "группы: расхождение на плате обнаружено");
        board["FUN_PWM_2"] = CurveGroups.CloneCurve(board["FUN_PWM_1"]); board["FUN_DC_0"] = CurveGroups.CloneCurve(board["FUN_PWM_1"]);
        Check(CurveGroups.Diverged(gs, board).Count == 0, "группы: одинаковые кривые - расхождения нет");
        CurveGroups.Unlink(gs, "FUN_DC_0");
        Check(CurveGroups.GroupOf(gs, "FUN_DC_0") == null && CurveGroups.GroupOf(gs, "FUN_PWM_1") == "A", "группы: Unlink одного члена");
        CurveGroups.Unlink(gs, "FUN_PWM_2");
        Check(gs.CurveGroups.Count == 0, "группы: когда остался один, группа распадается");
        CurveGroups.Link(gs, "FUN_PWM_1", new string[] { "FUN_PWM_2" });
        CurveGroups.Cleanup(gs, new string[] { "FUN_PWM_1" });
        Check(gs.CurveGroups.Count == 0, "группы: Cleanup убирает несуществующие каналы");

        // группы и свёрнутый режим в ini
        string ini2 = System.IO.Path.Combine(dir, "MksFanHubTray.ini");
        if (System.IO.File.Exists(ini2)) System.IO.File.Delete(ini2);
        Settings s5 = new Settings();
        s5.CurveGroups["FUN_PWM_1"] = "A"; s5.CurveGroups["FUN_PWM_2"] = "A";
        s5.LightMode = false; s5.LightStatusMs = 15000; s5.LightPollMs = 4000;
        s5.Save();
        Settings s6 = Settings.Load();
        Check(s6.CurveGroups["FUN_PWM_2"] == "A" && s6.CurveGroups.Count == 2 && !s6.LightMode && s6.LightStatusMs == 15000 && s6.LightPollMs == 4000, "ini: группы кривых и свёрнутый режим");
        System.IO.File.WriteAllText(ini2, "LightPollMs=9000\nLightStatusMs=100\n");
        Settings s7 = Settings.Load();
        Check(s7.LightPollMs == 3000 && s7.LightStatusMs == 10000 && s7.LightMode, "ini: недопустимые значения свёрнутого режима отбрасываются");
        System.IO.File.Delete(ini2);

        // ---- v1.3: события платы по-русски ----
        Settings s8 = new Settings();
        s8.Names["CH:FUN_DC_1"] = "Выдув 1"; s8.Names["TH:CASE"] = "Корпус";
        Usage u8 = new Usage(s8);
        Check(Ru.Event("HB,12", u8) == null, "событие HB не показывается");
        Check(Ru.Event("BOOT,0.3.15", u8) == "Плата перезагрузилась (прошивка 0.3.15)", "событие BOOT");
        Check(Ru.Event("FANFAIL,FUN_DC_1", u8).StartsWith("ОШИБКА: вентилятор «Выдув 1» не раскрутился"), "событие FANFAIL");
        Check(Ru.Event("STALL,FUN_DC_1,2", u8).Contains("неудачная попытка запуска 2"), "событие STALL с попыткой");
        Check(Ru.Event("FANOK,FUN_DC_1", u8).Contains("раскручен"), "событие FANOK");
        {
            StatusParser spP = new StatusParser(); HubState spS = null;
            string[] spL = { "STATUS=MODE,TMP-SRL", "STATUS=CH,FUN_DC_1,40,THERM,CASE,FREE,ACTIVE", "STATUS=SPIN,FUN_DC_1,ERR,3", "STATUS=SPIN,FUN_DC_0,OK,0", "STATUS=WATCHDOG,OK", "STATUS=END" };
            foreach (string l in spL) { HubState r = spP.Feed(l); if (r != null) spS = r; }
            Check(spS != null && spS.Spins.Count == 2 && spS.Spins[0].State == "ERR" && spS.Spins[0].Tries == 3, "STATUS=SPIN разобран");
        }
        Check(Ru.Event("KICK,FUN_DC_1", u8).StartsWith("Плата: DC-вентилятор «Выдув 1» запускается толчком"), "событие KICK");
        Check(Ru.Event("TACHGUARD,FUN_DC_1", u8).StartsWith("Плата: на тахо-входе «Выдув 1» шторм помех"), "событие TACHGUARD");
        Check(Ru.Event("BOOT,0.3.16,B", u8) == "Плата перезагрузилась (прошивка 0.3.16), причина: ПРОСАДКА ПИТАНИЯ (brown-out)", "BOOT с причиной B");
        Check(Ru.Event("BOOT,0.3.16,?", u8).EndsWith("неизвестна (загрузчик сбросил флаги)"), "BOOT с причиной ?");
        Check(Ru.Event("BOOT,0.3.16,PE", u8).EndsWith("включение питания, внешний сброс (USB/кнопка)"), "BOOT с двумя причинами");
        Check(Ru.Event("FAILSAFE,ON", u8).StartsWith("Плата: FAILSAFE —") && Ru.Event("FAILSAFE,OFF", u8).Contains("снят"), "события FAILSAFE ON/OFF");
        Check(Ru.Event("MODE,1", u8) == "Плата: режим TMP-MAN" && Ru.Event("MODE,0", u8) == "Плата: режим TMP-SRL", "событие MODE");
        Check(Ru.Event("BTN,LONG", u8).Contains("долгое") && Ru.Event("BTN,SHORT", u8).Contains("короткое"), "событие BTN");
        Check(Ru.Event("THERM,CASE,FAULT", u8) == "Термистор Корпус: ПОТЕРЯН", "событие THERM с пользовательским именем");
        Check(Ru.Event("HOLD,FUN_DC_1,END", u8).Contains("«Выдув 1»") && Ru.Event("HOLD,FUN_DC_1,FAILSAFE", u8).Contains("FAILSAFE"), "событие HOLD с именем канала");
        Check(Ru.Event("WHATEVER,1", u8) == "Событие платы: WHATEVER,1", "неизвестное событие показывается как есть");
        HubState stE = new HubState();
        ThermInfo tUsed = new ThermInfo(); tUsed.Name = "CASE"; tUsed.Enabled = true; tUsed.State = "OK"; tUsed.Value = 27f;
        ThermInfo tEmpty = new ThermInfo(); tEmpty.Name = "TH2"; tEmpty.Enabled = true; tEmpty.State = "FAULT";
        stE.Therms.Add(tUsed); stE.Therms.Add(tEmpty);
        Check(u8.EventRelevant("THERM,CASE,FAULT", stE) && !u8.EventRelevant("THERM,TH2,OK", stE) && u8.EventRelevant("BOOT,0.3.15", stE), "события пустых термисторов в журнал не пишутся");

        // ---- v1.3: список уровней диагностики ----
        string perr;
        int[] dd = TachDiag.ParseDuties("30, 50;70 100", out perr);
        Check(dd != null && dd.Length == 4 && dd[3] == 100, "диагностика: разбор уровней");
        Check(TachDiag.ParseDuties("30,abc", out perr) == null && perr != null && TachDiag.ParseDuties("120", out perr) == null && TachDiag.ParseDuties("", out perr) == null, "диагностика: плохие уровни отклоняются");

        // ---- v1.3: порог "плата не отвечает" ----
        Settings sh = new Settings();
        HubLink hl = new HubLink(sh, new LhmSensorSource());
        Check(Math.Abs(hl.StaleSeconds - 8.0) < 0.01, "порог STATUS в обычном режиме 8 с");
        hl.SetLight(true);
        Check(Math.Abs(hl.StaleSeconds - 24.0) < 0.01, "порог STATUS в свёрнутом режиме 24 с (опрос раз в 10 с)");
        hl.SetLight(false);
        Check(hl.StaleSeconds > 20.0, "после выхода из свёрнутого режима короткая пауза на свежий STATUS");
        HubState stH = new HubState();
        HealthResult hr = HealthEvaluator.Evaluate(ConnState.Connected, stH, DateTime.Now.AddSeconds(-15), new Dictionary<string, float>(), new Usage(sh), 24.0);
        HealthResult hr2 = HealthEvaluator.Evaluate(ConnState.Connected, stH, DateTime.Now.AddSeconds(-15), new Dictionary<string, float>(), new Usage(sh));
        Check(hr.Level == Health.Ok && hr2.Level == Health.Bad, "Health: 15 с без STATUS - норма в свёрнутом режиме и тревога в обычном");

        Console.WriteLine(fails == 0 ? "ALL OK" : fails + " FAILED");
        return fails;
    }
}
