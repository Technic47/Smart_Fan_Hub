# fanhub-web — веб-интерфейс контроллера MKS Fan Hub (замена Windows-трея на Ubuntu)

Хаб по-прежнему сидит на USB-serial порту сервера, но вместо трея его обслуживает
этот сервис: кормит плату температурами (раз в 1 с), опрашивает `STATUS?` (раз в 2 с),
держит соединение и показывает всё в браузере. Прошивку менять не нужно — протокол 0.3.25
тот же, что в `Windows Tray/mks_fan_hub_tray_v1_3_9`.

## Температуры — напрямую, без Prometheus

- GPU (обе карты): NVML (`pynvml`, тот же источник, что у `nvidia-smi`)
- CPU: `/sys/class/hwmon`, чип `coretemp` (Package)
- NVMe: `/sys/class/hwmon`, чип `nvme` (Composite)

Никаких сетевых зависимостей: мониторинг может лежать целый день — вентиляторы этого
не заметят. Если датчик пропал, в плату уходит последнее известное значение
(плата сама уйдёт в FAILSAFE по своему watchdog, если данные совсем кончатся).

## Установка на сервере (Ubuntu)

```bash
sudo apt install -y python3.14-venv    # или python3-venv на других версиях Ubuntu
sudo usermod -aG dialout technic47     # доступ к /dev/ttyACM* (перелогиниться)
git clone <этот репозиторий> ~/fanhub-web # или скопировать папку fanhub-web
cd ~/fanhub-web
python3 -m venv .venv
./.venv/bin/pip install -r requirements.txt
cp fanhub-web.service /etc/systemd/system/   # через sudo, см. ниже путь в unit
sudo systemctl daemon-reload
sudo systemctl enable --now fanhub-web
```

Веб-интерфейс: `http://<сервер>:8811`

## Что уже есть (итерация 1)

- поиск платы (`AUTO`: все `/dev/ttyACM*`/`ttyUSB*`), переподключение при обрыве,
  открытие порта без сброса (HUPCL выключен — как `DtrEnable=false` в трее);
- кормление `TEMP=` из прямых датчиков, опрос `STATUS?`, журнал событий платы;
- страница мониторинга: температура ПК, термисторы, каналы (%, датчик, тахо, RPM,
  состояние автозапуска), FAILSAFE-баннер.

## Дальше (итерация 2+)

- редактор кривых (5 точек, график, проверяемое «Применить/Сохранить») и связи групп;
- привязки каналов к датчикам, ручной режим (ползунок + HOLD), тест тахо;
- настройки автозапуска (SPIN) и частот ШИМ (DCFREQ);
- консоль-прокси к плате в браузере;
- (опционально) экспорт телеметрии хаба в Prometheus для Grafana.

## Тест без железа

Эмулятор платы из трея: `Windows Tray/mks_fan_hub_tray_v1_3_9/MksFanHubTray/test/fake_board.py`
(POSIX-терминал). Соединить через псевдотерминалы:

```bash
socat -d -d PTY,raw,echo=0,b115200 PTY,raw,echo=0,b115200 &
# один конец — эмулятор, другой указать в config.json: "port": "/dev/pts/N"
```
