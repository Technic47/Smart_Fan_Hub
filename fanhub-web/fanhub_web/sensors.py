"""Прямое чтение температур ПК: NVML (GPU) + /sys/class/hwmon (CPU, NVMe).

Намеренно без Prometheus и прочих посредников: цепочка кормления платы
должна работать, даже если весь мониторинг лежит (watchdog платы 8 с).
"""
from __future__ import annotations

import glob
import os
import time
from dataclasses import dataclass, field


@dataclass
class Reading:
    value: float | None      # None = датчик сейчас недоступен
    age: float               # секунд с последнего успешного чтения


class GpuTemps:
    """Температуры GPU через NVML (тот же источник, что и nvidia-smi)."""

    def __init__(self) -> None:
        self._ok = False
        self._nvml = None
        self._last: dict[int, tuple[float, float]] = {}
        try:
            import pynvml  # type: ignore
            self._nvml = pynvml
            pynvml.nvmlInit()
            self._ok = True
        except Exception:
            self._ok = False

    def available(self) -> bool:
        return self._ok

    def count(self) -> int:
        if not self._ok:
            return 0
        try:
            return self._nvml.nvmlDeviceGetCount()
        except Exception:
            return 0

    def read(self, index: int) -> Reading:
        if not self._ok:
            return Reading(None, 1e9)
        now = time.monotonic()
        try:
            handle = self._nvml.nvmlDeviceGetHandleByIndex(index)
            temp = float(self._nvml.nvmlDeviceGetTemperature(handle, self._nvml.NVML_TEMPERATURE_GPU))
            self._last[index] = (temp, now)
            return Reading(temp, 0.0)
        except Exception:
            last = self._last.get(index)
            if last is not None:
                return Reading(last[0], now - last[1])
            return Reading(None, 1e9)


class HwmonTemps:
    """Температуры из /sys/class/hwmon: coretemp (CPU), nvme (SSD).

    Путь ищем по имени чипа (по нему же ищем вентиляторы в README прошивки),
    а не по номеру hwmonN — номера между перезагрузками плавают.
    """

    def __init__(self) -> None:
        self._paths: dict[str, str] = {}   # chip -> path к tempN_input
        self._last: dict[str, tuple[float, float]] = {}

    def _scan(self) -> None:
        for base in glob.glob("/sys/class/hwmon/hwmon*"):
            try:
                chip = open(os.path.join(base, "name")).read().strip()
            except OSError:
                continue
            if chip not in ("coretemp", "nvme") or chip in self._paths:
                continue
            # ищем первый вход с label; для coretemp берём "Package id 0"
            want_label = {"coretemp": "Package id 0"}.get(chip)
            best = None
            for inp in sorted(glob.glob(os.path.join(base, "temp*_input"))):
                label_file = inp.replace("_input", "_label")
                label = ""
                try:
                    label = open(label_file).read().strip()
                except OSError:
                    pass
                if want_label is None or label == want_label:
                    best = inp
                    break
                if best is None:
                    best = inp
            if best:
                self._paths[chip] = best

    def read(self, chip: str) -> Reading:
        now = time.monotonic()
        if chip not in self._paths:
            self._scan()
        path = self._paths.get(chip)
        if path is None:
            return Reading(None, 1e9)
        try:
            millideg = int(open(path).read().strip())
            temp = millideg / 1000.0
            self._last[chip] = (temp, now)
            return Reading(temp, 0.0)
        except (OSError, ValueError):
            last = self._last.get(chip)
            if last is not None:
                return Reading(last[0], now - last[1])
            return Reading(None, 1e9)


@dataclass
class SensorSet:
    """Все источники температур хоста одним объектом."""

    gpus: GpuTemps = field(default_factory=GpuTemps)
    hwmon: HwmonTemps = field(default_factory=HwmonTemps)

    def read(self, spec: dict) -> Reading:
        """spec: {"type": "gpu", "index": 0} | {"type": "hwmon", "chip": "coretemp"}"""
        if spec.get("type") == "gpu":
            return self.gpus.read(int(spec.get("index", 0)))
        if spec.get("type") == "hwmon":
            return self.hwmon.read(str(spec.get("chip", "")))
        return Reading(None, 1e9)
