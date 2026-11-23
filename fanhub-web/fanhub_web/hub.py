"""Сериальный линк с платой MKS Fan Hub (прошивка 0.3.25, протокол как в HubLink.cs трея).

Потоковая модель:
- фоновый поток: поиск платы -> сессия (кормление TEMP= + опрос STATUS?) -> реконнект;
- запросы из API (консоль и будущие диалоги) встают в очередь, на время ответа
  фоновые посылки TEMP=/STATUS? приостанавливаются (плата занята).
"""
from __future__ import annotations

import glob
import threading
import time
from collections import deque
from dataclasses import dataclass, field

import serial

BAUD = 115200

# события платы -> русский текст журнала (как в трее)
EVENT_TEXT = {
    "FAILSAFE,ON": "Плата: FAILSAFE включён (нет температур от ПК)",
    "FAILSAFE,OFF": "Плата: FAILSAFE выключен",
}


@dataclass
class Channel:
    name: str = ""
    pct: int = 0
    source_type: str = ""      # THERM | SERIAL
    source_name: str = ""
    locked: bool = False
    active: bool = False
    tach_pin: str = "-"        # '-' = тахо-входа нет
    tach_state: str = "NONE"
    rpm: int = 0
    spin_state: str = ""       # только DC-каналы: OK/KICK/.../ERR
    spin_tries: int = 0


@dataclass
class HubState:
    mode: str = "?"                        # TMP-SRL | TMP-MAN
    serial_values: dict = field(default_factory=dict)   # P102/GTX/CPU/SSD -> float
    thermistors: list = field(default_factory=list)     # dicts: name/enabled/value/state
    channels: list = field(default_factory=list)        # Channel
    watchdog_tripped: bool = False
    version: int = 0                       # бампается на каждом STATUS=END


class HubParser:
    """Разбор ответов платы в HubState."""

    def __init__(self) -> None:
        self.state = HubState()
        self._ch_by_name: dict[str, Channel] = {}
        self._spin_by_name: dict[str, Channel] = {}

    def _ch(self, name: str) -> Channel:
        ch = self._ch_by_name.get(name)
        if ch is None:
            ch = Channel(name=name)
            self._ch_by_name[name] = ch
            self.state.channels.append(ch)
        return ch

    def feed(self, raw: str) -> bool:
        """Вернуть True, если после этой строки состояние целостно (STATUS=END)."""
        line = raw.strip()
        if not line.startswith("STATUS="):
            return False
        body = line[len("STATUS="):]
        parts = body.split(",")
        kind = parts[0]
        st = self.state
        if kind == "MODE" and len(parts) >= 2:
            st.mode = parts[1]
        elif kind == "SERIAL" and len(parts) >= 3:
            try:
                st.serial_values[parts[1]] = float(parts[2])
            except ValueError:
                pass
        elif kind == "THERM" and len(parts) >= 5:
            rec = self._therm(parts[1])
            rec["enabled"] = parts[2] == "1"
            rec["value"] = None if parts[3] == "NaN" else float(parts[3])
            rec["state"] = parts[4]
        elif kind == "CH" and len(parts) >= 7:
            ch = self._ch(parts[1])
            try:
                ch.pct = int(parts[2])
            except ValueError:
                pass
            ch.source_type = parts[3]
            ch.source_name = parts[4]
            ch.locked = parts[5] == "LOCKED"
            ch.active = parts[6] == "ACTIVE"
        elif kind == "TACH" and len(parts) >= 5:
            ch = self._ch(parts[1])
            ch.tach_pin = parts[2]
            ch.tach_state = parts[3]
            try:
                ch.rpm = int(parts[4])
            except ValueError:
                ch.rpm = 0
        elif kind == "SPIN" and len(parts) >= 4:
            ch = self._ch(parts[1])
            ch.spin_state = parts[2]
            try:
                ch.spin_tries = int(parts[3])
            except ValueError:
                ch.spin_tries = 0
        elif kind == "WATCHDOG" and len(parts) >= 2:
            st.watchdog_tripped = parts[1] == "TRIPPED"
        elif kind == "END":
            st.version += 1
            return True
        return False

    def _therm(self, name: str) -> dict:
        for rec in self.state.thermistors:
            if rec["name"] == name:
                return rec
        rec = {"name": name, "enabled": False, "value": None, "state": "?"}
        self.state.thermistors.append(rec)
        return rec


class LogBook:
    """Кольцевой журнал для UI."""

    def __init__(self, maxlen: int = 300) -> None:
        self.items: deque = deque(maxlen=maxlen)
        self._lock = threading.Lock()

    def add(self, kind: str, text: str) -> None:
        with self._lock:
            self.items.append({"t": time.strftime("%H:%M:%S"), "kind": kind, "text": text})

    def tail(self, n: int = 100) -> list:
        with self._lock:
            return list(self.items)[-n:]


