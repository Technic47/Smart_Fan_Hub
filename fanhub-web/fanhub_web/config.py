"""Конфигурация fanhub-web: JSON-файл рядом с сервисом (аналог MksFanHubTray.ini)."""
from __future__ import annotations

import json
import os

DEFAULTS = {
    # порт: "AUTO" или путь вроде "/dev/ttyACM0"
    "port": "AUTO",
    # эксклюзивный захват порта (TIOCEXCL): на реальном железе не дать
    # открыть порт другим программам; выключить для теста с эмулятором
    "exclusive": True,
    # веб-сервер
    "web_host": "0.0.0.0",
    "web_port": 8811,
    # периоды (как в трее: TEMP раз в сек, STATUS? раз в 2 с; watchdog платы 8 с)
    "poll_ms": 1000,
    "status_ms": 2000,
    # какие датчики ПК кормят какие ключи платы
    # (P102/GTX — исторические имена каналов из прошивки; теперь это 2080 Ti №1/№2)
    "temp_map": {
        "P102": {"type": "gpu", "index": 0},
        "GTX": {"type": "gpu", "index": 1},
        "CPU": {"type": "hwmon", "chip": "coretemp"},
        "SSD": {"type": "hwmon", "chip": "nvme"},
    },
    # отображаемые имена ключей платы в UI
    "serial_display_names": {
        "P102": "GPU 0 (2080 Ti)",
        "GTX": "GPU 1 (2080 Ti)",
        "CPU": "CPU",
        "SSD": "NVMe",
    },
    # отображаемые имена каналов (пусто = как в плате)
    "channel_display_names": {},
    # каналы, снятые с учёта (чекбокс «Подключён»): строка в UI серая
    "disabled_channels": [],
    # термисторы, снятые с учёта
    "disabled_thermistors": [],
    # группы общих кривых: канал -> id группы (как «Link.*» в трее);
    # кривая группы редактируется у первого канала (по порядку платы),
    # остальные члены группы читают её оттуда
    "curve_groups": {},
}


class Config:
    def __init__(self, path: str):
        self.path = path
        self.data = dict(DEFAULTS)
        self.load()

    def load(self) -> None:
        try:
            with open(self.path, encoding="utf-8") as f:
                custom = json.load(f)
            self.data.update(custom)
        except (OSError, ValueError):
            pass

    def save(self) -> None:
        os.makedirs(os.path.dirname(self.path) or ".", exist_ok=True)
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump(self.data, f, ensure_ascii=False, indent=2)
