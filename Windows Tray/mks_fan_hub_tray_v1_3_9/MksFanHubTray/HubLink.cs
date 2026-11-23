// Связь с платой по Serial: автопоиск порта, переподключение, отправка TEMP=, опрос STATUS?
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace MksFanHub
{
    public enum ConnState { Searching, Connected, Lost }

    // Минимальный интерфейс порта: в программе - SerialPort, в тестах можно подставить эмулятор
    public interface ISerialLink
    {
        void Write(string text);
        int BytesToRead { get; }
        int Read(byte[] buffer, int offset, int count);
        void DiscardInBuffer();
        string ReadLine();           // бросает TimeoutException
        void Close();
    }

    public class RealSerialLink : ISerialLink
    {
        private readonly SerialPort p;

        public RealSerialLink(string name)
        {
            p = new SerialPort(name, 115200, Parity.None, 8, StopBits.One);
            p.NewLine = "\n";
            p.ReadTimeout = 500;
            p.WriteTimeout = 1000;
            p.DtrEnable = false;   // не перезагружаем плату при каждом подключении
            p.RtsEnable = false;
            p.Open();
        }

        public void Write(string text) { p.Write(text); }
        public int BytesToRead { get { return p.BytesToRead; } }
        public int Read(byte[] buffer, int offset, int count) { return p.Read(buffer, offset, count); }
        public void DiscardInBuffer() { p.DiscardInBuffer(); }
        public string ReadLine() { return p.ReadLine(); }
        public void Close()
        {
            try { if (p.IsOpen) p.Close(); } catch { }
            try { p.Dispose(); } catch { }
        }
    }

    public class HubLink
    {
        private readonly Settings settings;
        private readonly ISensorSource sensors;
        private Thread worker;
        private volatile bool stop;
        private ISerialLink port;

        // Как открывать порт (в тестах подменяется)
        public Func<string, ISerialLink> PortFactory = delegate (string name) { return new RealSerialLink(name); };

        public volatile ConnState State = ConnState.Searching;
        public volatile string PortName = "";
        public volatile HubState Last;
        public DateTime LastStatusAt = DateTime.MinValue;
        public Dictionary<string, float> LastSent = new Dictionary<string, float>();
        public DateTime LastSentAt = DateTime.MinValue;
        public string SensorInfo = "";

        public event Action Updated;   // вызывается из рабочего потока

        // ---- события платы (EVENT=..., прошивка 0.3.15+), свёрнутый режим, сон Windows ----
        // Текст события без префикса "EVENT=", например "FAILSAFE,ON". HB (пульс) сюда не попадает.
        // Вызывается из рабочего потока.
        public event Action<string> BoardEvent;
        public volatile bool HbSeen;                  // плата присылает пульс (прошивка 0.3.15+)
        public DateTime LastHbAt = DateTime.MinValue;
        public volatile bool Light;                   // окно скрыто: опрашиваем реже
        private volatile bool statusNow;              // запросить STATUS? немедленно
        private volatile bool paused;                 // Windows уходит в сон: порт закрыт, плату не ищем
        private volatile bool sensorsReset;           // после сна переоткрыть датчики ПК
        private DateTime resumeAfter = DateTime.MinValue;
        private DateTime staleGraceUntil = DateTime.MinValue;
        public const int ResumeDelaySec = 5;          // после пробуждения USB поднимается не сразу

        public bool IsPaused { get { return paused; } }

        // Переключить свёрнутый режим. При выходе из него сразу запрашиваем свежий STATUS?
        public void SetLight(bool v)
        {
            if (Light == v) return;
            Light = v;
            if (!v) { statusNow = true; staleGraceUntil = DateTime.Now.AddSeconds(4); }
        }

        public void RequestStatusNow() { statusNow = true; }

        // Сон Windows: закрыть порт и не искать плату, пока не проснёмся
        public void Pause() { paused = true; }

        public void Resume()
        {
            resumeAfter = DateTime.Now.AddSeconds(ResumeDelaySec);
            sensorsReset = true;
            reconnect = true;
            paused = false;
        }

        // Через сколько секунд без свежего STATUS считаем, что плата не отвечает
        public double StaleSeconds
        {
            get
            {
                double st = settings.StatusMs / 1000.0;
                if (Light || DateTime.Now < staleGraceUntil) st = Math.Max(st, settings.LightStatusMs / 1000.0);
                return Math.Max(8.0, st * 2.0 + 4.0);
            }
        }

        private int CurrentPollMs() { return Light ? Math.Max(settings.PollMs, settings.LightPollMs) : settings.PollMs; }
        private int CurrentStatusMs() { return Light ? Math.Max(settings.StatusMs, settings.LightStatusMs) : settings.StatusMs; }

        private void HandleEvent(string text)
        {
            if (text.StartsWith("HB", StringComparison.Ordinal))
            {
                HbSeen = true;
                LastHbAt = DateTime.Now;
                return;
            }
            if (!text.Equals("BTN,SHORT", StringComparison.Ordinal)) statusNow = true;   // остальное меняет состояние - обновим сразу
            Action<string> h = BoardEvent;
            if (h != null) { try { h(text); } catch { } }
        }

        // Трафик для консоли: (true = ушло в плату, строка) / (false = пришло из платы, строка).
        // Фоновые TEMP= и STATUS? не показываются, строки STATUS=... тоже (кроме окна после ручного STATUS?).
        // Вызывается из рабочего потока.
        public event Action<bool, string> Traffic;
        private DateTime echoStatusUntil = DateTime.MinValue;

        private void RaiseTraffic(bool outgoing, string line)
        {
            Action<bool, string> h = Traffic;
            if (h != null) { try { h(outgoing, line); } catch { } }
        }

        // Ручная команда из консоли (без ожидания ответа: ответ придёт событием Traffic)
        // Пока плата снимает TACHDUMP/TACHFAST, она не читает Serial (буфер приёма всего 64 байта):
        // фоновые TEMP=/STATUS? в это время переполняют его и портят следующие команды.
        private static bool IsQuietCmd(string cmd)
        {
            return cmd.StartsWith("TACHDUMP=", StringComparison.Ordinal) || cmd.StartsWith("TACHFAST=", StringComparison.Ordinal);
        }
        private DateTime quietUntil = DateTime.MinValue;

        public bool ConsoleSend(string cmd, out string error)
        {
            if (IsQuietCmd(cmd)) quietUntil = DateTime.Now.AddSeconds(2.5);
            if (cmd.StartsWith("STATUS?", StringComparison.Ordinal)) echoStatusUntil = DateTime.Now.AddSeconds(2.5);
            return Send(cmd, out error);
        }

        // ---- запросы от интерфейса (кривые, привязки, режим) ----
        private class Request
        {
            public string Cmd;
            public Func<string, bool> IsEnd;       // null - просто отправить
            public int TimeoutMs;
            public DateTime SentAt;
            public List<string> Lines = new List<string>();
            public ManualResetEvent Done = new ManualResetEvent(false);
            public bool Ok;
            public string Error;
            public volatile bool Abandoned;
            public bool Quiet;     // на время ответа не слать TEMP=/STATUS? (плата занята и не читает Serial)
        }
        private readonly Queue<Request> queue = new Queue<Request>();
        private Request active;
        private volatile bool reconnect;

        public static string[] ListPorts()
        {
            try { string[] n = SerialPort.GetPortNames(); Array.Sort(n); return n; }
            catch { return new string[0]; }
        }

        public void RequestReconnect() { reconnect = true; }

        // Отправить команду и дождаться ответа (isEnd - признак последней строки ответа).
        // Блокирует вызывающий поток не дольше timeoutMs.
        public bool Query(string cmd, Func<string, bool> isEnd, int timeoutMs, out List<string> lines, out string error)
        {
            lines = new List<string>();
            error = null;
            if (State != ConnState.Connected) { error = "нет связи с платой"; return false; }
            Request r = new Request();
            r.Cmd = cmd;
            r.Quiet = IsQuietCmd(cmd);
            r.IsEnd = isEnd;
            r.TimeoutMs = timeoutMs;
            lock (queue) { queue.Enqueue(r); }
            if (!r.Done.WaitOne(timeoutMs + 1500))
            {
                r.Abandoned = true;
                error = "плата не ответила на " + cmd;
                return false;
            }
            lines = r.Lines;
            error = r.Error;
            return r.Ok;
        }

        // Отправить без ожидания ответа (например MODE=1)
        public bool Send(string cmd, out string error)
        {
            List<string> dummy;
            return Query(cmd, null, 500, out dummy, out error);
        }

        private void Finish(Request r, bool ok, string error)
        {
            r.Ok = ok;
            r.Error = error;
            r.Done.Set();
        }

        private void FailPending(string error)
        {
            lock (queue)
            {
                while (queue.Count > 0) Finish(queue.Dequeue(), false, error);
            }
            if (active != null) { Finish(active, false, error); active = null; }
        }

        public HubLink(Settings s, ISensorSource src)
        {
            settings = s;
            sensors = src;
        }

        public void Start()
        {
            stop = false;
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "HubLink";
            worker.Start();
        }

        public void Stop()
        {
            stop = true;
            try { if (worker != null) worker.Join(2000); } catch { }
            ClosePort();
        }

        private void Raise()
        {
            Action h = Updated;
            if (h != null) { try { h(); } catch { } }
        }

        private void SetState(ConnState s, string portName)
        {
            if (State != s || PortName != portName)
            {
                State = s;
                PortName = portName;
                Raise();
            }
        }

        private void ClosePort()
        {
            try { if (port != null) port.Close(); } catch { }
            port = null;
        }

        private void Run()
        {
            while (!stop)
            {
                WaitResume();
                SetState(ConnState.Searching, "");
                string found = FindBoard();
                if (found == null) { Sleep(3000); continue; }

                Log.Write("Плата найдена на " + found);
                SetState(ConnState.Connected, found);
                bool requested = false;
                try { requested = Session(); }
                catch (Exception ex) { Log.Write("Связь прервана: " + ex.Message); }
                ClosePort();
                FailPending("связь с платой потеряна");
                if (paused) { SetState(ConnState.Searching, ""); continue; }   // сон: в начале цикла дождёмся пробуждения
                if (requested) { Log.Write("Переподключение"); continue; }
                if (!stop) { SetState(ConnState.Lost, found); Sleep(2000); }
            }
        }

        // Пока Windows спит (и ещё несколько секунд после пробуждения) плату не ищем
        private void WaitResume()
        {
            while (!stop && (paused || DateTime.Now < resumeAfter)) Thread.Sleep(200);
        }

        private void Sleep(int ms)
        {
            int t = 0;
            while (t < ms && !stop) { Thread.Sleep(100); t += 100; }
        }

        // ---------- поиск платы ----------
        private string FindBoard()
        {
            List<string> candidates = new List<string>();
            string want = settings.ComPort;
            if (!string.IsNullOrEmpty(want) && !want.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(want);
            }
            else
            {
                string[] names;
                try { names = SerialPort.GetPortNames(); } catch { names = new string[0]; }
                Array.Sort(names);
                candidates.AddRange(names);
            }
            foreach (string name in candidates)
            {
                if (stop) return null;
                if (Probe(name)) return name;
            }
            return null;
        }

        // Открыть порт, спросить STATUS?, ждать STATUS=MODE. Если порт ответил - оставляем открытым.
        private bool Probe(string name)
        {
            ISerialLink p = null;
            try
            {
                p = PortFactory(name);
                for (int attempt = 0; attempt < 3 && !stop; attempt++)
                {
                    p.DiscardInBuffer();
                    p.Write("STATUS?\n");
                    DateTime until = DateTime.Now.AddMilliseconds(1200);
                    while (DateTime.Now < until && !stop)
                    {
                        string line;
                        try { line = p.ReadLine(); } catch (TimeoutException) { continue; }
                        if (line != null && line.Trim().StartsWith("STATUS=", StringComparison.Ordinal))
                        {
                            port = p;
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { /* занят или не наш */ }
            try { if (p != null) p.Close(); } catch { }
            return false;
        }

        // ---------- рабочая сессия ----------
        private bool Session()
        {
            StatusParser parser = new StatusParser();
            DateTime nextFeed = DateTime.MinValue;
            DateTime nextStatus = DateTime.MinValue;
            DateTime lastRx = DateTime.Now;
            StringBuilder buf = new StringBuilder();

            reconnect = false;
            HbSeen = false;
            statusNow = false;
            while (!stop)
            {
                if (paused) return false;
                if (reconnect) { reconnect = false; return true; }
                DateTime now = DateTime.Now;
                PumpRequests(now);
                bool quiet = now < quietUntil || (active != null && active.Quiet);
                if (now >= nextFeed && !quiet)
                {
                    nextFeed = now.AddMilliseconds(CurrentPollMs());
                    SendTemps();
                }
                if ((now >= nextStatus || statusNow) && !quiet)
                {
                    statusNow = false;
                    nextStatus = now.AddMilliseconds(CurrentStatusMs());
                    port.Write("STATUS?\n");
                }

                // читаем всё, что накопилось
                int n = port.BytesToRead;
                if (n > 0)
                {
                    byte[] data = new byte[n];
                    int got = port.Read(data, 0, n);
                    for (int i = 0; i < got; i++)
                    {
                        char c = (char)data[i];
                        if (c == '\n')
                        {
                            string full = buf.ToString();
                            buf.Length = 0;
                            string shown = full.Trim();
                            bool isEvent = shown.StartsWith("EVENT=", StringComparison.Ordinal);
                            bool isHb = isEvent && shown.StartsWith("EVENT=HB", StringComparison.Ordinal);
                            if (isEvent) HandleEvent(shown.Substring(6));
                            else OnResponseLine(full);
                            if (shown.Length > 0 && !isHb && (!shown.StartsWith("STATUS=", StringComparison.Ordinal) || DateTime.Now < echoStatusUntil))
                                RaiseTraffic(false, shown);
                            HubState st = parser.Feed(full);
                            lastRx = DateTime.Now;
                            if (st != null)
                            {
                                Last = st;
                                LastStatusAt = DateTime.Now;
                                Raise();
                            }
                        }
                        else if (c != '\r')
                        {
                            if (buf.Length < 512) buf.Append(c);
                        }
                    }
                }
                else
                {
                    Thread.Sleep(30);
                }

                // С пульсом (EVENT=HB каждые 5 с) тишина больше 12 с - обрыв. Без него (старая прошивка)
                // ждём дольше, если опрос редкий (свёрнутый режим).
                double silenceLimit = HbSeen ? 12.0 : StaleSeconds;
                if ((DateTime.Now - lastRx).TotalSeconds > silenceLimit)
                    throw new Exception("плата молчит больше " + (int)silenceLimit + " с");
            }
            return false;
        }

        // Берёт следующий запрос из очереди и отправляет; снимает просроченные
        private void PumpRequests(DateTime now)
        {
            if (active != null)
            {
                if (active.Abandoned) { active = null; }
                else if ((now - active.SentAt).TotalMilliseconds > active.TimeoutMs)
                {
                    Finish(active, false, "плата не ответила на " + active.Cmd);
                    active = null;
                }
            }
            if (active != null) return;
            Request r = null;
            lock (queue) { while (queue.Count > 0) { r = queue.Dequeue(); if (!r.Abandoned) break; r = null; } }
            if (r == null) return;
            port.Write(r.Cmd + "\n");
            RaiseTraffic(true, r.Cmd);
            r.SentAt = now;
            if (r.IsEnd == null) { Finish(r, true, null); return; }
            active = r;
        }

        // Все строки, кроме STATUS=..., считаем ответом на активный запрос
        private void OnResponseLine(string line)
        {
            if (active == null) return;
            string l = line.Trim();
            if (l.Length == 0 || l.StartsWith("STATUS=", StringComparison.Ordinal) || l.StartsWith("EVENT=", StringComparison.Ordinal)) return;
            active.Lines.Add(l);
            bool end = false;
            try { end = active.IsEnd(l); } catch { }
            if (end) { Finish(active, true, null); active = null; }
        }

        private void SendTemps()
        {
            if (sensorsReset)
            {
                sensorsReset = false;
                try { sensors.Reset(); Log.Write("Датчики ПК переоткрыты после сна"); }
                catch (Exception ex) { Log.Write("Датчики ПК не переоткрылись: " + ex.Message); }
            }
            Dictionary<string, float> t = sensors.Read();
            SensorInfo = sensors.Info;
            if (t.Count == 0) return;
            StringBuilder sb = new StringBuilder("TEMP=");
            bool first = true;
            foreach (KeyValuePair<string, float> kv in t)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("0.0", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
            port.Write(sb.ToString());
            LastSent = t;
            LastSentAt = DateTime.Now;
        }
    }
}