@dataclass
class _Request:
    cmd: str
    lines: list = field(default_factory=list)
    done: bool = False
    started: float = 0.0
    max_ms: int = 8000


class HubLink(threading.Thread):
    """Фоновый поток связи с платой."""

    def __init__(self, sensors, temp_map: dict, log: LogBook,
                 port: str = "AUTO", poll_ms: int = 1000, status_ms: int = 2000,
                 exclusive: bool = True):
        super().__init__(daemon=True, name="hublink")
        self.sensors = sensors
        self.temp_map = temp_map          # serial key -> sensor spec
        self.log = log
        self.port_wanted = port
        self.poll_ms = poll_ms
        self.status_ms = status_ms
        self.exclusive = exclusive

        self.state_lock = threading.Lock()
        self.parser = HubParser()
        self.connected = False
        self.port_name = ""
        self.last_rx = 0.0                # monotonic
        self.hb_seen = False
        self._stop = threading.Event()
        self._req_lock = threading.Lock()
        self._req: _Request | None = None
        # последние известные температуры (шлём их, если датчик пропал)
        self._last_temps: dict[str, float] = {}
        self.board_events = 0

    # ---------- API ----------

    def snapshot(self) -> dict:
        with self.state_lock:
            st = self.parser.state
            return {
                "link": {
                    "connected": self.connected,
                    "port": self.port_name,
                    "hb": self.hb_seen,
                    "idle_s": round(time.monotonic() - self.last_rx, 1) if self.connected else None,
                },
                "mode": st.mode,
                "watchdog": "TRIPPED" if st.watchdog_tripped else "OK",
                "serials": st.serial_values,
                "thermistors": st.thermistors,
                "channels": [vars(c).copy() for c in st.channels],
                "version": st.version,
            }

    def request(self, cmd: str, timeout_ms: int = 8000) -> list[str]:
        """Отправить команду и собрать ответ (до тишины 800 мс или маркера =END)."""
        req = _Request(cmd=cmd, started=time.monotonic(), max_ms=timeout_ms)
        with self._req_lock:
            self._req = req
        deadline = req.started + timeout_ms / 1000.0
        while not req.done and time.monotonic() < deadline and not self._stop.is_set():
            time.sleep(0.02)
        with self._req_lock:
            if self._req is req:
                self._req = None
        return req.lines

    def stop(self) -> None:
        self._stop.set()

    # ---------- внутреннее ----------

    def _find_board(self) -> serial.Serial | None:
        if self.port_wanted.upper() != "AUTO":
            candidates = [self.port_wanted]
        else:
            candidates = sorted(glob.glob("/dev/ttyACM*") + glob.glob("/dev/ttyUSB*"))
        for name in candidates:
            if self._stop.is_set():
                return None
            p = self._probe(name)
            if p is not None:
                return p
        return None

    def _probe(self, name: str) -> serial.Serial | None:
        """Открыть порт без сброса платы, спросить STATUS?."""
        try:
            p = serial.Serial(name, BAUD, timeout=0.2, write_timeout=1.0,
                              exclusive=self.exclusive)
        except (serial.SerialException, OSError):
            return None
        try:
            # не ронять DTR при закрытии: иначе при переподключении будет
            # спад уровня -> аппаратный сброс Mega (как DtrEnable=false в трее)
            import termios
            attrs = termios.tcgetattr(p.fd)
            attrs[2] &= ~termios.HUPCL
            termios.tcsetattr(p.fd, termios.TCSANOW, attrs)
            for _attempt in range(3):
                if self._stop.is_set():
                    break
                p.reset_input_buffer()
                p.write(b"STATUS?\n")
                deadline = time.monotonic() + 1.2
                buf = b""
                while time.monotonic() < deadline:
                    chunk = p.read(512)
                    if chunk:
                        buf += chunk
                        if b"STATUS=" in buf:
                            return p
                time.sleep(0.2)
        except (serial.SerialException, OSError, termios.error):
            pass
        try:
            p.close()
        except Exception:
            pass
        return None

    def _send_temps(self, port: serial.Serial) -> None:
        parts = []
        for key, spec in self.temp_map.items():
            reading = self.sensors.read(spec)
            if reading.value is not None:
                self._last_temps[key] = reading.value
                parts.append(f"{key}:{reading.value:.1f}")
            elif key in self._last_temps:
                # датчик пропал: шлём последнее известное (плата продолжит работать
                # по кривым), в UI это видно по возрасту датчика
                parts.append(f"{key}:{self._last_temps[key]:.1f}")
            # совсем нет данных — ключ пропускаем: плата пометит датчик ABSENT
        if parts:
            port.write(("TEMP=" + ",".join(parts) + "\n").encode())

    def _handle_event(self, line: str) -> None:
        body = line[len("EVENT="):]
        self.board_events += 1
        if body.startswith("HB,"):
            self.hb_seen = True
            return
        text = EVENT_TEXT.get(body)
        if text is None:
            if body.startswith("BOOT,"):
                text = "Плата перезагрузилась (%s)" % body.split(",", 1)[1]
            elif body.startswith("MODE,"):
                text = "Плата: режим %s" % body.split(",", 1)[1]
            elif body.startswith("FANFAIL,"):
                text = "ОШИБКА: вентилятор %s не раскрутился" % body.split(",", 1)[1]
            elif body.startswith("FANOK,"):
                text = "Плата: вентилятор %s снова крутится" % body.split(",", 1)[1]
            elif body.startswith("STALL,"):
                text = "Плата: вентилятор %s остановился" % body.split(",", 1)[1]
            elif body.startswith("KICK,"):
                text = "Плата: DC-вентилятор %s запускается толчком" % body.split(",", 1)[1]
            elif body.startswith("TACHGUARD,"):
                text = "Плата: на тахо-входе %s шторм помех" % body.split(",", 1)[1]
            elif body.startswith("THERM,"):
                p = body.split(",")
                if len(p) >= 3:
                    text = "Плата: термистор %s — %s" % (p[1], p[2])
            elif body.startswith("HOLD,"):
                p = body.split(",")
                if len(p) >= 3 and p[2] == "FAILSAFE":
                    text = "Плата: удержание %s снято (FAILSAFE)" % p[1]
            elif body.startswith("BTN,"):
                text = "Плата: кнопка (%s)" % body.split(",", 1)[1]
            else:
                text = "Плата: " + body
        self.log.add("event", text)

    def run(self) -> None:
        self.log.add("sys", "Сервис стартовал, ищу плату…")
        while not self._stop.is_set():
            port = self._find_board()
            if port is None:
                self.connected = False
                time.sleep(2.0)
                continue
            self.connected = True
            self.port_name = port.name
            self.last_rx = time.monotonic()
            self.log.add("sys", f"Плата найдена на {port.name}")
            self._session(port)
            self.connected = False
            self.log.add("sys", "Связь с платой потеряна, переподключаюсь…")
            try:
                port.close()
            except Exception:
                pass
            time.sleep(1.0)

    def _session(self, port: serial.Serial) -> None:
        next_feed = 0.0
        next_status = 0.0
        rx_buf = b""
        self.parser = HubParser()
        while not self._stop.is_set():
            now = time.monotonic()
            # запрос из API?
            with self._req_lock:
                req = self._req
            if req is not None and not req.done:
                try:
                    port.reset_input_buffer()
                    port.write((req.cmd.strip() + "\n").encode())
                except serial.SerialException:
                    return
                req.lines = self._collect_response(port, req.max_ms)
                req.done = True
                rx_buf = b""
                next_feed = time.monotonic() + 0.3
                next_status = time.monotonic() + 0.3
                continue
            # живость: без HB и без любого трафика 12 с -> реконнект
            if now - self.last_rx > 12.0:
                self.log.add("sys", "Плата не отвечает больше 12 с")
                return
            if now >= next_feed:
                next_feed = now + self.poll_ms / 1000.0
                try:
                    self._send_temps(port)
                except serial.SerialException:
                    return
            if now >= next_status:
                next_status = now + self.status_ms / 1000.0
                try:
                    port.write(b"STATUS?\n")
                except serial.SerialException:
                    return
            # чтение
            try:
                chunk = port.read(1024)
            except serial.SerialException:
                return
            if chunk:
                self.last_rx = time.monotonic()
                rx_buf += chunk
                while b"\n" in rx_buf:
                    raw, rx_buf = rx_buf.split(b"\n", 1)
                    line = raw.decode("utf-8", "replace").strip()
                    if not line:
                        continue
                    if line.startswith("EVENT="):
                        self._handle_event(line)
                    with self.state_lock:
                        self.parser.feed(line)
            else:
                time.sleep(0.02)

    def _collect_response(self, port: serial.Serial, max_ms: int) -> list[str]:
        """Собрать ответ на запрошенную команду: до маркера =END или тишины 800 мс."""
        lines: list[str] = []
        buf = b""
        deadline = time.monotonic() + max_ms / 1000.0
        quiet_deadline = None
        while time.monotonic() < deadline:
            chunk = port.read(1024)
            if chunk:
                self.last_rx = time.monotonic()
                buf += chunk
                while b"\n" in buf:
                    raw, buf = buf.split(b"\n", 1)
                    line = raw.decode("utf-8", "replace").strip()
                    if not line:
                        continue
                    if line.startswith("EVENT=HB"):
                        continue
                    if line.startswith("EVENT="):
                        self._handle_event(line)
                        continue
                    lines.append(line)
                    if line.endswith("=END"):
                        return lines
                    quiet_deadline = time.monotonic() + 0.8
            else:
                if quiet_deadline is not None and time.monotonic() > quiet_deadline:
                    return lines
                time.sleep(0.02)
        return lines
