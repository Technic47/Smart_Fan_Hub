"""FastAPI-сервер: статика, REST-статус, WebSocket live-ленты, консоль к плате."""
from __future__ import annotations

import asyncio
import os

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles

from .hub import HubLink, LogBook
from .sensors import SensorSet

STATIC_DIR = os.path.join(os.path.dirname(__file__), "static")


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

    @app.post("/api/console")
    async def console(body: dict):
        cmd = str(body.get("cmd", "")).strip()
        if not cmd:
            return JSONResponse({"error": "пустая команда"}, status_code=400)
        # запрос уходит в фоновый поток; блокируем event loop не дольше 9 с
        lines = await asyncio.get_event_loop().run_in_executor(None, hub.request, cmd, 9000)
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
