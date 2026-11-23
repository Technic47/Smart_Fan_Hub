"""FastAPI-сервер: статика, REST-статус, WebSocket live-ленты, консоль к плате,
диалоги (кривые с верификацией, привязки, режим)."""
from __future__ import annotations

import asyncio
import os
import re

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles

from .hub import HubLink, LogBook
from .sensors import SensorSet

STATIC_DIR = os.path.join(os.path.dirname(__file__), "static")

_TOKEN_RE = re.compile(r"^[A-Za-z0-9_\-]+$")


def _parse_kv_lines(lines: list[str], prefix: str) -> dict:
    """CURVE=<имя>,<idx>,<t>,<p> / BIND=<имя>,THERM|SERIAL,<src>,LOCKED|FREE -> dict."""
    out: dict[str, dict] = {}
    for ln in lines:
        if not ln.startswith(prefix + "=") or ln == prefix + "=END":
            continue
        parts = ln[len(prefix) + 1:].split(",")
        out[parts[0]] = parts[1:]
    return out


def _curves_from(lines: list[str]) -> dict:
    """CURVES? -> {канал: [[temp, pct] x 5]}."""
    pts_by_ch: dict[str, dict[int, list]] = {}
    for ln in lines:
        if not ln.startswith("CURVE=") or ln == "CURVE=END":
            continue
        parts = ln[len("CURVE="):].split(",")
        if len(parts) < 4:
            continue
        try:
            idx, t, p = int(parts[1]), float(parts[2]), int(parts[3])
        except ValueError:
            continue
        pts_by_ch.setdefault(parts[0], {})[idx] = [t, p]
    return {name: [pts.get(i, [0, 0]) for i in range(5)]
            for name, pts in pts_by_ch.items()}


def create_app(config) -> FastAPI:
    app = FastAPI(title="fanhub-web", docs_url=None, redoc_url=None)
    log = LogBook()
    sensors = SensorSet()
    hub = HubLink(
        sensors,
        temp_map=config.data["temp_map"],
        log=log,
        port=config.data["port"],
        poll_ms=config.data["poll_ms"],
        status_ms=config.data["status_ms"],
        exclusive=bool(config.data.get("exclusive", True)),
    )
    hub.daemon = True
    hub.start()

    @app.on_event("shutdown")
    def _shutdown() -> None:
        hub.stop()

    display_serial = config.data["serial_display_names"]
    display_channel = config.data["channel_display_names"]

    def host_temps() -> dict:
        out = {}
        for key, spec in config.data["temp_map"].items():
            r = sensors.read(spec)
            out[key] = {
                "value": r.value,
                "age": round(r.age, 1),
                "label": display_serial.get(key, key),
            }
        return out

    async def req(cmd: str, ms: int = 9000) -> list[str]:
        """Отправить команду плате, не блокируя event loop."""
        return await asyncio.get_event_loop().run_in_executor(None, hub.request, cmd, ms)

    def bad(msg: str, code: int = 400):
        return JSONResponse({"error": msg}, status_code=code)

    @app.get("/api/status")
    async def status():
        snap = hub.snapshot()
        for ch in snap["channels"]:
            ch["label"] = display_channel.get(ch["name"], ch["name"])
        return {
            "hub": snap,
            "host_temps": host_temps(),
            "log": log.tail(150),
            "serial_display_names": display_serial,
        }

    # ---------- диалоги ----------

    @app.get("/api/curves")
    async def curves_get():
        lines = await req("CURVES?")
        return {"curves": _curves_from(lines), "raw": lines}

    @app.post("/api/curves")
    async def curves_save(body: dict):
        ch = str(body.get("channel", "")).strip()
        if not _TOKEN_RE.match(ch):
            return bad("некорректное имя канала")
        points = body.get("points")
        if not isinstance(points, list) or len(points) != 5:
            return bad("нужно ровно 5 точек")
        norm = []
        for pt in points:
            try:
                t, p = float(pt[0]), int(pt[1])
            except (TypeError, ValueError, IndexError):
                return bad("точка задана неверно")
            if not (0 <= t <= 125 and 0 <= p <= 100):
                return bad("точка вне диапазона (t: 0..125, %: 0..100)")
            norm.append((int(round(t)), p))
        for i, (t, p) in enumerate(norm):
            for ln in await req(f"CURVE={ch},{i},{t},{p}"):
                if "ERR" in ln:
                    return bad("плата отклонила точку: " + ln)
        saved = await req("CURVE=SAVE")
        board = _curves_from(await req("CURVES?")).get(ch)
        expect = [[float(t), p] for t, p in norm]
        ok = board == expect
        if not ok:
            log.add("sys", f"Кривая {ch}: верификация не сошлась, на плате {board}")
        return {"ok": ok, "saved": saved, "board": board}

    @app.get("/api/binds")
    async def binds_get():
        lines = await req("BINDS?")
        binds = []
        for name, parts in _parse_kv_lines(lines, "BIND").items():
            if len(parts) >= 3:
                binds.append({
                    "channel": name,
                    "type": parts[0],
                    "source": parts[1],
                    "locked": parts[2] == "LOCKED",
                })
        return {"binds": binds, "raw": lines}

    @app.post("/api/bind")
    async def bind_set(body: dict):
        ch = str(body.get("channel", "")).strip()
        typ = str(body.get("type", "")).strip().upper()
        src = str(body.get("source", "")).strip()
        if not (_TOKEN_RE.match(ch) and typ in ("THERM", "SERIAL") and _TOKEN_RE.match(src)):
            return bad("некорректные аргументы")
        lines = await req(f"BIND={ch},{typ},{src}")
        rejected = [ln for ln in lines if "ERR" in ln or "LOCKED" in ln]
        if rejected:
            return {"ok": False, "lines": lines}
        await req("BIND=SAVE")
        return {"ok": True, "lines": lines}

    @app.post("/api/mode")
    async def mode_set(body: dict):
        try:
            m = int(body.get("mode"))
        except (TypeError, ValueError):
            return bad("режим 0 или 1")
        if m not in (0, 1):
            return bad("режим 0 или 1")
        await req(f"MODE={m}")
        return {"ok": True, "lines": await req("MODE?")}

    @app.post("/api/console")
    async def console(body: dict):
        cmd = str(body.get("cmd", "")).strip()
        if not cmd:
            return bad("пустая команда")
        if len(cmd) > 120 or not re.match(r"^[A-Za-z0-9_\-,=?.\s]+$", cmd):
            return bad("команда содержит недопустимые символы")
        # запрос уходит в фоновый поток; блокируем event loop не дольше 9 с
        lines = await req(cmd, 9000)
        return {"cmd": cmd, "lines": lines}

    @app.websocket("/ws")
    async def ws(websocket: WebSocket):
        await websocket.accept()
        last_version = -1
        try:
            while True:
                snap = hub.snapshot()
                if snap["version"] != last_version:
                    last_version = snap["version"]
                    for ch in snap["channels"]:
                        ch["label"] = display_channel.get(ch["name"], ch["name"])
                    await websocket.send_json({
                        "hub": snap,
                        "host_temps": host_temps(),
                    })
                await asyncio.sleep(0.5)
        except WebSocketDisconnect:
            pass

    app.mount("/", StaticFiles(directory=STATIC_DIR, html=True), name="static")
    return app
