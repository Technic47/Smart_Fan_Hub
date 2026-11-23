/* ======================================================================
 * MKS Fan Hub v0.3.25
 * Контроллер вентиляторов/дисплея для X79 home-server
 * Плата: MKS Gen_L V1.0 (ATmega2560 / Arduino Mega 2560)
 * Arduino IDE: плата "Arduino Mega or Mega 2560". Библиотеки: U8g2 (для OLED) или Adafruit GFX + Adafruit PCD8544 (для Nokia 5110).
 *
 * Что нового в v0.3.25 (относительно v0.3.24) - короткая проверка:
 *   - Если в первые 0,25 с проверки (100%) пришло >= FS_EARLY_PULSES (2) импульса - вентилятор крутился, проверка заканчивается сразу
 *     (~0,3 с вместо 0,7 с). Если импульсов нет или один - как раньше, до 0,7 с.
 *
 * Что нового в v0.3.24 (относительно v0.3.23) - автозапуск DC-вентиляторов после остановки:
 *   - У каждого DC-канала хранится "точка раскрутки" (spinStart, по умолчанию 37%) - с какого % вентилятор стартует с места.
 *   - Остановку 3-pin вентилятора на частичном ШИМ по тахо не видно (читается только на 100%), поэтому раз в spinProbeS секунд
 *     (по умолчанию 60, 0 = выключено) плата на ~0,7 с даёт каналу 100% и считает импульсы. В первые 0,25 с нет импульсов -
 *     вентилятор стоял. Канал на 100% контролируется постоянно (нет импульсов > 4 с).
 *   - Остановка -> точка раскрутки + spinBoost % на 2 с, затем снижение на spinStep % каждые spinRampMs до нужного %,
 *     затем проверка (100% на 0,7 с). Не закрутился - повтор, всего spinTries попыток подряд (по умолчанию 3), потом
 *     ошибка канала: EVENT=FANFAIL,<имя>, выход 0%, STATUS=SPIN,<имя>,ERR. Сброс: SPIN=CLEAR,<канал|ALL> (или перезагрузка).
 *   - Толчок при пуске с 0% (v0.3.23) теперь тоже проверяется по импульсам; "не крутится" - тот же цикл попыток.
 *   - Команды: SPIN? / SPIN=<канал|ALL>,<%> / SPIN=PARAM,<boost>,<step>,<rampMs>,<probeS>,<tries> / SPIN=SAVE / SPIN=RESET /
 *     SPIN=CLEAR[,<канал|ALL>] / SPIN=TEST,<канал> (проверка прямо сейчас). События: STALL, FANOK, FANFAIL.
 *   - Каналы без вентилятора (тахо ни разу не видел импульсов) не проверяются и ошибку не выдают.
 *
 * Что нового в v0.3.23 (относительно v0.3.22) - запуск DC-вентиляторов толчком и нижний предел по кривой:
 *   Измерено: 3-pin DC вентиляторы (31 Гц) останавливаются при падении до ~14%, а стартуют только с ~37%.
 *   - DC_MIN_RUN_PCT 20: на DC-каналах значение кривой 1..19% поднимается до 20% (вентилятор не глохнет в "мёртвой зоне").
 *     0% остаётся 0% (выключено).
 *   - Если DC-канал стоит (0%), а кривая просит 1..44% (DC_KICK_BELOW_PCT) - сначала толчок 100% на DC_KICK_MS (3 с),
 *     потом нужный %. Так же при включении платы. Событие EVENT=KICK,<имя>.
 *   - Только для значений из кривой. Ручной HOLD=/SET=, TACHTEST и fail-safe работают как раньше (без толчка и подъёма).
 *
 * Что нового в v0.3.22 (относительно v0.3.21) - своя частота ШИМ у каждого DC-канала:
 *   - Все четыре DC-канала (D8 FUN_DC_0, D10 FUN_DC_1, D7 FUN_DC_2, D9 FUN_DC_3) - программный ШИМ из прерывания Timer2
 *     (тик 4 кГц, CTC). У каждого канала свой период, частота 15..300 Гц, шаг заполнения 100*Гц/4000 % (на 31 Гц ~0,8%).
 *   - DCFREQ? -> строки DCFREQ=<канал>,<Гц>,<период_в_тиках>, затем DCFREQ=END.
 *     DCFREQ=<канал|ALL>,<Гц> - сменить на лету (ответ DCFREQ=OK,<канал>,<Гц>,<период>), DCFREQ=ERR,channel|range.
 *     DCFREQ=SAVE - записать частоты в EEPROM, DCFREQ=RESET - вернуть заводские (DC_DEFAULT_HZ = 31 Гц).
 *   - Прежняя команда DCFREQ=<1..7> (v0.3.20/21) заменена. Аппаратный ШИМ на D9/D10 больше не используется.
 *   - 4-pin каналы (25 кГц, Timer1/3/4/5) не затронуты.
 *
 * Что нового в v0.3.21 (относительно v0.3.20) - низкочастотный ШИМ ~31 Гц на всех DC-каналах (нет звона, ниже пороги):
 *   - DC_PWM_MODE_DEFAULT 7: каналы на Timer2 (D9 = FUN_DC_3, D10 = FUN_DC_1) стартуют на ~31 Гц (раньше 31 кГц).
 *   - DC_SOFT_PWM 1: каналы D7 (FUN_DC_2) и D8 (FUN_DC_0) переведены на программный ШИМ из прерывания Timer0 (~976 Гц):
 *     период 32 тика = ~30,5 Гц, шаг 1/32 (~3%). Аппаратный Timer4 остался только у 4-pin канала D6 (25 кГц).
 *     Для вентиляторов ступеньки в 3% не важны; 0% и 100% - ровно "выключено/включено" (на 100% tach чистый).
 *   - DCFREQ по-прежнему меняет частоту только D9/D10 (для опытов); программные каналы D7/D8 всегда ~30,5 Гц.
 *   - Нужно, чтобы DC_SOFT_PWM 0 / DC_PWM_MODE_DEFAULT 1 вернули прежнее поведение (25-31 кГц).
 *
 * Что нового в v0.3.20 (относительно v0.3.19) - подбор частоты ШИМ для DC-вентиляторов на слух:
 *   - DCFREQ=<1..7> меняет частоту ШИМ каналов на Timer2 (D9 = FUN_DC_3, D10 = FUN_DC_1) прямо на ходу, без перепрошивки:
 *       1 = 31372 Гц (как раньше), 2 = 3921, 3 = 980, 4 = 490, 5 = 245, 6 = 122, 7 = 31 Гц.
 *     Ответ DCFREQ=OK,<n>,<Гц>; DCFREQ? - текущее значение. После перезагрузки платы - снова режим 1. Не сохраняется.
 *     FUN_DC_0 (D8) и FUN_DC_2 (D7) сидят на Timer4 вместе с 4-pin каналом FUN_PWM_1 (нужны строго ~25 кГц), поэтому
 *     их частоту программно менять нельзя.
 *
 * Что нового в v0.3.19 (относительно v0.3.18) - тахо-входы FUN_DC_1 и FUN_DC_2 поменяны местами (по факту проводки):
 *   FUN_DC_1 (D10, HE0) читает tach с D51, FUN_DC_2 (D7, HE1) - с A10 (D64). FUN_DC_0 - A9, FUN_DC_3 - A12 как в 0.3.18.
 *   Выходы и имена не менялись. Проверено по поведению: при смене % на одном канале обороты менялись в строке другого.
 *
 *   - защита от "шторма" на tach-входе: если на PCINT-входе за 100 мс набирается >400 отброшенных импульсов
 *     (линия рубится ШИМом чужого вентилятора, провод tach воткнут не в тот канал и т.п.), вход отключается на 3 с,
 *     плата шлёт EVENT=TACHGUARD,<канал>. Раньше такой шторм съедал весь процессор: плата переставала отвечать по Serial.
 *
 * Что нового в v0.3.18 (относительно v0.3.17) - исправлены перепутанные тахо-входы FUN_DC_2 и FUN_DC_3:
 *   FUN_DC_2 (D7, HE1) теперь читает tach с D51, FUN_DC_3 (D9, FAN) - с A12 (D66). Выходы и имена не менялись.
 *   Провода перетыкать не надо: тахо-провод вентилятора на HE1 идёт на D51, на FAN - на A12.
 *
 * Что нового в v0.3.17 (относительно v0.3.16) - имена DC-каналов идут по порядку разъёмов:
 *   FUN_DC_0 = D8 (терминал BED, бывший FUN_DC_1), FUN_DC_1 = D10 (HE0, бывший FUN_DC_2),
 *   FUN_DC_2 = D7 (HE1, бывший FUN_DC_3), FUN_DC_3 = D9 (FAN, бывший FUN_DC_0).
 *   Меняются ТОЛЬКО имена: пины, tach-входы, номера idx и раскладка EEPROM прежние, поэтому сохранённые кривые
 *   остаются на тех же физических разъёмах. Закреплённый "выдув" (CH_EXHAUST, "Out:" на OLED) теперь канал FUN_DC_0 = D8.
 *   Программа 1.3.3+ сама переносит свои имена/связи кривых на новые названия.
 *
 * Что нового в v0.3.16 (относительно v0.3.15) - диагностика сбоев на DC-каналах:
 *   - EVENT=BOOT,<версия>,<причина>: причина сброса из MCUSR: P - включение питания, E - внешний сброс (кнопка/DTR),
 *     B - просадка питания (brown-out), W - watchdog, J - JTAG; ? - загрузчик затёр флаги. Если при ручном
 *     выставлении % на DC-канале плата "пропадает", здесь видно, что это была перезагрузка и почему.
 *   - CURVE=/BIND= с ошибкой теперь отвечают: CURVE=ERR,args|channel|idx, BIND=ERR,args|channel|source
 *     (раньше молчали, и программа видела только "плата не ответила").
 *
 * Что нового в v0.3.15 (относительно v0.3.14) - диагностика тахо 3-pin и события для программы:
 *   - HOLD=<канал>,<проц>[,<сек>] : ручное УДЕРЖАНИЕ процентов канала (кривые его не трогают), по умолчанию
 *     30 с, максимум 300 с, потом канал возвращается под управление кривой. HOLD=<канал>,OFF - снять,
 *     HOLD=ALL,OFF - снять всё, HOLD? - список. <канал> - номер или имя. FAILSAFE (watchdog) удержание снимает.
 *   - TACHDUMP=<канал>[,<бин мкс>,<число бинов>] : "сырая" запись tach-пина (по умолчанию 1000 бинов по 1 мс = 1 с).
 *     На каждый бин: число переходов (0..255) и доля времени в "1" (0..255). Прерывания этого входа на время
 *     записи отключаются. Ответ: TACHDUMP=BEGIN,... / TACHDUMP=D,<с какого бина>,<hex TTHH...> / TACHDUMP=END,<канал>
 *   - TACHFAST=<канал> : то же, но 1000 бинов по 50 мкс (50 мс) - тонкая структура помехи ШИМ.
 *   - события платы (линии EVENT=...), отправляются сами, без запроса:
 *       EVENT=BOOT,<версия>,<причина>; EVENT=HB,<аптайм_с> каждые 5 с; EVENT=FAILSAFE,ON|OFF; EVENT=MODE,<0|1>;
 *       EVENT=BTN,SHORT|LONG; EVENT=THERM,<имя>,<состояние>; EVENT=HOLD,<канал>,END|FAILSAFE; EVENT=TACHGUARD,<канал>
 *
 * Что нового в v0.3.14 (относительно v0.3.13):
 *   - термистор EXT (A1, колодка драйвера X, DIR; 100 кОм подтяжка) включён: THERM_ENABLED[4] = true
 *
 * Что нового в v0.3.13 (относительно v0.3.12):
 *   - фильтр tach по форме сигнала: спад принимается, только если перед ним линия была в "1" не меньше
 *     TACH_MIN_HIGH_US (1,5 мс). На входах концевиков (RC-фильтр + подтяжка) медленный фронт нарастания
 *     с шумом давал ложный "спад" на каждом подъёме - обороты удваивались (NF-S12-1200: 2100 вместо 1110).
 *     D18..D21 теперь по CHANGE (нужны и подъёмы), PCINT уже видели оба фронта.
 *
 * Что нового в v0.3.12 (относительно v0.3.11):
 *   - отладочная команда TACHRAW=n: интервалы между последними 7 принятыми импульсами канала n (мкс)
 *     и счётчик отброшенных фильтром помех. Нужна, чтобы увидеть, что реально приходит на tach-пин
 *     (двойные импульсы, дребезг, пропуски).
 *
 * Что нового в v0.3.11 (относительно v0.3.10):
 *   - tach-входы 3-pin каналов (4,5,6,9) подключаются к прерыванию PCINT только пока канал на 100% PWM.
 *     При ШИМ по минусу tach-линия рубится с частотой ШИМа (десятки кГц), и поток прерываний мог
 *     загрузить процессор и "подвесить" Serial/кнопку/дисплей. Теперь на неполных оборотах прерываний нет.
 *
 * Что нового в v0.3.10 (относительно v0.3.9):
 *   - OLED разделён на две части: слева (15 символов) температуры и прокручиваемые экраны, справа
 *     (линия-разделитель) статично: "Mode: SER"/"Mode: MAN", индикация FailSafe ("FS: OK" или
 *     инвертированная плашка FAILSAFE!) и номер текущего экрана. Режим и FAILSAFE из левой части убраны.
 *   - экран по умолчанию - сводка температур (P102, GTX, CPU, SSD, Case) и процент канала GPU
 *   - для Nokia (DISPLAY_TYPE 0) экраны остаются прежними
 *
 * Что нового в v0.3.9 (относительно v0.3.8):
 *   - кнопка опрашивается из прерывания Timer0 (~1 кГц), а не в loop(): вывод на OLED через программный
 *     I2C блокирует loop() на десятки/сотни мс, и короткие нажатия терялись. Действия выполняются в loop().
 *
 * Что нового в v0.3.8 (относительно v0.3.7):
 *   - команда TACHTEST: все 3-pin каналы (4,5,6,9) на 100% на TACHTEST_SPINUP_MS (5 с), потом
 *     замер RPM, вывод в Serial (TACHTEST=имя,пин,состояние,rpm), возврат прежних процентов.
 *     На время теста кривые для этих каналов не применяются, на дисплее экран TACH TEST.
 *
 * Что нового в v0.3.7 (относительно v0.3.6):
 *   - tach 3-pin каналов (4,5,6,9; MOSFET по минусу) принимается ТОЛЬКО при 100% PWM
 *     (1 полное окно без снижения): при ШИМ сигнал tach рубится ШИМом и даёт ложные тысячи об/мин.
 *     В остальное время состояние PWM (на дисплее PWM, rpm 0). Подтверждено на NF-S12-1200: 1110 при 100%.
 *   - верхний предел TACH_MAX_RPM: значения выше считаются помехой (пустой вход) и не принимаются
 *
 * Что нового в v0.3.6 (относительно v0.3.5):
 *   - кнопка перенесена на колодку драйвера E0: D24 (ENABLE) и GND той же колодки
 *
 * Что нового в v0.3.5 (относительно v0.3.4):
 *   - термисторы CASE и EXT перенесены на колодку драйвера X: CASE -> A0 (STEP), EXT -> A1 (DIR)
 *     (колодка драйвера Y освобождена)
 *
 * Что нового в v0.3.4 (относительно v0.3.3):
 *   - OLED SSD1306 теперь через библиотеку U8g2 с ПРОГРАММНЫМ I2C на AUX-3: SDA=D50, SCL=D52
 *     (на плате нет выведенных D20/D21). Разрешение 128x32 (по умолчанию) или 128x64: OLED_HEIGHT.
 *   - экраны собираются в буфер строк и выводятся постранично: при 4 строках (128x32)
 *     длинные экраны сами листаются (заголовок остаётся, остальные строки меняются каждые 2,5 с)
 *   - тахометры каналов 4 и 6 перенесены с D50/D52 на D63 (A9) и D64 (A10), AUX-2
 *
 * Что нового в v0.3.3 (относительно v0.3.2):
 *   - поддержка OLED SSD1306 по I2C (например GM009605v4, 0.96", 128x64): DISPLAY_TYPE 1 (по умолчанию)
 *     (в v0.3.4 это уже программный I2C на AUX-3, см. выше). DISPLAY_TYPE 0 - прежний Nokia 5110.
 *   - тахометр канала 9 возвращён на A12 (D66, AUX-2)
 *
 * Что нового в v0.3.2 (относительно v0.3.1):
 *   - DISPLAY_LAYOUT 3 (по умолчанию): AUX-2 разведён зеркально к EXP1, так что кабель дисплея,
 *     распаянный под EXP1, ложится на AUX-2 позиция в позицию (GND->GND, 5V->5V)
 *
 * Что нового в v0.3.1 (относительно v0.3):
 *   - дисплей по умолчанию на AUX-2 (программный SPI), AUX-2 только под дисплей
 *   - FUN_PWM_2 перенесён с D44 на D3 (концевик X-), кнопка с D3 перенесена (с v0.3.6 она на D24)
 *   - термистор CASE перенесён с A9 на A2; запасной EXT по умолчанию на A6 (выключен)
 *   - тахометр канала 9 на D20 (AUX-1, аппаратное прерывание); добавлена поддержка D20/D21
 *
 * Что нового в v0.3 (относительно v0.2.1):
 *   - 10 каналов: 6 x 4-pin (ШИМ 25 кГц) + 4 x 3-pin (через MOSFET)
 *   - 5 термисторов (3 штатных входа платы TH1/TH2/TB + A9 + запасной EXT),
 *     у каждого своя подтяжка; датчик, которого нет - ни на что не влияет
 *   - тахометры (RPM) по всем каналам, только информационные; нет сигнала - нет вреда
 *   - дисплей Nokia 5110 на программном SPI, четыре варианта разводки (DISPLAY_LAYOUT 0/1/2/3)
 *   - ШИМ D9/D10 (3-pin) на ~31 кГц вместо 490 Гц (нет писка)
 *   - FUN_PWM_3 перенесён с D45 на D5 (разъём SERVOS2)
 *
 * ---------------------------------------------------------------------
 * КАНАЛЬНАЯ КАРТА (индексы для SET=ch,pct; 0-6 совпадают с v0.2.1, кроме
 * FUN_PWM_3: теперь D5, а не D45)
 * ---------------------------------------------------------------------
 *   idx  Имя        Пин  Тип           Разъём / терминал      По умолчанию (TEMP_SERIAL)
 *   0    FUN_PWM_0  D11  4-pin 25 кГц  SERVOS1                GPU fans  <- Serial:P102 (ЗАФИКСИРОВАНО)
 *   1    FUN_PWM_1  D6   4-pin 25 кГц  SERVOS1                Вдув      <- Therm:CASE  (ЗАФИКСИРОВАНО)
 *   2    FUN_PWM_2  D3   4-pin 25 кГц  концевик X-            свободно (кнопка с v0.3.6 на D24)
 *   3    FUN_PWM_3  D5   4-pin 25 кГц  SERVOS2                свободно
 *   4    FUN_DC_0   D8   3-pin MOSFET  терминал BED           Выдув     <- Therm:CASE  (ЗАФИКСИРОВАНО)
 *   5    FUN_DC_3   D9   3-pin MOSFET  терминал FAN           свободно
 *   6    FUN_DC_1   D10  3-pin MOSFET  терминал E0 (HE0)      свободно
 *   7    FUN_PWM_4  D2   4-pin 25 кГц  концевик X+            свободно (проверь осциллографом, см. ниже)
 *   8    FUN_PWM_5  D46  4-pin 25 кГц  колодка драйвера Z, STEP свободно
 *   9    FUN_DC_2   D7   3-pin MOSFET  терминал E1 (HE1)      свободно
 *   Имена DC-каналов идут по порядку разъёмов (BED, E0, E1, FAN), а не по idx (см. v0.3.17).
 *   D12 (PS_ON) под вентиляторы не используется. D3 теперь канал FUN_PWM_2, кнопка на D24.
 *   D2 (вход концевика) может иметь на плате фильтр дребезга - проверь форму
 *   ШИМ на выходе. Если искажена - канал FUN_PWM_4 просто не используй.
 *
 * ---------------------------------------------------------------------
 * ТЕРМИСТОРЫ (NTC 100K B3950)
 * ---------------------------------------------------------------------
 *   idx  Имя   Пин   Подтяжка к 5 В  Включён
 *   0    CASE  A0    100 кОм внешняя  да   (колодка драйвера X, STEP, D54)
 *   1    TH1   A13   4.7 кОм на плате да   (разъём TH1)
 *   2    TH2   A15   4.7 кОм на плате да   (разъём TH2)
 *   3    TB    A14   4.7 кОм на плате да   (разъём TB)
 *   4    EXT   A1    100 кОм внешняя  да   (колодка драйвера X, DIR, D55)
 *   Подтяжку 4.7 кОм на TH1/TH2/TB проверь мультиметром (между сигналом и +5 В
 *   при выключенном питании); если другая - поправь THERM_SERIES_OHM.
 *
 *   Состояния датчика (правило "отсутствие датчика ничему не вредит"):
 *     OFF    - выключен в конфиге, пин не читается вообще
 *     ABSENT - ни разу не получили нормальных показаний с момента включения
 *              (не подключен). Каналы на нём стоят на NO_SENSOR_PERCENT, ошибки нет
 *     OK     - нормальные показания (THERM_GOOD_SAMPLES подряд)
 *     FAULT  - БЫЛ в порядке, потом пропал (обрыв/КЗ/выдернут): каналы на 100%
 *              (или на CASE_FAULT_FORCE_PERCENT, если температура нереально низкая)
 *   Вернулся - снова OK. Датчик, неподключённый с самого начала, аварии не вызывает.
 *   Нюанс: обрыв провода ДО включения питания выглядит как ABSENT, а не FAULT.
 *
 * ---------------------------------------------------------------------
 * ТАХОМЕТРЫ (только чтение, на управление НЕ влияют)
 * ---------------------------------------------------------------------
 *   Вход с подтяжкой INPUT_PULLUP, считаются спады, 2 импульса на оборот.
 *   idx  Канал      Tach-пин  Где
 *   0    FUN_PWM_0  D18       концевик Z-   (аппаратное прерывание)
 *   1    FUN_PWM_1  D19       концевик Z+   (аппаратное прерывание)
 *   2    FUN_PWM_2  D14       концевик Y-   (PCINT)
 *   3    FUN_PWM_3  D15       концевик Y+   (PCINT)
 *   4    FUN_DC_0   A9 (D63)  AUX-2         (PCINT)
 *   5    FUN_DC_3   A12 (D66) AUX-2         (PCINT; при DISPLAY_TYPE 0 и Nokia на AUX-2 пин D66 свободен)
 *   6    FUN_DC_1   D51       AUX-3 / EXP2  (PCINT)
 *   7    FUN_PWM_4  D53       AUX-3 / EXP2  (PCINT)
 *   8    FUN_PWM_5  A8 (D62)  колодка драйвера Z, ENABLE (PCINT)
 *   9    FUN_DC_2   A10 (D64) AUX-2         (PCINT)
 *   (D50 и D52 заняты OLED. Если DISPLAY_TYPE 0 и Nokia на D63/D64 - эти тахо отключатся сами.)
 *   Состояния: NONE (пин не задан/занят), NOSIG (импульсов не было с включения),
 *   OK (есть RPM), STALL (импульсы были, потом пропали при PWM >= TACH_STALL_MIN_PERCENT
 *   дольше TACH_STALL_AFTER_MS). STALL - только индикация, управление не меняется.
 *   Tach 3-pin вентилятора при ШИМ по минусу (MOSFET) недостоверен: у каналов 4,5,6,9
 *   RPM показывается только при 100% PWM (состояние PWM иначе).
 *   Пин, занятый дисплеем, термистором, выходом вентилятора или кнопкой,
 *   автоматически исключается (сообщение TACHCFG=SKIP в Serial при старте).
 *
 * ---------------------------------------------------------------------
 * ДИСПЛЕЙ Nokia 5110 (DISPLAY_LAYOUT)
 * ---------------------------------------------------------------------
 *   3 (по умолчанию) - AUX-2, программный SPI, позиции как у EXP1 (кабель от EXP1 без перепайки):
 *        CLK=D40, DIN=D64, DC=D63, CE=D59, RST=D44, VCC/BL=5V, GND
 *        Соответствие позиций AUX-2 -> EXP1: GND->GND, 5V->5V, D63->D27, D59->D29,
 *        D40->D23, D64->D25, D42->D17, D44->D16, D65->D37, D66->D35
 *   2 - AUX-2, программный SPI, другая разводка:
 *        CLK=D40, DIN=D42, CE=D65, RST=D59, DC=D64, VCC/BL=5V, GND
 *        (ряды AUX-2: сверху GND, D63, D40, D42, D65; снизу 5V, D59, D64, D44, D66)
 *   1 - EXP1, программный SPI:
 *        CLK=D23, DIN=D25, DC=D27, CE=D29, RST=D16, VCC/BL=5V, GND
 *   0 - старая разводка: RST=D4, CE=D5, DC=D14, DIN=D51, CLK=D52 (аппаратный SPI).
 *        При этом каналы/тахо, использующие D5, D14, D51, D52, D53, автоматически
 *        отключаются (пины заняты дисплеем).
 *
 * ---------------------------------------------------------------------
 * РЕЖИМЫ КОНТРОЛЛЕРА (ControllerMode) - 2 режима, переключение: долгое
 * нажатие кнопки (D24), либо MODE=n
 * ---------------------------------------------------------------------
 *   0 = TEMP_SERIAL (по умолчанию):
 *       - FUN_PWM_0 (GPU)    всегда <- Serial:P102
 *       - FUN_PWM_1 (вдув)   всегда <- Therm:CASE
 *       - FUN_DC_0  (выдув, D8) всегда <- Therm:CASE
 *       - остальные 7 каналов - привязку можно менять командой BIND=
 *   1 = TEMP_MANUAL: привязку можно менять у ЛЮБОГО канала.
 *   При переключении В TEMP_SERIAL привязка GPU/вдува/выдува принудительно
 *   возвращается к стандартной.
 *
 * ---------------------------------------------------------------------
 * Serial-протокол (115200 8N1), команды разделены '\n'
 * ---------------------------------------------------------------------
 *   SET=ch,pct        -- ручная установка канала ch (0-9) на pct% (0-100).
 *   TEMP=K:V,K:V,...  -- Serial-температуры. Ключи: P102, GTX, CPU, SSD.
 *                        Пример: TEMP=P102:52,GTX:45,CPU:61,SSD:38
 *   MODE=n / MODE?    -- режим контроллера (0=TEMP_SERIAL, 1=TEMP_MANUAL)
 *   CURVES?           -- все кривые: CURVE=Имя,idx,tempC,percent ... CURVE=END
 *   CURVE=Имя,idx,tempC,percent -- править точку (idx 0-4). Пример: CURVE=FUN_PWM_1,2,42,45
 *   CURVE=SAVE/RESET  -- сохранить в EEPROM / вернуть заводские
 *   BINDS?            -- привязки: BIND=Имя,THERM|SERIAL,имя,LOCKED|FREE ... BIND=END
 *   BIND=Имя,THERM,имяТермистора (CASE,TH1,TH2,TB,EXT) | BIND=Имя,SERIAL,ключ
 *   BIND=SAVE/RESET   -- сохранить в EEPROM / вернуть заводские
 *   THERMS?           -- термисторы: THERM=имя,включён,значение,состояние ... THERM=END
 *   TACH?             -- тахометры: TACH=имя,пин,состояние,rpm ... TACH=END
 *   TACHRAW=n         -- отладка tach канала n: TACHRAW=n,отброшено,dt1..dt7 (мкс между последними импульсами)
 *   TACHTEST          -- тест тахо 3-pin: каналы 4,5,6,9 -> 100% на 5 с, замер, возврат как было
 *   STATUS?           -- всё сразу (режим, Serial, термисторы, каналы, тахо, watchdog)
 *
 * Гистерезис: канал, ведомый кривой, меняет % только если новое значение
 * отличается от текущего на CURVE_HYSTERESIS_PERCENT и больше. Fail-safe - точно.
 *
 * EEPROM: раскладка изменилась (10 каналов). Старые сохранённые кривые и
 * привязки v0.2.1 игнорируются (другие magic) - после прошивки снова заводские.
 *
 * Безопасность:
 *  - Serial-канал: данных нет дольше DATA_TIMEOUT_MS -> 100% (watchdog).
 *  - Термисторный канал: см. состояния датчика выше. Не зависит от watchdog.
 *  - FAILSAFE_ENABLED=false отключает ПРИНУДИТЕЛЬНОЕ выставление % на старте и
 *    по Serial-watchdog (для тестов). Индикация FAILSAFE на дисплее остаётся.
 * ====================================================================== */

#include <avr/io.h>
#include <avr/interrupt.h>
#include <string.h>
#include <stdio.h>
#include <EEPROM.h>
#include <SPI.h>
#include <Wire.h>

// ============================= КОНФИГУРАЦИЯ =============================

// Тип дисплея: 1 = OLED SSD1306 по программному I2C (U8g2) на AUX-3, 0 = Nokia 5110 (см. DISPLAY_LAYOUT ниже)
#define DISPLAY_TYPE 1
// Высота OLED в пикселях: 32 (128x32) или 64 (128x64)
#define OLED_HEIGHT 32
// 1 = перевернуть изображение на 180 градусов
#define OLED_FLIP 0
#if DISPLAY_TYPE == 1
#include <U8g2lib.h>
#else
#include <Adafruit_GFX.h>
#include <Adafruit_PCD8544.h>
#endif

// 1 = дисплей на EXP1 (программный SPI), 0 = старая разводка SERVOS2 + AUX-3
#define DISPLAY_LAYOUT 3

#define DC_MIN_RUN_PCT 20        // нижний предел для DC-канала по кривой (стоп ~14%, старт ~37% - измерено на 31 Гц), 0 = выключено
#define DC_KICK_BELOW_PCT 45     // с места до этого % DC-вентилятор не стартует сам - нужен толчок
#define DC_KICK_MS 3000UL        // длительность толчка 100%
#define DC_DEFAULT_HZ 31         // частота ШИМ 3-pin DC каналов по умолчанию, Гц (программный ШИМ, см. DCFREQ)

// ---- Тайминги (всё настраивается здесь) ----
const unsigned long DEBOUNCE_MS         = 30;    // антидребезг кнопки
const unsigned long LONG_PRESS_MS       = 5000;  // удержание кнопки -> смена режима контроллера
const unsigned long INFO_IDLE_REVERT_MS = 5000;  // простой -> возврат на дефолтный экран
const bool INFO_IDLE_REVERT_ENABLED = false;     // автовозврат на дефолтный экран по таймауту
const unsigned long DATA_TIMEOUT_MS     = 8000;  // нет Serial-данных -> fail-safe 100% (только Serial-каналы)
const bool FAILSAFE_ENABLED = true;              // false - для тестов: не форсировать 100% (старт и watchdog)
const unsigned long DISPLAY_REFRESH_MS  = (DISPLAY_TYPE == 1) ? 500 : 250;   // период перерисовки дисплея (программный I2C медленный)
const unsigned long LOCAL_CURVE_PERIOD_MS = 1000; // период пересчёта термисторных каналов
const uint8_t CURVE_HYSTERESIS_PERCENT = 2;       // гасит дрожание % от шума АЦП

// ---- Пины ----
const uint8_t PIN_BUTTON  = 24;   // кнопка на GND, INPUT_PULLUP. Колодка драйвера E0: ENABLE (D24) и GND той же колодки

#if DISPLAY_TYPE == 1
const uint8_t PIN_OLED_SDA = 50;   // AUX-3 (MISO)
const uint8_t PIN_OLED_SCL = 52;   // AUX-3 (SCK)
const uint8_t DISPLAY_PINS[] = { 50, 52 };
#else
const uint16_t TEXT_ON = BLACK;              // у PCD8544 BLACK = пиксель включён
#endif
const uint8_t LCD_CONTRAST = 23;  // подобрано по факту для конкретной панели (обычно 40-60, у этой - 23)
const uint8_t LCD_ROTATION = 0;   // 0/1/2/3 = поворот экрана на 0/90/180/270 град.

#if DISPLAY_TYPE == 1
// пины I2C заданы выше
#elif DISPLAY_LAYOUT == 3
const uint8_t PIN_LCD_CLK = 40;   // AUX-2 (позиция EXP1 D23)
const uint8_t PIN_LCD_DIN = 64;   // AUX-2 (A10, позиция EXP1 D25)
const uint8_t PIN_LCD_DC  = 63;   // AUX-2 (A9,  позиция EXP1 D27)
const uint8_t PIN_LCD_CE  = 59;   // AUX-2 (A5,  позиция EXP1 D29)
const uint8_t PIN_LCD_RST = 44;   // AUX-2 (позиция EXP1 D16)
const uint8_t DISPLAY_PINS[] = { 40, 64, 63, 59, 44 };
#elif DISPLAY_LAYOUT == 2
const uint8_t PIN_LCD_CLK = 40;   // AUX-2
const uint8_t PIN_LCD_DIN = 42;   // AUX-2
const uint8_t PIN_LCD_CE  = 65;   // AUX-2 (A11)
const uint8_t PIN_LCD_RST = 59;   // AUX-2 (A5)
const uint8_t PIN_LCD_DC  = 64;   // AUX-2 (A10)
const uint8_t DISPLAY_PINS[] = { 40, 42, 65, 59, 64 };
#elif DISPLAY_LAYOUT == 1
const uint8_t PIN_LCD_CLK = 23;   // EXP1
const uint8_t PIN_LCD_DIN = 25;   // EXP1
const uint8_t PIN_LCD_DC  = 27;   // EXP1
const uint8_t PIN_LCD_CE  = 29;   // EXP1
const uint8_t PIN_LCD_RST = 16;   // EXP1
const uint8_t DISPLAY_PINS[] = { 23, 25, 27, 29, 16 };
#else
const uint8_t PIN_LCD_RST = 4;    // SERVOS2
const uint8_t PIN_LCD_CE  = 5;    // SERVOS2
const uint8_t PIN_LCD_DC  = 14;   // Y_MIN
// hardware SPI: DIN=D51(MOSI), CLK=D52(SCK); D53 (SS) библиотека SPI делает выходом
const uint8_t DISPLAY_PINS[] = { 4, 5, 14, 51, 52, 53 };
#endif

// ---- Кривые охлаждения: темп. в °C -> % оборотов ----
struct CurvePoint { int16_t tempC; uint8_t percent; };
const uint8_t CURVE_POINTS = 5;

// ---- Каналы вентиляторов (порядок idx НЕ менять, см. канальную карту) ----
struct FanChannel {
  const char* name;       // FUN_PWM_x / FUN_DC_x - для Serial-протокола (CURVE=/BIND=)
  uint8_t pin;             // Arduino-пин
  volatile uint16_t* ocr;  // не NULL -> аппаратный 25кГц ШИМ через этот OCR-регистр; NULL -> analogWrite(pin,...)
};

const FanChannel FAN_CHANNELS[] = {
  { "FUN_PWM_0", 11, &OCR1A },   // idx0, D11 (SERVOS1)       4-pin. GPU fans
  { "FUN_PWM_1", 6,  &OCR4A },   // idx1, D6  (SERVOS1)       4-pin. Вдув
  { "FUN_PWM_2", 3,  &OCR3C },   // idx2, D3  (X-)            4-pin
  { "FUN_PWM_3", 5,  &OCR3A },   // idx3, D5  (SERVOS2)       4-pin
  { "FUN_DC_0",  8,  &OCR4C },   // idx4, D8  (BED)           3-pin. Выдув
  { "FUN_DC_3",  9,  nullptr },  // idx5, D9  (FAN)           3-pin
  { "FUN_DC_1",  10, nullptr },  // idx6, D10 (E0/HE0)        3-pin
  { "FUN_PWM_4", 2,  &OCR3B },   // idx7, D2  (X+)            4-pin
  { "FUN_PWM_5", 46, &OCR5A },   // idx8, D46 (драйвер Z STEP) 4-pin
  { "FUN_DC_2",  7,  &OCR4B },   // idx9, D7  (E1/HE1)        3-pin
};
const uint8_t TOTAL_CHANNELS = sizeof(FAN_CHANNELS) / sizeof(FAN_CHANNELS[0]); // 10

const uint8_t CH_GPU     = 0; // FUN_PWM_0
const uint8_t CH_INTAKE  = 1; // FUN_PWM_1
const uint8_t CH_EXHAUST = 4; // FUN_DC_0 (D8)

// Заводские кривые на каждый канал. "Свободным" каналам дана мягкая
// корпусная кривая - они по умолчанию ни к чему физически не подключены.
const CurvePoint CURVE_DEFAULT[][CURVE_POINTS] = {
  /* idx0 FUN_PWM_0 GPU    */ { {30,20},{50,35},{65,55},{75,80},{85,100} },
  /* idx1 FUN_PWM_1 вдув   */ { {28,15},{35,25},{42,40},{50,60},{55,100} },
  /* idx2 FUN_PWM_2 free   */ { {28,15},{35,25},{42,40},{50,60},{55,100} },
  /* idx3 FUN_PWM_3 free   */ { {28,15},{35,25},{42,40},{50,60},{55,100} },
  /* idx4 FUN_DC_0  выдув  */ { {28,20},{35,35},{42,55},{48,80},{52,100} },
  /* idx5 FUN_DC_3  free   */ { {28,20},{35,35},{42,55},{48,80},{52,100} },
  /* idx6 FUN_DC_1  free   */ { {28,20},{35,35},{42,55},{48,80},{52,100} },
  /* idx7 FUN_PWM_4 free   */ { {28,15},{35,25},{42,40},{50,60},{55,100} },
  /* idx8 FUN_PWM_5 free   */ { {28,15},{35,25},{42,40},{50,60},{55,100} },
  /* idx9 FUN_DC_2  free   */ { {28,20},{35,35},{42,55},{48,80},{52,100} },
};
CurvePoint channelCurve[TOTAL_CHANNELS][CURVE_POINTS]; // рабочие таблицы в RAM

// ---- Источник температуры для канала: термистор или Serial-значение ----
enum SourceType : uint8_t { SRC_THERM = 0, SRC_SERIAL = 1 };
struct ChannelSource { SourceType type; uint8_t index; };

const ChannelSource CHANNEL_SOURCE_DEFAULT[] = {
  /* idx0 FUN_PWM_0 GPU    */ { SRC_SERIAL, 0 }, // serialValues[0] = P102
  /* idx1 FUN_PWM_1 вдув   */ { SRC_THERM,  0 }, // thermValues[0]  = CASE
  /* idx2 FUN_PWM_2 free   */ { SRC_THERM,  0 },
  /* idx3 FUN_PWM_3 free   */ { SRC_THERM,  0 },
  /* idx4 FUN_DC_0  выдув  */ { SRC_THERM,  0 },
  /* idx5 FUN_DC_3  free   */ { SRC_THERM,  0 },
  /* idx6 FUN_DC_1  free   */ { SRC_THERM,  0 },
  /* idx7 FUN_PWM_4 free   */ { SRC_THERM,  0 },
  /* idx8 FUN_PWM_5 free   */ { SRC_THERM,  0 },
  /* idx9 FUN_DC_2  free   */ { SRC_THERM,  0 },
};
ChannelSource channelSource[TOTAL_CHANNELS]; // рабочие привязки в RAM

// ---- Термисторы ----
// Делитель: 5V --[подтяжка]-- (ADC-пин) --[NTC]-- GND
const uint8_t NUM_THERMISTORS = 5;
const uint8_t PIN_THERM[NUM_THERMISTORS]         = { A0, A13, A15, A14, A1 };  // CASE: A0 (X STEP); EXT: A1 (X DIR)
const bool    THERM_ENABLED[NUM_THERMISTORS]     = { true, true, true, true, true };
const float   THERM_SERIES_OHM[NUM_THERMISTORS]  = { 100000.0, 4700.0, 4700.0, 4700.0, 100000.0 };
const char*   THERM_NAMES[NUM_THERMISTORS]       = { "CASE", "TH1", "TH2", "TB", "EXT" };

// Термистор от хотэнда 3D-принтера без маркировки - типовой NTC 100K B3950.
const float THERM_NOMINAL_OHM = 100000.0; // сопротивление NTC при 25°C
const float THERM_NOMINAL_C   = 25.0;
const float THERM_BETA        = 3950.0;

// Ниже этой температуры датчик считается неисправным/отключённым
// (в корпусе компьютера такого быть не должно).
const float MIN_VALID_AMBIENT_C        = 0.0;
const uint8_t CASE_FAULT_FORCE_PERCENT = 50;  // % канала, если температура нереально низкая (FAULT)

// Отсутствие датчика
const uint8_t THERM_GOOD_SAMPLES = 3;   // сколько нормальных отсчётов подряд -> датчик OK
const uint8_t THERM_BAD_SAMPLES  = 3;   // сколько плохих подряд -> FAULT (если был OK)
const uint8_t NO_SENSOR_PERCENT  = 50;  // % канала, привязанного к датчику, которого нет (ABSENT/OFF)

enum ThermState : uint8_t { TS_OFF = 0, TS_ABSENT = 1, TS_OK = 2, TS_FAULT = 3 };
const char* THERM_STATE_NAMES[4] = { "OFF", "ABSENT", "OK", "FAULT" };
float      thermValues[NUM_THERMISTORS];     // NAN пока датчик не OK
ThermState thermState[NUM_THERMISTORS];
uint8_t    thermGood[NUM_THERMISTORS];
uint8_t    thermBad[NUM_THERMISTORS];
uint8_t    thermFaultKind[NUM_THERMISTORS];  // 1 = граница АЦП (обрыв/КЗ), 2 = нереально низкая темп.

// ---- Тахометры ----
const uint8_t TACH_NO_PIN = 0xFF;
const uint8_t TACH_PIN[TOTAL_CHANNELS] = { 18, 19, 14, 15, 63, A12, 51, 53, A8, 64 };  // см. таблицу в шапке
const uint8_t  TACH_PPR = 2;                     // импульсов на оборот
const unsigned long TACH_WINDOW_MS = 1000;       // окно усреднения RPM
const uint32_t TACH_MIN_HIGH_US = 1500;          // перед спадом линия должна быть в "1" не меньше (отсекает ложные спады на подъёме)
const uint32_t TACH_MIN_PERIOD_US = 2500;        // отсечка помех: импульсы чаще 400 Гц игнорируются
const uint8_t  TACH_STALL_MIN_PERCENT = 30;      // STALL оценивается только при PWM не ниже этого
const unsigned long TACH_STALL_AFTER_MS = 5000;  // и если импульсов нет дольше

const uint16_t TACH_MAX_RPM = 6000;             // выше - считаем помехой (пустой вход, наводки)
// Каналы с MOSFET по минусу (3-pin): tach достоверен только при 100% PWM
const bool TACH_NEEDS_FULL[TOTAL_CHANNELS] = { false, false, false, false, true, true, true, false, false, true };

enum TachState : uint8_t { TACH_NONE = 0, TACH_NOSIG = 1, TACH_OK = 2, TACH_STALL = 3, TACH_PWM = 4 };
const char* TACH_STATE_NAMES[5] = { "NONE", "NOSIG", "OK", "STALL", "PWM" };

// Тест тахо 3-pin (команда TACHTEST)
const unsigned long TACHTEST_SPINUP_MS = 5000;   // сколько держать 100% перед замером
bool tachTestActive = false;
unsigned long tachTestStart = 0;
uint8_t tachTestSaved[TOTAL_CHANNELS];

// ---- Ручное удержание процентов (HOLD=) и диагностика тахо (TACHDUMP=) ----
const unsigned long HOLD_DEFAULT_SEC = 30;
const unsigned long HOLD_MAX_SEC     = 300;
bool holdActive[TOTAL_CHANNELS];                 // false при старте (глобальные обнуляются)
unsigned long holdUntilMs[TOTAL_CHANNELS];
void releaseHold(uint8_t ch, const __FlashStringHelper* why);   // определена ниже

const uint16_t TACHDUMP_MAX_BINS = 1000;
const uint16_t TACHDUMP_DEF_BIN_US = 1000;       // 1 мс на бин -> 1 с записи
const uint16_t TACHFAST_BIN_US = 50;             // 50 мкс на бин -> 50 мс записи
uint8_t dumpTrans[TACHDUMP_MAX_BINS];            // число переходов в бине (насыщение 255)
uint8_t dumpHigh[TACHDUMP_MAX_BINS];             // доля времени в "1" (0..255)

// ---- События платы (EVENT=...) ----
const unsigned long HEARTBEAT_MS = 5000;
const unsigned long THERM_EVENT_MIN_GAP_MS = 3000;   // чтобы мерцающий пустой вход не спамил
unsigned long lastHeartbeatMs = 0;
unsigned long thermEventLastMs[5];

// ---- Именованные Serial-значения (ключи протокола TEMP=) ----
const uint8_t NUM_SERIAL_KEYS = 4;
const char* SERIAL_KEY_NAMES[NUM_SERIAL_KEYS] = { "P102", "GTX", "CPU", "SSD" };
const uint8_t SK_P102 = 0, SK_GTX = 1, SK_CPU = 2, SK_SSD = 3;
float serialValues[NUM_SERIAL_KEYS] = { 0, 0, 0, 0 };

// ========================================================================

#if DISPLAY_TYPE == 1
#if OLED_FLIP
#define OLED_ROT U8G2_R2
#else
#define OLED_ROT U8G2_R0
#endif
#if OLED_HEIGHT == 64
U8G2_SSD1306_128X64_NONAME_F_SW_I2C display(OLED_ROT, PIN_OLED_SCL, PIN_OLED_SDA, U8X8_PIN_NONE);
#else
U8G2_SSD1306_128X32_UNIVISION_F_SW_I2C display(OLED_ROT, PIN_OLED_SCL, PIN_OLED_SDA, U8X8_PIN_NONE);
#endif
#elif DISPLAY_LAYOUT >= 1
Adafruit_PCD8544 display(PIN_LCD_CLK, PIN_LCD_DIN, PIN_LCD_DC, PIN_LCD_CE, PIN_LCD_RST);
#else
Adafruit_PCD8544 display(PIN_LCD_DC, PIN_LCD_CE, PIN_LCD_RST);
#endif

// ---------------------------- Режим контроллера ----------------------------
enum ControllerMode : uint8_t { TEMP_SERIAL = 0, TEMP_MANUAL = 1, CTRL_MODE_COUNT = 2 };
const char* CTRL_MODE_NAMES[CTRL_MODE_COUNT] = { "TMP-SRL", "TMP-MAN" };
ControllerMode controllerMode = TEMP_SERIAL;

// ---------------------------- EEPROM ----------------------------
const int EEPROM_ADDR_MAGIC    = 0;  // режим контроллера
const int EEPROM_ADDR_MODE     = 1;
const uint8_t EEPROM_MAGIC_VAL = 0xA5;

const int EEPROM_ADDR_CURVE_MAGIC  = 2;
const int EEPROM_ADDR_CURVES_START = 3; // TOTAL_CHANNELS*CURVE_POINTS точек подряд
const uint8_t EEPROM_CURVE_MAGIC_VAL = 0x5D; // v0.3: другой magic (было 0x5C), раскладка на 10 каналов

const int EEPROM_ADDR_BIND_MAGIC  = EEPROM_ADDR_CURVES_START + (int)(TOTAL_CHANNELS * CURVE_POINTS * sizeof(CurvePoint));
const int EEPROM_ADDR_BINDS_START = EEPROM_ADDR_BIND_MAGIC + 1; // TOTAL_CHANNELS привязок подряд
const int EEPROM_ADDR_DCF_MAGIC   = EEPROM_ADDR_BINDS_START + (int)(TOTAL_CHANNELS * sizeof(ChannelSource));
const int EEPROM_ADDR_DCF_START   = EEPROM_ADDR_DCF_MAGIC + 1;  // TOTAL_CHANNELS частот (uint16, Гц) подряд
const uint8_t EEPROM_DCF_MAGIC_VAL = 0x7A;
const int EEPROM_ADDR_SPIN_MAGIC  = EEPROM_ADDR_DCF_START + (int)(TOTAL_CHANNELS * 2);
const int EEPROM_ADDR_SPIN_START  = EEPROM_ADDR_SPIN_MAGIC + 1;            // TOTAL_CHANNELS байт: точки раскрутки
const int EEPROM_ADDR_SPIN_PARAM  = EEPROM_ADDR_SPIN_START + (int)TOTAL_CHANNELS;  // boost, step, tries (по байту), rampMs, probeS (uint16)
const uint8_t EEPROM_SPIN_MAGIC_VAL = 0x5B;
const uint8_t EEPROM_BIND_MAGIC_VAL = 0x3F;  // v0.3: другой magic (было 0x3E)

// ---------------------------- Watchdog ----------------------------
unsigned long lastDataMillis = 0;
bool watchdogTripped = false;

// ---------------------------- Каналы: текущее состояние ----------------------------
uint8_t channelPercent[TOTAL_CHANNELS]; // текущий выставленный %
bool    chActive[TOTAL_CHANNELS];       // false - пин занят дисплеем, выход не используется
unsigned long lastLocalCurveMillis = 0;

// ---------------------------- Тахометры: состояние ----------------------------
volatile uint32_t tachCnt[TOTAL_CHANNELS];
volatile uint32_t tachTotal[TOTAL_CHANNELS];   // импульсов за всё время (не сбрасывается) - для проверки автозапуска
volatile uint32_t tachLastUs[TOTAL_CHANNELS];
bool          tachActive[TOTAL_CHANNELS];
bool          tachSeen[TOTAL_CHANNELS];
bool          tachFullSoFar[TOTAL_CHANNELS];  // в текущем окне канал был все время на 100%
bool          tachValid[TOTAL_CHANNELS];      // результат последнего окна годен для показа
uint16_t      tachRpm[TOTAL_CHANNELS];
unsigned long tachLastPulseMs[TOTAL_CHANNELS];
unsigned long tachWindowStart = 0;

// PCINT-группы: 0 = порт B (D50-D53), 1 = порт J (D14, D15), 2 = порт K (A8-A15)
uint8_t tgN[3];
uint8_t tgPinMask[3][8];
uint8_t tgChan[3][8];
volatile uint8_t tgLast[3];
uint8_t tgPcmsk[3];
volatile uint32_t tachDbgUs[TOTAL_CHANNELS][8];   // метки времени последних принятых импульсов (кольцо)
volatile uint8_t  tachDbgIdx[TOTAL_CHANNELS];
volatile uint16_t tachRejected[TOTAL_CHANNELS];   // отброшено фильтром помех
volatile uint32_t tachBurstStartUs[TOTAL_CHANNELS]; // защита от шторма: начало текущего 100-мс окна
volatile uint16_t tachBurstN[TOTAL_CHANNELS];       // отброшенных импульсов в окне
volatile bool     tachTrip[TOTAL_CHANNELS];         // ISR отключил вход (шторм), loop() ещё не обработал
bool              tachGuardOn[TOTAL_CHANNELS];      // вход временно отключён защитой
unsigned long     tachGuardUntilMs[TOTAL_CHANNELS];
unsigned long     tachGuardEventMs[TOTAL_CHANNELS];
volatile uint32_t tachRiseUs[TOTAL_CHANNELS];     // момент последнего подъёма (0->1) линии
uint8_t tachGrp[TOTAL_CHANNELS];       // PCINT-группа канала (0..2), 255 - не PCINT
uint8_t tachPcmskBit[TOTAL_CHANNELS]; // бит в PCMSKx
volatile int8_t tachChanD18 = -1;
volatile int8_t tachChanD19 = -1;
volatile int8_t tachChanD20 = -1;
volatile int8_t tachChanD21 = -1;

// ---------------------------- Кнопка ----------------------------
bool buttonRawLast      = HIGH;
bool buttonStable        = HIGH;
unsigned long lastDebounceTime = 0;
bool buttonIsDown        = false;
unsigned long pressStartMillis = 0;
bool longPressFired      = false;
volatile bool evShortPress = false;   // события кнопки: ставит прерывание, разбирает loop()
volatile bool evLongPress  = false;

// ---------------------------- Экраны дисплея ----------------------------
enum InfoScreen : uint8_t { SCR_DEFAULT = 0, SCR_GPU, SCR_CPU, SCR_SSD, SCR_CASE, SCR_TEMPS, SCR_FANS_A, SCR_FANS_B, SCR_COUNT };
InfoScreen currentScreen = SCR_DEFAULT;
unsigned long lastScreenChangeMillis = 0;
unsigned long lastDisplayRefresh = 0;

// ---------------------------- Serial line buffer ----------------------------
char serialBuf[64];
uint8_t serialBufLen = 0;

// ============================================================================
//                         ФУНКЦИИ: ПИНЫ / ПРОВЕРКИ КОНФЛИКТОВ
// ============================================================================

bool pinReservedByDisplay(uint8_t pin) {
  for (uint8_t i = 0; i < sizeof(DISPLAY_PINS); i++) if (DISPLAY_PINS[i] == pin) return true;
  return false;
}

// Активен ли выход вентилятора на этом пине (есть в таблице каналов и не занят дисплеем)
bool outPinActive(uint8_t pin) {
  for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) if (FAN_CHANNELS[i].pin == pin) return chActive[i];
  return false;
}

bool pinIsThermistor(uint8_t pin) {
  for (uint8_t i = 0; i < NUM_THERMISTORS; i++) if (THERM_ENABLED[i] && PIN_THERM[i] == pin) return true;
  return false;
}

bool pinIsFanOutput(uint8_t pin) {
  for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) if (chActive[i] && FAN_CHANNELS[i].pin == pin) return true;
  return false;
}

// ============================================================================
//                              ФУНКЦИИ: ТАЙМЕРЫ
// ============================================================================

// ---- Программный ШИМ 3-pin DC каналов (D7, D8, D9, D10): у каждого канала своя частота ----
// Тик 4 кГц от Timer2 (CTC). Период канала = swPeriod тиков, выход включён первые swOn тиков периода.
const uint16_t SWPWM_TICK_HZ = 4000;
const uint16_t DC_HZ_MIN = 15;
const uint16_t DC_HZ_MAX = 300;
volatile uint16_t swOn[TOTAL_CHANNELS];       // тиков "включено" за период
volatile uint16_t swPeriod[TOTAL_CHANNELS];   // период в тиках
volatile uint16_t swPh[TOTAL_CHANNELS];       // текущая фаза
bool swMode[TOTAL_CHANNELS];                  // канал ведётся программным ШИМом
volatile uint8_t* swPort[TOTAL_CHANNELS];

// ---- Автозапуск DC-вентиляторов (fs = fan supervision) ----
enum FsState : uint8_t { FS_IDLE = 0, FS_KICK, FS_BOOST, FS_RAMP, FS_PROBE, FS_VERIFY, FS_ERR };
const char* const FS_NAMES[] = { "OK", "KICK", "BOOST", "RAMP", "PROBE", "VERIFY", "ERR" };
const unsigned long FS_BOOST_MS = 2000;       // сколько держим точку раскрутки + boost
const unsigned long FS_PROBE_MS = 700;        // длина проверочного 100%
const unsigned long FS_BASE_DELAY_MS = 30;    // пропускаем начало (включение PCINT-гейта, дребезг)
const unsigned long FS_STOP_CHECK_MS = 250;   // окно "стоял ли вентилятор": нет импульсов -> стоял
const uint32_t      FS_EARLY_PULSES = 2;      // импульсов за первые FS_STOP_CHECK_MS - вентилятор крутится, проверку можно закончить сразу
const uint32_t      FS_MIN_PULSES = 4;        // импульсов за проверку - вентилятор крутится
uint8_t  fsState[TOTAL_CHANNELS];
uint8_t  fsTgt[TOTAL_CHANNELS];               // нужный % (кривая, с нижним пределом)
uint8_t  fsCur[TOTAL_CHANNELS];               // текущая ступень снижения
uint8_t  fsTries[TOTAL_CHANNELS];             // подряд неудачных попыток
unsigned long fsT0[TOTAL_CHANNELS];           // начало фазы
unsigned long fsNextProbe[TOTAL_CHANNELS];
uint32_t fsBase[TOTAL_CHANNELS];
uint32_t fsFirst[TOTAL_CHANNELS];
bool     fsBaseSet[TOTAL_CHANNELS];
bool     fsFirstDone[TOTAL_CHANNELS];
bool     fsSeen[TOTAL_CHANNELS];              // вентилятор на канале замечен (были импульсы)
bool     fsDisc[TOTAL_CHANNELS];              // разведочная проверка уже была
bool     fsInt = false;                       // setChannel вызывает сам автозапуск
uint8_t  spinStart[TOTAL_CHANNELS];           // точка раскрутки, %
uint8_t  spinBoost = 5, spinStep = 5, spinTries = 3;
uint16_t spinRampMs = 1000, spinProbeS = 60;
uint8_t swMask[TOTAL_CHANNELS];
uint8_t swList[TOTAL_CHANNELS];
volatile uint8_t swCount = 0;

uint16_t hzToPeriod(uint16_t hz) {
  if (hz < DC_HZ_MIN) hz = DC_HZ_MIN;
  if (hz > DC_HZ_MAX) hz = DC_HZ_MAX;
  return (uint16_t)((SWPWM_TICK_HZ + hz / 2) / hz);
}

uint16_t periodToHz(uint16_t per) {
  if (!per) return 0;
  return (uint16_t)((SWPWM_TICK_HZ + per / 2) / per);
}

// заполнение канала (проценты) -> тики; вызывается из основного кода
void swSetDuty(uint8_t ch, uint8_t pct) {
  uint16_t per = swPeriod[ch];
  uint16_t on = (uint16_t)(((uint32_t)pct * per + 50) / 100);
  noInterrupts(); swOn[ch] = on; interrupts();
}

// сменить частоту канала; pct - текущее заполнение, чтобы пересчитать тики
void swSetHz(uint8_t ch, uint16_t hz, uint8_t pct) {
  uint16_t per = hzToPeriod(hz);
  uint16_t on = (uint16_t)(((uint32_t)pct * per + 50) / 100);
  noInterrupts(); swPeriod[ch] = per; swOn[ch] = on; swPh[ch] = 0; interrupts();
}

void initSoftPwm() {
  uint8_t n = 0;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    uint8_t pin = FAN_CHANNELS[ch].pin;
    if (chActive[ch] && (pin == 7 || pin == 8 || pin == 9 || pin == 10)) {
      swMode[ch] = true;
      swPort[ch] = portOutputRegister(digitalPinToPort(pin));
      swMask[ch] = digitalPinToBitMask(pin);
      swPeriod[ch] = hzToPeriod(DC_DEFAULT_HZ);
      swList[n++] = ch;
    }
  }
  swCount = n;   // последним: до этого прерывание ничего не делает
}

bool pinIsSoftPwm(uint8_t pin) {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) if (swMode[ch] && FAN_CHANNELS[ch].pin == pin) return true;
  return false;
}

// вызывается из ISR(TIMER2_COMPA_vect) 4000 раз в секунду
inline void swPwmTick() {
  uint8_t n = swCount;
  for (uint8_t i = 0; i < n; i++) {
    uint8_t ch = swList[i];
    uint16_t ph = swPh[ch] + 1;
    if (ph >= swPeriod[ch]) ph = 0;
    swPh[ch] = ph;
    if (ph < swOn[ch]) *swPort[ch] |= swMask[ch]; else *swPort[ch] &= (uint8_t)~swMask[ch];
  }
}

// Timer2: CTC, 16 МГц / 32 / 125 = 4 кГц; выводы OC2A/OC2B (D10/D9) от таймера отключены
void initSoftPwmTimer() {
  TCCR2A = _BV(WGM21);
  TCCR2B = _BV(CS21) | _BV(CS20);   // предделитель 32
  OCR2A = 124;
  TCNT2 = 0;
  TIMSK2 |= _BV(OCIE2A);
}

// Timer1: Phase-Correct PWM, TOP=ICR1, 16МГц/(2*1*320) = 25кГц
// Только COM1A1 (D11): COM1B1 (D12/PS_ON_PIN) сознательно НЕ включается.
void initTimer1_25kHz() {
  TCCR1A = _BV(COM1A1) | _BV(WGM11);
  TCCR1B = _BV(WGM13) | _BV(CS10);
  ICR1 = 320;
  OCR1A = 0;
}

// Timer3: D5 (OC3A), D2 (OC3B), D3 (OC3C)
void initTimer3_25kHz() {
  uint8_t a = _BV(WGM31);
  if (outPinActive(5)) a |= _BV(COM3A1);
  if (outPinActive(2)) a |= _BV(COM3B1);
  if (outPinActive(3)) a |= _BV(COM3C1);
  TCCR3A = a;
  TCCR3B = _BV(WGM33) | _BV(CS30);
  ICR3 = 320;
  OCR3A = 0;
  OCR3B = 0;
  OCR3C = 0;
}

// Timer4: D6 (OC4A), D7 (OC4B), D8 (OC4C)
void initTimer4_25kHz() {
  uint8_t a = _BV(WGM41);
  if (outPinActive(6)) a |= _BV(COM4A1);
  if (outPinActive(7) && !pinIsSoftPwm(7)) a |= _BV(COM4B1);
  if (outPinActive(8) && !pinIsSoftPwm(8)) a |= _BV(COM4C1);
  TCCR4A = a;
  TCCR4B = _BV(WGM43) | _BV(CS40);
  ICR4 = 320;
  OCR4A = 0;
  OCR4B = 0;
  OCR4C = 0;
}

// Timer5: D46 (OC5A). D44 и D45 больше не используются.
void initTimer5_25kHz() {
  uint8_t a = _BV(WGM51);
  if (outPinActive(46)) a |= _BV(COM5A1);
  TCCR5A = a;
  TCCR5B = _BV(WGM53) | _BV(CS50);
  ICR5 = 320;
  OCR5A = 0;
}

// ============================================================================
//                         ФУНКЦИИ: УПРАВЛЕНИЕ КАНАЛАМИ
// ============================================================================

uint16_t percentToOcr320(uint8_t pct) {
  return (uint16_t)map(constrain(pct, 0, 100), 0, 100, 0, 320);
}

// Единая точка входа по индексу канала (0..TOTAL_CHANNELS-1)
void setChannel(int ch, uint8_t pct) {
  if (ch < 0 || ch >= (int)TOTAL_CHANNELS) return;
  pct = (uint8_t)constrain(pct, 0, 100);
  if (!fsInt && fsState[ch] != FS_ERR) fsState[ch] = FS_IDLE;   // внешний выбор % (HOLD/SET/fail-safe) прерывает автозапуск
  channelPercent[ch] = pct;
  if (!chActive[ch]) return; // пин занят дисплеем - выход не трогаем
  if (swMode[ch]) { swSetDuty(ch, pct); return; }
  if (FAN_CHANNELS[ch].ocr) {
    *(FAN_CHANNELS[ch].ocr) = percentToOcr320(pct);
  } else {
    analogWrite(FAN_CHANNELS[ch].pin, map(pct, 0, 100, 0, 255));
  }
}

uint8_t getChannelPercent(int ch) {
  if (ch < 0 || ch >= (int)TOTAL_CHANNELS) return 0;
  return channelPercent[ch];
}

void setAllChannels(uint8_t pct) {
  for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) setChannel(i, pct);
}

// На watchdog-fail-safe реагируют только каналы, чей источник - Serial.
void failSafeSerialChannels() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (channelSource[ch].type != SRC_SERIAL) continue;
    if (holdActive[ch]) { holdActive[ch] = false; Serial.print(F("EVENT=HOLD,")); Serial.print(FAN_CHANNELS[ch].name); Serial.println(F(",FAILSAFE")); }
    setChannel(ch, 100);
  }
}

// ============================================================================
//                         ФУНКЦИИ: КРИВЫЕ ОХЛАЖДЕНИЯ
// ============================================================================

uint8_t curveLookupGeneric(CurvePoint* table, uint8_t points, float t) {
  if (t <= table[0].tempC) return table[0].percent;
  for (uint8_t i = 1; i < points; i++) {
    if (t <= table[i].tempC) {
      float t0 = table[i - 1].tempC, t1 = table[i].tempC;
      float p0 = table[i - 1].percent, p1 = table[i].percent;
      float frac = (t1 > t0) ? (t - t0) / (t1 - t0) : 0;
      return (uint8_t)(p0 + frac * (p1 - p0));
    }
  }
  return table[points - 1].percent;
}

// Находит индекс канала по имени из Serial-команды
bool channelIndexByName(const char* name, uint8_t& ch) {
  for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) {
    if (strcmp(name, FAN_CHANNELS[i].name) == 0) { ch = i; return true; }
  }
  return false;
}

// Один отсчёт термистора (среднее по 4 чтениям АЦП).
// Возвращает: 0 = валидно (*tOut заполнен), 1 = АЦП на границе (обрыв/КЗ/не подключён),
// 2 = нереально низкая температура.
uint8_t sampleThermistor(uint8_t i, float* tOut) {
  uint8_t pin = PIN_THERM[i];
  analogRead(pin); // холостое чтение: даём мультиплексору успокоиться
  uint16_t sum = 0;
  for (uint8_t k = 0; k < 4; k++) sum += analogRead(pin);
  int adc = sum / 4;
  if (adc <= 2 || adc >= 1021) return 1;
  float r = THERM_SERIES_OHM[i] * (float)adc / (1023.0 - adc);
  float steinhart = r / THERM_NOMINAL_OHM;
  steinhart = log(steinhart);
  steinhart /= THERM_BETA;
  steinhart += 1.0 / (THERM_NOMINAL_C + 273.15);
  steinhart = 1.0 / steinhart;
  float t = steinhart - 273.15;
  if (t < MIN_VALID_AMBIENT_C) return 2;
  *tOut = t;
  return 0;
}

// Обновляет состояние одного термистора (машина состояний OFF/ABSENT/OK/FAULT)
void updateThermistor(uint8_t i) {
  if (!THERM_ENABLED[i]) { thermState[i] = TS_OFF; thermValues[i] = NAN; return; }

  ThermState prevState = thermState[i];
  float t = 0;
  uint8_t kind = sampleThermistor(i, &t);
  if (kind == 0) {
    thermBad[i] = 0;
    if (thermGood[i] < 255) thermGood[i]++;
    if (thermState[i] != TS_OK && thermGood[i] >= THERM_GOOD_SAMPLES) thermState[i] = TS_OK;
    thermValues[i] = (thermState[i] == TS_OK) ? t : NAN;
  } else {
    thermGood[i] = 0;
    if (thermBad[i] < 255) thermBad[i]++;
    thermFaultKind[i] = kind;
    if (thermState[i] == TS_OK && thermBad[i] >= THERM_BAD_SAMPLES) thermState[i] = TS_FAULT;
    if (thermState[i] != TS_OK) thermValues[i] = NAN; // короткий сбой при OK - держим последнее значение
  }
  if (thermState[i] != prevState && i < 5 && (thermEventLastMs[i] == 0 || millis() - thermEventLastMs[i] >= THERM_EVENT_MIN_GAP_MS)) {
    thermEventLastMs[i] = millis() | 1;   // 0 = "ещё не было"
    Serial.print(F("EVENT=THERM,")); Serial.print(THERM_NAMES[i]); Serial.print(',');
    Serial.println(THERM_STATE_NAMES[thermState[i]]);
  }
}

void readAllThermistors() {
  for (uint8_t i = 0; i < NUM_THERMISTORS; i++) updateThermistor(i);
}

// Гистерезис по выходному %. НЕ применяется к ручному SET= и к fail-safe.
void fsBeginBurst(uint8_t ch, uint8_t st);   // ниже
void setChannelFromCurve(uint8_t ch, uint8_t newPct) {
  if (swMode[ch]) {
    if (newPct > 0 && newPct < DC_MIN_RUN_PCT) newPct = DC_MIN_RUN_PCT;
    uint8_t st = fsState[ch];
    if (st == FS_ERR) return;                                   // ошибка канала: выход 0% до SPIN=CLEAR
    if (st != FS_IDLE) {                                        // идёт автозапуск: запоминаем свежую цель
      if (newPct == 0) { fsState[ch] = FS_IDLE; setChannel(ch, 0); return; }
      fsTgt[ch] = newPct; return;
    }
    fsTgt[ch] = newPct;
    if (getChannelPercent(ch) == 0 && newPct > 0 && newPct < DC_KICK_BELOW_PCT) {
      Serial.print(F("EVENT=KICK,")); Serial.println(FAN_CHANNELS[ch].name);
      fsBeginBurst(ch, FS_KICK);
      return;
    }
  }
  int diff = (int)newPct - (int)getChannelPercent(ch);
  if (diff < 0) diff = -diff;
  if (diff < CURVE_HYSTERESIS_PERCENT) return;
  setChannel(ch, newPct);
}

// Сколько процентов сейчас "положено" каналу по его источнику (без гистерезиса)
uint8_t sourcePercentNow(uint8_t ch) {
  if (channelSource[ch].type == SRC_SERIAL) {
    if (watchdogTripped) return 100;
    return curveLookupGeneric(channelCurve[ch], CURVE_POINTS, serialValues[channelSource[ch].index]);
  }
  uint8_t ti = channelSource[ch].index;
  if (ti >= NUM_THERMISTORS) return NO_SENSOR_PERCENT;
  switch (thermState[ti]) {
    case TS_OK:    return curveLookupGeneric(channelCurve[ch], CURVE_POINTS, thermValues[ti]);
    case TS_FAULT: return thermFaultKind[ti] == 2 ? CASE_FAULT_FORCE_PERCENT : 100;
    default:       return NO_SENSOR_PERCENT;
  }
}

// Снять удержание: канал сразу возвращается к значению по кривой. why != NULL -> событие EVENT=HOLD
void releaseHold(uint8_t ch, const __FlashStringHelper* why) {
  if (ch >= TOTAL_CHANNELS || !holdActive[ch]) return;
  holdActive[ch] = false;
  setChannel(ch, sourcePercentNow(ch));
  if (why) { Serial.print(F("EVENT=HOLD,")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(','); Serial.println(why); }
}

void loopHold() {
  unsigned long now = millis();
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (holdActive[ch] && (long)(now - holdUntilMs[ch]) >= 0) releaseHold(ch, F("END"));
  }
}

// Термисторные каналы - работают ВСЕГДА, независимо от режима и Serial-данных.
// ---- Автозапуск: вспомогательные ----
void fsSetOut(uint8_t ch, uint8_t pct) { fsInt = true; setChannel(ch, pct); fsInt = false; }

uint32_t fsTotal(uint8_t ch) { noInterrupts(); uint32_t v = tachTotal[ch]; interrupts(); return v; }

void fsBeginBurst(uint8_t ch, uint8_t st) {    // 100% с подсчётом импульсов
  fsState[ch] = st; fsT0[ch] = millis(); fsBaseSet[ch] = false; fsFirstDone[ch] = false;
  fsSetOut(ch, 100);
}

void fsBeginBoost(uint8_t ch) {
  uint16_t d = (uint16_t)spinStart[ch] + spinBoost; if (d > 100) d = 100;
  fsState[ch] = FS_BOOST; fsT0[ch] = millis();
  fsSetOut(ch, (uint8_t)d);
}

void fsFinish(uint8_t ch) {                    // вернуть канал на нужный %
  fsSetOut(ch, fsTgt[ch]);
  fsState[ch] = FS_IDLE;
  fsNextProbe[ch] = millis() + (unsigned long)spinProbeS * 1000UL;
}

void fsEvent(const __FlashStringHelper* ev, uint8_t ch, int extra) {
  Serial.print(F("EVENT=")); Serial.print(ev); Serial.print(','); Serial.print(FAN_CHANNELS[ch].name);
  if (extra >= 0) { Serial.print(','); Serial.print(extra); }
  Serial.println();
}

void fsFail(uint8_t ch) {                      // вентилятор не крутится
  if (fsTries[ch] < 255) fsTries[ch]++;
  if (fsTries[ch] >= spinTries) {
    fsState[ch] = FS_ERR;
    fsSetOut(ch, 0);
    fsEvent(F("FANFAIL"), ch, -1);
  } else {
    fsEvent(F("STALL"), ch, fsTries[ch]);
    fsBeginBoost(ch);
  }
}

bool fsBusyBurst() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++)
    if (fsState[ch] == FS_KICK || fsState[ch] == FS_PROBE || fsState[ch] == FS_VERIFY) return true;
  return false;
}

void loopSpin() {
  unsigned long now = millis();
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch] || !chActive[ch]) continue;
    uint8_t st = fsState[ch];
    if (st == FS_IDLE) {
      if (!tachActive[ch] || holdActive[ch] || tachTestActive || tachGuardOn[ch]) continue;
      uint8_t p = getChannelPercent(ch);
      bool present = fsSeen[ch] || tachSeen[ch];
      if (p >= 100) {                                // на 100% тахо читается и так
        if (present && tachValid[ch] && tachRpm[ch] == 0 && now - tachLastPulseMs[ch] > 4000UL) {
          fsTgt[ch] = 100;
          fsEvent(F("STALL"), ch, 0);
          fsBeginBoost(ch);
        }
      } else if (p > 0 && spinProbeS > 0 && (present || !fsDisc[ch]) && (long)(now - fsNextProbe[ch]) >= 0 && !fsBusyBurst()) {
        fsTgt[ch] = p;
        fsDisc[ch] = true;                           // канал без замеченного вентилятора проверяется один раз (разведка)
        fsBeginBurst(ch, FS_PROBE);
      }
    } else if (st == FS_KICK || st == FS_PROBE || st == FS_VERIFY) {
      unsigned long el = now - fsT0[ch];
      if (!fsBaseSet[ch] && el >= FS_BASE_DELAY_MS) { fsBase[ch] = fsTotal(ch); fsBaseSet[ch] = true; }
      if (fsBaseSet[ch] && !fsFirstDone[ch] && el >= FS_BASE_DELAY_MS + FS_STOP_CHECK_MS) { fsFirst[ch] = fsTotal(ch) - fsBase[ch]; fsFirstDone[ch] = true; }
      unsigned long dur = (st == FS_KICK) ? DC_KICK_MS : FS_PROBE_MS;
      bool early = (st != FS_KICK) && fsFirstDone[ch] && fsFirst[ch] >= FS_EARLY_PULSES;   // крутился с самого начала
      if (!early && (el < dur || !fsBaseSet[ch])) continue;
      uint32_t delta = fsTotal(ch) - fsBase[ch];
      bool alive = early || delta >= FS_MIN_PULSES;
      bool wasStopped = fsFirstDone[ch] && fsFirst[ch] == 0;
      bool present = fsSeen[ch] || tachSeen[ch];
      if (alive) fsSeen[ch] = true;
      if (st == FS_KICK) {
        if (alive || !present) { fsTries[ch] = 0; fsFinish(ch); } else fsFail(ch);
      } else if (st == FS_PROBE) {
        if (!alive) { if (present) fsFail(ch); else fsFinish(ch); }
        else if (wasStopped) { fsEvent(F("STALL"), ch, 0); fsBeginBoost(ch); }
        else fsFinish(ch);
      } else {                                       // FS_VERIFY
        if (alive && !wasStopped) { fsTries[ch] = 0; fsEvent(F("FANOK"), ch, -1); fsFinish(ch); }
        else fsFail(ch);
      }
    } else if (st == FS_BOOST) {
      if (now - fsT0[ch] >= FS_BOOST_MS) {
        uint16_t bd = (uint16_t)spinStart[ch] + spinBoost; if (bd > 100) bd = 100;
        if (fsTgt[ch] >= bd) fsBeginBurst(ch, FS_VERIFY);
        else { fsState[ch] = FS_RAMP; fsCur[ch] = (uint8_t)bd; fsT0[ch] = now; }
      }
    } else if (st == FS_RAMP) {
      if (now - fsT0[ch] >= spinRampMs) {
        fsT0[ch] = now;
        if (fsCur[ch] > fsTgt[ch] + spinStep) fsCur[ch] -= spinStep; else fsCur[ch] = fsTgt[ch];
        fsSetOut(ch, fsCur[ch]);
        if (fsCur[ch] == fsTgt[ch]) fsBeginBurst(ch, FS_VERIFY);
      }
    }
  }
}

void applyThermCurves() {
  if (millis() - lastLocalCurveMillis < LOCAL_CURVE_PERIOD_MS) return;
  lastLocalCurveMillis = millis();

  readAllThermistors();

  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (tachTestActive && TACH_NEEDS_FULL[ch]) continue;   // идёт TACHTEST
    if (holdActive[ch]) continue;                          // ручное удержание (HOLD=)
    if (channelSource[ch].type != SRC_THERM) continue;
    uint8_t ti = channelSource[ch].index;
    if (ti >= NUM_THERMISTORS) continue;
    switch (thermState[ti]) {
      case TS_OK:
        setChannelFromCurve(ch, curveLookupGeneric(channelCurve[ch], CURVE_POINTS, thermValues[ti]));
        break;
      case TS_FAULT: // был в порядке и пропал - громко и безопасно, без гистерезиса
        setChannel(ch, thermFaultKind[ti] == 2 ? CASE_FAULT_FORCE_PERCENT : 100);
        break;
      default:       // ABSENT/OFF - датчика нет: спокойный постоянный %, без ошибки
        setChannel(ch, NO_SENSOR_PERCENT);
        break;
    }
  }
}

// Serial-каналы - пересчитываются сразу при приходе новых TEMP= данных
void applySerialCurves() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (tachTestActive && TACH_NEEDS_FULL[ch]) continue;   // идёт TACHTEST
    if (holdActive[ch]) continue;                          // ручное удержание (HOLD=)
    if (channelSource[ch].type != SRC_SERIAL) continue;
    float t = serialValues[channelSource[ch].index];
    setChannelFromCurve(ch, curveLookupGeneric(channelCurve[ch], CURVE_POINTS, t));
  }
}

// ============================================================================
//                              ФУНКЦИИ: ТАХОМЕТРЫ
// ============================================================================

// Один спад на tach-входе канала ch. Импульсы чаще TACH_MIN_PERIOD_US - помеха.
inline void tachRise(uint8_t ch) {
  tachRiseUs[ch] = micros();
}

// Выключить PCINT-бит канала прямо из обработчика (шторм помех)
inline void tachMaskOff(uint8_t ch) {
  uint8_t b = (uint8_t)~_BV(tachPcmskBit[ch]);
  uint8_t g = tachGrp[ch];
  if (g == 0) PCMSK0 &= b; else if (g == 1) PCMSK1 &= b; else if (g == 2) PCMSK2 &= b;
}

inline void tachEdge(uint8_t ch) {
  uint32_t now = micros();
  if (now - tachRiseUs[ch] >= TACH_MIN_HIGH_US && now - tachLastUs[ch] >= TACH_MIN_PERIOD_US) {
    tachLastUs[ch] = now;
    tachCnt[ch]++;
    tachTotal[ch]++;
    uint8_t k = tachDbgIdx[ch];
    tachDbgUs[ch][k] = now;
    tachDbgIdx[ch] = (uint8_t)((k + 1) & 7);
  } else {
    if (tachRejected[ch] < 65535) tachRejected[ch]++;
    if (now - tachBurstStartUs[ch] > 100000UL) { tachBurstStartUs[ch] = now; tachBurstN[ch] = 0; }
    if (++tachBurstN[ch] > 400 && tachGrp[ch] <= 2 && !tachTrip[ch]) { tachTrip[ch] = true; tachMaskOff(ch); }
  }
}

void isrTach18() { int8_t c = tachChanD18; if (c >= 0) { if (digitalRead(18)) tachRise((uint8_t)c); else tachEdge((uint8_t)c); } }
void isrTach19() { int8_t c = tachChanD19; if (c >= 0) { if (digitalRead(19)) tachRise((uint8_t)c); else tachEdge((uint8_t)c); } }
void isrTach20() { int8_t c = tachChanD20; if (c >= 0) { if (digitalRead(20)) tachRise((uint8_t)c); else tachEdge((uint8_t)c); } }
void isrTach21() { int8_t c = tachChanD21; if (c >= 0) { if (digitalRead(21)) tachRise((uint8_t)c); else tachEdge((uint8_t)c); } }

void tachScanGroup(uint8_t g, uint8_t cur) {
  uint8_t fall = tgLast[g] & (uint8_t)~cur; // спады с прошлого прерывания
  uint8_t rise = (uint8_t)~tgLast[g] & cur; // подъёмы
  tgLast[g] = cur;
  if (!fall && !rise) return;
  for (uint8_t k = 0; k < tgN[g]; k++) {
    uint8_t m = tgPinMask[g][k];
    if (rise & m) tachRise(tgChan[g][k]);
    if (fall & m) tachEdge(tgChan[g][k]);
  }
}

ISR(PCINT0_vect) { tachScanGroup(0, PINB); }
ISR(PCINT1_vect) { tachScanGroup(1, PINJ); }
ISR(PCINT2_vect) { tachScanGroup(2, PINK); }

void tachAddToGroup(uint8_t g, uint8_t ch, uint8_t pinMask, uint8_t pcmskBit) {
  if (tgN[g] >= 8) return;
  tgPinMask[g][tgN[g]] = pinMask;
  tgChan[g][tgN[g]] = ch;
  tgN[g]++;
  tgPcmsk[g] |= _BV(pcmskBit);
  tachGrp[ch] = g;
  tachPcmskBit[ch] = pcmskBit;
}

void tachSkip(uint8_t ch, uint8_t pin, const __FlashStringHelper* why) {
  Serial.print(F("TACHCFG=SKIP,")); Serial.print(ch); Serial.print(',');
  Serial.print(pin); Serial.print(','); Serial.println(why);
}

void tachSetup() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    tachActive[ch] = false;
    tachGrp[ch] = 255;
    uint8_t p = TACH_PIN[ch];
    if (p == TACH_NO_PIN) continue;
    if (!chActive[ch])                { tachSkip(ch, p, F("channel off")); continue; }
    if (pinReservedByDisplay(p))      { tachSkip(ch, p, F("display")); continue; }
    if (pinIsThermistor(p))           { tachSkip(ch, p, F("thermistor")); continue; }
    if (pinIsFanOutput(p))            { tachSkip(ch, p, F("fan output")); continue; }
    if (p == PIN_BUTTON)              { tachSkip(ch, p, F("button")); continue; }
    bool dup = false;
    for (uint8_t j = 0; j < ch; j++) if (tachActive[j] && TACH_PIN[j] == p) dup = true;
    if (dup)                          { tachSkip(ch, p, F("duplicate")); continue; }

    pinMode(p, INPUT_PULLUP);
    bool ok = true;
    if (p == 18) {
      tachChanD18 = ch;
      attachInterrupt(digitalPinToInterrupt(18), isrTach18, CHANGE);
    } else if (p == 19) {
      tachChanD19 = ch;
      attachInterrupt(digitalPinToInterrupt(19), isrTach19, CHANGE);
    } else if (p == 20) {
      tachChanD20 = ch;
      attachInterrupt(digitalPinToInterrupt(20), isrTach20, CHANGE);
    } else if (p == 21) {
      tachChanD21 = ch;
      attachInterrupt(digitalPinToInterrupt(21), isrTach21, CHANGE);
    } else if (p >= 50 && p <= 53) {          // PB3..PB0
      uint8_t bit = 53 - p;                   // 53->0, 52->1, 51->2, 50->3
      tachAddToGroup(0, ch, _BV(bit), bit);
    } else if (p == 15) {                     // PJ0 = PCINT9
      tachAddToGroup(1, ch, _BV(0), 1);
    } else if (p == 14) {                     // PJ1 = PCINT10
      tachAddToGroup(1, ch, _BV(1), 2);
    } else if (p >= A8 && p <= A15) {         // PK0..PK7 = PCINT16..23
      uint8_t bit = p - A8;
      tachAddToGroup(2, ch, _BV(bit), bit);
    } else {
      ok = false;
      tachSkip(ch, p, F("unsupported pin"));
    }
    tachActive[ch] = ok;
  }

  // Включаем PCINT-группы, где есть tach-входы
  tgLast[0] = PINB; tgLast[1] = PINJ; tgLast[2] = PINK;
  if (tgN[0]) { PCMSK0 = tgPcmsk[0]; PCIFR |= _BV(PCIF0); PCICR |= _BV(PCIE0); }
  if (tgN[1]) { PCMSK1 = tgPcmsk[1]; PCIFR |= _BV(PCIF1); PCICR |= _BV(PCIE1); }
  if (tgN[2]) { PCMSK2 = tgPcmsk[2]; PCIFR |= _BV(PCIF2); PCICR |= _BV(PCIE2); }

  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) { tachFullSoFar[ch] = true; tachValid[ch] = !TACH_NEEDS_FULL[ch]; }
  tachWindowStart = millis();
}

// Обработка срабатываний защиты: событие, пауза 3 с, затем вход возвращается (3-pin каналы - через гейт ниже)
void loopTachGuard() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (tachGrp[ch] > 2) continue;
    if (tachTrip[ch]) {
      noInterrupts(); tachTrip[ch] = false; interrupts();
      tachGuardOn[ch] = true;
      tachGuardUntilMs[ch] = millis() + 3000UL;
      if (tachGuardEventMs[ch] == 0 || millis() - tachGuardEventMs[ch] > 10000UL) {
        tachGuardEventMs[ch] = millis() | 1UL;
        Serial.print(F("EVENT=TACHGUARD,")); Serial.println(FAN_CHANNELS[ch].name);
      }
    }
    if (tachGuardOn[ch] && (long)(millis() - tachGuardUntilMs[ch]) >= 0) {
      tachGuardOn[ch] = false;
      tachBurstN[ch] = 0;
      if (!TACH_NEEDS_FULL[ch]) {          // 3-pin каналы вернёт гейт, когда будет 100%
        uint8_t g = tachGrp[ch];
        volatile uint8_t* reg = (g == 0) ? &PCMSK0 : (g == 1) ? &PCMSK1 : &PCMSK2;
        noInterrupts();
        tgLast[g] = (g == 0) ? PINB : (g == 1) ? PINJ : PINK;
        PCIFR |= _BV(PCIF0 + g);
        *reg |= _BV(tachPcmskBit[ch]);
        interrupts();
      }
    }
  }
}

// Гейт прерываний для 3-pin каналов: PCINT включён только при 100% PWM (иначе tach рубится ШИМом)
void tachGateUpdate() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!TACH_NEEDS_FULL[ch] || !tachActive[ch] || tachGrp[ch] > 2) continue;
    uint8_t g = tachGrp[ch];
    uint8_t bit = _BV(tachPcmskBit[ch]);
    volatile uint8_t* reg = (g == 0) ? &PCMSK0 : (g == 1) ? &PCMSK1 : &PCMSK2;
    bool want = getChannelPercent(ch) >= 100 && !tachGuardOn[ch] && !tachTrip[ch];
    bool isOn = (*reg & bit) != 0;
    if (want == isOn) continue;
    noInterrupts();
    if (want) {
      tgLast[g] = (g == 0) ? PINB : (g == 1) ? PINJ : PINK;   // чтобы не поймать ложный спад
      PCIFR |= _BV(PCIF0 + g);
      *reg |= bit;
    } else {
      *reg &= (uint8_t)~bit;
    }
    interrupts();
  }
}

// Раз в TACH_WINDOW_MS переводит накопленные импульсы в RPM
void updateTach() {
  // Для 3-pin каналов следим, что в окне канал не снижался ниже 100%
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (TACH_NEEDS_FULL[ch] && (getChannelPercent(ch) < 100 || tachGuardOn[ch])) tachFullSoFar[ch] = false;
  }
  unsigned long now = millis();
  unsigned long dt = now - tachWindowStart;
  if (dt < TACH_WINDOW_MS) return;
  tachWindowStart = now;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!tachActive[ch]) continue;
    noInterrupts();
    uint32_t n = tachCnt[ch];
    tachCnt[ch] = 0;
    interrupts();
    if (TACH_NEEDS_FULL[ch]) {
      bool full = tachFullSoFar[ch];
      tachFullSoFar[ch] = true;           // следующее окно считаем с чистого листа
      tachValid[ch] = full;
      if (!full) { tachRpm[ch] = 0; continue; }   // при ШИМ tach рубится ШИМом - не доверяем
    }
    if (n > 60000UL) n = 60000UL; // защита от переполнения при сильной помехе
    uint32_t rpm = (n * 60000UL) / ((uint32_t)TACH_PPR * dt);
    if (rpm > TACH_MAX_RPM) { tachRpm[ch] = 0; continue; }   // помеха
    tachRpm[ch] = (uint16_t)rpm;
    if (n > 0) { tachSeen[ch] = true; tachLastPulseMs[ch] = now; }
  }
}

TachState tachStateOf(uint8_t ch) {
  if (ch >= TOTAL_CHANNELS || !tachActive[ch]) return TACH_NONE;
  if (TACH_NEEDS_FULL[ch] && !tachValid[ch]) return TACH_PWM;
  if (!tachSeen[ch]) return TACH_NOSIG;
  if (tachRpm[ch] > 0) return TACH_OK;
  if (getChannelPercent(ch) >= TACH_STALL_MIN_PERCENT && millis() - tachLastPulseMs[ch] > TACH_STALL_AFTER_MS) return TACH_STALL;
  return TACH_OK; // 0 об/мин при низком PWM - нормально (вентилятор может стоять)
}

// ============================================================================
//                              ФУНКЦИИ: EEPROM
// ============================================================================

void saveControllerMode(ControllerMode m) {
  EEPROM.update(EEPROM_ADDR_MAGIC, EEPROM_MAGIC_VAL);
  EEPROM.update(EEPROM_ADDR_MODE, (uint8_t)m);
}

ControllerMode loadControllerMode() {
  if (EEPROM.read(EEPROM_ADDR_MAGIC) != EEPROM_MAGIC_VAL) return TEMP_SERIAL;
  uint8_t raw = EEPROM.read(EEPROM_ADDR_MODE);
  if (raw >= CTRL_MODE_COUNT) return TEMP_SERIAL;
  return (ControllerMode)raw;
}

void saveCurvesToEeprom() {
  int addr = EEPROM_ADDR_CURVES_START;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++)
    for (uint8_t i = 0; i < CURVE_POINTS; i++) { EEPROM.put(addr, channelCurve[ch][i]); addr += sizeof(CurvePoint); }
  EEPROM.update(EEPROM_ADDR_CURVE_MAGIC, EEPROM_CURVE_MAGIC_VAL);
}

void loadCurvesFromEeprom() {
  if (EEPROM.read(EEPROM_ADDR_CURVE_MAGIC) != EEPROM_CURVE_MAGIC_VAL) return;
  int addr = EEPROM_ADDR_CURVES_START;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++)
    for (uint8_t i = 0; i < CURVE_POINTS; i++) { EEPROM.get(addr, channelCurve[ch][i]); addr += sizeof(CurvePoint); }
}

void resetCurvesToDefaults() {
  memcpy(channelCurve, CURVE_DEFAULT, sizeof(CURVE_DEFAULT));
  EEPROM.update(EEPROM_ADDR_CURVE_MAGIC, 0xFF);
}

void saveBindsToEeprom() {
  int addr = EEPROM_ADDR_BINDS_START;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) { EEPROM.put(addr, channelSource[ch]); addr += sizeof(ChannelSource); }
  EEPROM.update(EEPROM_ADDR_BIND_MAGIC, EEPROM_BIND_MAGIC_VAL);
}

void loadBindsFromEeprom() {
  if (EEPROM.read(EEPROM_ADDR_BIND_MAGIC) != EEPROM_BIND_MAGIC_VAL) return;
  int addr = EEPROM_ADDR_BINDS_START;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) { EEPROM.get(addr, channelSource[ch]); addr += sizeof(ChannelSource); }
}

void resetBindsToDefaults() {
  memcpy(channelSource, CHANNEL_SOURCE_DEFAULT, sizeof(CHANNEL_SOURCE_DEFAULT));
  EEPROM.update(EEPROM_ADDR_BIND_MAGIC, 0xFF);
}

// Частоты ШИМ DC-каналов (программный ШИМ)
void saveDcFreqToEeprom() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch]) continue;
    uint16_t hz = periodToHz(swPeriod[ch]);
    EEPROM.put(EEPROM_ADDR_DCF_START + ch * 2, hz);
  }
  EEPROM.update(EEPROM_ADDR_DCF_MAGIC, EEPROM_DCF_MAGIC_VAL);
}

void loadDcFreqFromEeprom() {
  if (EEPROM.read(EEPROM_ADDR_DCF_MAGIC) != EEPROM_DCF_MAGIC_VAL) return;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch]) continue;
    uint16_t hz = 0;
    EEPROM.get(EEPROM_ADDR_DCF_START + ch * 2, hz);
    if (hz >= DC_HZ_MIN && hz <= DC_HZ_MAX) swPeriod[ch] = hzToPeriod(hz);   // повреждённое значение - остаётся по умолчанию
  }
}

// Автозапуск: точки раскрутки и параметры
void initSpinDefaults() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) spinStart[ch] = 37;
  spinBoost = 5; spinStep = 5; spinTries = 3; spinRampMs = 1000; spinProbeS = 60;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) fsNextProbe[ch] = millis() + 20000UL;   // первые 20 с после включения не проверяем (вентиляторы разгоняются)
}

void saveSpinToEeprom() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) EEPROM.update(EEPROM_ADDR_SPIN_START + ch, spinStart[ch]);
  EEPROM.update(EEPROM_ADDR_SPIN_PARAM, spinBoost);
  EEPROM.update(EEPROM_ADDR_SPIN_PARAM + 1, spinStep);
  EEPROM.update(EEPROM_ADDR_SPIN_PARAM + 2, spinTries);
  EEPROM.put(EEPROM_ADDR_SPIN_PARAM + 3, spinRampMs);
  EEPROM.put(EEPROM_ADDR_SPIN_PARAM + 5, spinProbeS);
  EEPROM.update(EEPROM_ADDR_SPIN_MAGIC, EEPROM_SPIN_MAGIC_VAL);
}

void loadSpinFromEeprom() {
  if (EEPROM.read(EEPROM_ADDR_SPIN_MAGIC) != EEPROM_SPIN_MAGIC_VAL) return;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    uint8_t v = EEPROM.read(EEPROM_ADDR_SPIN_START + ch);
    if (v >= 1 && v <= 100) spinStart[ch] = v;
  }
  uint8_t b = EEPROM.read(EEPROM_ADDR_SPIN_PARAM), st = EEPROM.read(EEPROM_ADDR_SPIN_PARAM + 1), tr = EEPROM.read(EEPROM_ADDR_SPIN_PARAM + 2);
  uint16_t rm = 0, ps = 0;
  EEPROM.get(EEPROM_ADDR_SPIN_PARAM + 3, rm); EEPROM.get(EEPROM_ADDR_SPIN_PARAM + 5, ps);
  if (b <= 50 && st >= 1 && st <= 50 && tr >= 1 && tr <= 9 && rm >= 200 && rm <= 10000 && ps <= 3600) {
    spinBoost = b; spinStep = st; spinTries = tr; spinRampMs = rm; spinProbeS = ps;
  }
}

void resetSpinToDefaults() {
  initSpinDefaults();
  EEPROM.update(EEPROM_ADDR_SPIN_MAGIC, 0xFF);
}

void resetDcFreqToDefaults() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) if (swMode[ch]) swSetHz(ch, DC_DEFAULT_HZ, getChannelPercent(ch));
  EEPROM.update(EEPROM_ADDR_DCF_MAGIC, 0xFF);
}

// ============================================================================
//                              ФУНКЦИИ: РЕЖИМЫ / ПРИВЯЗКИ
// ============================================================================

void feedWatchdog() {
  lastDataMillis = millis();
  if (watchdogTripped) Serial.println(F("EVENT=FAILSAFE,OFF"));
  watchdogTripped = false;
}

// В TEMP_SERIAL привязка GPU/вдува/выдува зафиксирована
bool isBindingLocked(uint8_t ch) {
  if (controllerMode != TEMP_SERIAL) return false;
  return (ch == CH_GPU || ch == CH_INTAKE || ch == CH_EXHAUST);
}

void setControllerMode(ControllerMode m) {
  controllerMode = m;
  if (m == TEMP_SERIAL) {
    channelSource[CH_GPU]     = CHANNEL_SOURCE_DEFAULT[CH_GPU];
    channelSource[CH_INTAKE]  = CHANNEL_SOURCE_DEFAULT[CH_INTAKE];
    channelSource[CH_EXHAUST] = CHANNEL_SOURCE_DEFAULT[CH_EXHAUST];
  }
  saveControllerMode(m);
  Serial.print(F("EVENT=MODE,")); Serial.println((int)m);
  feedWatchdog();
}

void checkWatchdog() {
  if (millis() - lastDataMillis > DATA_TIMEOUT_MS) {
    if (!watchdogTripped) {
      watchdogTripped = true; // индикация "** FAILSAFE **" на дисплее - всегда
      Serial.println(F("EVENT=FAILSAFE,ON"));
      if (FAILSAFE_ENABLED) failSafeSerialChannels();
    }
  }
}

// ============================================================================
//                              ФУНКЦИИ: SERIAL
// ============================================================================

void dumpCurves() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    for (uint8_t i = 0; i < CURVE_POINTS; i++) {
      Serial.print(F("CURVE=")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
      Serial.print(i); Serial.print(',');
      Serial.print(channelCurve[ch][i].tempC); Serial.print(',');
      Serial.println(channelCurve[ch][i].percent);
    }
  }
  Serial.println(F("CURVE=END"));
}

void printSource(uint8_t ch) {
  if (channelSource[ch].type == SRC_THERM) {
    Serial.print(F("THERM,")); Serial.print(THERM_NAMES[channelSource[ch].index]);
  } else {
    Serial.print(F("SERIAL,")); Serial.print(SERIAL_KEY_NAMES[channelSource[ch].index]);
  }
}

void dumpBinds() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    Serial.print(F("BIND=")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    printSource(ch);
    Serial.println(isBindingLocked(ch) ? F(",LOCKED") : F(",FREE"));
  }
  Serial.println(F("BIND=END"));
}

void printThermLine(uint8_t i) {
  Serial.print(THERM_NAMES[i]); Serial.print(',');
  Serial.print(THERM_ENABLED[i] ? '1' : '0'); Serial.print(',');
  if (isnan(thermValues[i])) Serial.print(F("NaN"));
  else Serial.print(thermValues[i], 1);
  Serial.print(','); Serial.println(THERM_STATE_NAMES[thermState[i]]);
}

void dumpTherms() {
  for (uint8_t i = 0; i < NUM_THERMISTORS; i++) { Serial.print(F("THERM=")); printThermLine(i); }
  Serial.println(F("THERM=END"));
}

void printTachLine(uint8_t ch) {
  Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
  if (TACH_PIN[ch] == TACH_NO_PIN) Serial.print('-'); else Serial.print(TACH_PIN[ch]);
  Serial.print(',');
  Serial.print(TACH_STATE_NAMES[tachStateOf(ch)]); Serial.print(',');
  Serial.println(tachActive[ch] ? tachRpm[ch] : 0);
}

void dumpTach() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) { Serial.print(F("TACH=")); printTachLine(ch); }
  Serial.println(F("TACH=END"));
}

// TACHRAW=n: что реально приходит на tach-пин канала n
void dumpTachRaw(int ch) {
  if (ch < 0 || ch >= (int)TOTAL_CHANNELS) return;
  uint32_t t[8];
  uint8_t idx;
  uint16_t rej;
  noInterrupts();
  for (uint8_t i = 0; i < 8; i++) t[i] = tachDbgUs[ch][i];
  idx = tachDbgIdx[ch];
  rej = tachRejected[ch];
  tachRejected[ch] = 0;
  interrupts();
  Serial.print(F("TACHRAW=")); Serial.print(ch); Serial.print(',');
  Serial.print(rej);
  uint32_t prev = t[idx];                 // самый старый
  for (uint8_t k = 1; k < 8; k++) {
    uint32_t cur = t[(idx + k) & 7];
    Serial.print(','); Serial.print((unsigned long)(cur - prev));
    prev = cur;
  }
  Serial.println();
}

// TACHTEST: 3-pin каналы -> 100%, через TACHTEST_SPINUP_MS замер и возврат прежних процентов
void startTachTest() {
  if (tachTestActive) return;
  tachTestActive = true;
  tachTestStart = millis();
  Serial.println(F("TACHTEST=START"));
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!TACH_NEEDS_FULL[ch]) continue;
    tachTestSaved[ch] = getChannelPercent(ch);
    setChannel(ch, 100);
  }
}

void loopTachTest() {
  if (!tachTestActive) return;
  if (millis() - tachTestStart < TACHTEST_SPINUP_MS) return;
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!TACH_NEEDS_FULL[ch]) continue;
    Serial.print(F("TACHTEST=")); printTachLine(ch);
    uint8_t back = tachTestSaved[ch];
    if (watchdogTripped && channelSource[ch].type == SRC_SERIAL) back = 100;   // fail-safe важнее
    setChannel(ch, back);
  }
  tachTestActive = false;
  Serial.println(F("TACHTEST=END"));
}

// ---------------------------------------------------------------------------
// TACHDUMP / TACHFAST: сырая запись tach-пина в бины (для разбора "мусора" на 3-pin каналах)
// ---------------------------------------------------------------------------
static void printHex2(uint8_t v) {
  static const char H[] = "0123456789ABCDEF";
  Serial.print(H[v >> 4]); Serial.print(H[v & 15]);
}

void runTachDump(int ch, uint16_t binUs, uint16_t nBins) {
  if (ch < 0 || ch >= (int)TOTAL_CHANNELS) { Serial.println(F("TACHDUMP=ERR,channel")); return; }
  if (!tachActive[ch]) { Serial.println(F("TACHDUMP=ERR,no_tach")); return; }
  if (binUs < 20) binUs = 20;
  if (binUs > 2000) binUs = 2000;                 // n в бине должно помещаться в uint16
  if (nBins < 1) nBins = 1;
  if (nBins > TACHDUMP_MAX_BINS) nBins = TACHDUMP_MAX_BINS;

  uint8_t pin = TACH_PIN[ch];
  volatile uint8_t* reg = portInputRegister(digitalPinToPort(pin));
  const uint8_t mask = digitalPinToBitMask(pin);

  // На время записи отключаем прерывание этого входа (иначе помеха грузит процессор)
  volatile uint8_t* pcReg = 0; uint8_t pcBit = 0; bool pcWas = false;
  int8_t extN = -1; bool extWas = false;
  if (tachGrp[ch] <= 2) {
    pcReg = (tachGrp[ch] == 0) ? &PCMSK0 : (tachGrp[ch] == 1) ? &PCMSK1 : &PCMSK2;
    pcBit = _BV(tachPcmskBit[ch]);
    pcWas = (*pcReg & pcBit) != 0;
    noInterrupts(); *pcReg &= (uint8_t)~pcBit; interrupts();
  } else if (pin >= 18 && pin <= 21) {
    extN = (int8_t)digitalPinToInterrupt(pin);
    if (extN >= 0) {
      extWas = (EIMSK & _BV(extN)) != 0;
      noInterrupts(); EIMSK &= (uint8_t)~_BV(extN); interrupts();
    }
  }

  uint32_t t0 = micros();
  uint8_t last = (*reg & mask) ? 1 : 0;
  for (uint16_t i = 0; i < nBins; i++) {
    uint32_t binEnd = t0 + (uint32_t)(i + 1) * binUs;
    uint8_t tr = 0; uint16_t hi = 0, n = 0;
    do {
      for (uint8_t k = 0; k < 8; k++) {            // 8 чтений подряд на один вызов micros()
        uint8_t l = (*reg & mask) ? 1 : 0;
        if (l != last) { if (tr < 255) tr++; last = l; }
        hi += l; n++;
      }
    } while ((int32_t)(micros() - binEnd) < 0);
    dumpTrans[i] = tr;
    dumpHigh[i] = (uint8_t)(((uint32_t)hi * 255UL) / n);
  }

  // Возвращаем прерывания как были
  if (pcReg) {
    noInterrupts();
    tgLast[tachGrp[ch]] = (tachGrp[ch] == 0) ? PINB : (tachGrp[ch] == 1) ? PINJ : PINK;
    PCIFR |= _BV(PCIF0 + tachGrp[ch]);
    if (pcWas) *pcReg |= pcBit;
    interrupts();
  } else if (extN >= 0) {
    noInterrupts();
    EIFR |= _BV(extN);
    if (extWas) EIMSK |= _BV(extN);
    interrupts();
  }

  Serial.print(F("TACHDUMP=BEGIN,")); Serial.print(ch); Serial.print(',');
  Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
  Serial.print(pin); Serial.print(',');
  Serial.print(getChannelPercent(ch)); Serial.print(',');
  Serial.print(binUs); Serial.print(','); Serial.println(nBins);
  const uint16_t PER_LINE = 40;
  for (uint16_t i = 0; i < nBins; i += PER_LINE) {
    Serial.print(F("TACHDUMP=D,")); Serial.print(i); Serial.print(',');
    for (uint16_t k = i; k < i + PER_LINE && k < nBins; k++) { printHex2(dumpTrans[k]); printHex2(dumpHigh[k]); }
    Serial.println();
  }
  Serial.print(F("TACHDUMP=END,")); Serial.println(ch);
}

// "<канал>" - номер или имя
static bool parseChannelToken(const char* tok, uint8_t& ch) {
  if (!tok || !*tok) return false;
  if (tok[0] >= '0' && tok[0] <= '9') {
    int v = atoi(tok);
    if (v < 0 || v >= (int)TOTAL_CHANNELS) return false;
    ch = (uint8_t)v; return true;
  }
  return channelIndexByName(tok, ch);
}

// DCFREQ? / DCFREQ=<канал|ALL>,<Гц> / DCFREQ=SAVE / DCFREQ=RESET
void dumpDcFreq() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch]) continue;
    Serial.print(F("DCFREQ=")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    Serial.print(periodToHz(swPeriod[ch])); Serial.print(','); Serial.println(swPeriod[ch]);
  }
  Serial.println(F("DCFREQ=END"));
}

void handleDcFreqCommand(char* args) {
  if (strcmp(args, "SAVE") == 0) { saveDcFreqToEeprom(); Serial.println(F("DCFREQ=SAVED")); return; }
  if (strcmp(args, "RESET") == 0) { resetDcFreqToDefaults(); Serial.println(F("DCFREQ=RESET_OK")); return; }
  char* a = strtok(args, ",");
  char* b = strtok(NULL, ",");
  if (!a || !b) { Serial.println(F("DCFREQ=ERR,args")); return; }
  int hz = atoi(b);
  if (hz < (int)DC_HZ_MIN || hz > (int)DC_HZ_MAX) { Serial.println(F("DCFREQ=ERR,range")); return; }
  bool all = (strcmp(a, "ALL") == 0);
  uint8_t one = 0;
  if (!all && (!parseChannelToken(a, one) || !swMode[one])) { Serial.println(F("DCFREQ=ERR,channel")); return; }
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch] || (!all && ch != one)) continue;
    swSetHz(ch, (uint16_t)hz, getChannelPercent(ch));
    Serial.print(F("DCFREQ=OK,")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    Serial.print(periodToHz(swPeriod[ch])); Serial.print(','); Serial.println(swPeriod[ch]);
  }
}

// SPIN? / SPIN=<канал|ALL>,<%> / SPIN=PARAM,... / SPIN=SAVE|RESET|CLEAR[,<канал|ALL>] / SPIN=TEST,<канал>
void dumpSpin() {
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch]) continue;
    Serial.print(F("SPIN=")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    Serial.print(spinStart[ch]); Serial.print(',');
    Serial.print(FS_NAMES[fsState[ch]]); Serial.print(',');
    Serial.println(fsTries[ch]);
  }
  Serial.print(F("SPIN=PARAM,")); Serial.print(spinBoost); Serial.print(',');
  Serial.print(spinStep); Serial.print(','); Serial.print(spinRampMs); Serial.print(',');
  Serial.print(spinProbeS); Serial.print(','); Serial.println(spinTries);
  Serial.println(F("SPIN=END"));
}

void handleSpinCommand(char* args) {
  if (strcmp(args, "SAVE") == 0) { saveSpinToEeprom(); Serial.println(F("SPIN=SAVED")); return; }
  if (strcmp(args, "RESET") == 0) { resetSpinToDefaults(); Serial.println(F("SPIN=RESET_OK")); return; }
  char* a = strtok(args, ",");
  char* b = strtok(NULL, ",");
  if (!a) { Serial.println(F("SPIN=ERR,args")); return; }
  if (strcmp(a, "PARAM") == 0) {
    char* c = strtok(NULL, ","); char* d = strtok(NULL, ","); char* e = strtok(NULL, ","); char* f = strtok(NULL, ",");
    if (!b || !c || !d || !e || !f) { Serial.println(F("SPIN=ERR,args")); return; }
    int boost = atoi(b), step = atoi(c), rm = atoi(d), ps = atoi(e), tr = atoi(f);
    if (boost < 0 || boost > 50 || step < 1 || step > 50 || rm < 200 || rm > 10000 || ps < 0 || ps > 3600 || tr < 1 || tr > 9) { Serial.println(F("SPIN=ERR,range")); return; }
    spinBoost = boost; spinStep = step; spinRampMs = rm; spinProbeS = ps; spinTries = tr;
    Serial.println(F("SPIN=OK,PARAM"));
    return;
  }
  if (strcmp(a, "CLEAR") == 0 || strcmp(a, "TEST") == 0) {
    bool test = (a[0] == 'T');
    bool all = (b && strcmp(b, "ALL") == 0) || (!b && !test);
    uint8_t one = 0;
    if (!all && (!b || !parseChannelToken(b, one) || !swMode[one])) { Serial.println(F("SPIN=ERR,channel")); return; }
    for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
      if (!swMode[ch] || (!all && ch != one)) continue;
      if (!test) {
        fsState[ch] = FS_IDLE; fsTries[ch] = 0; fsNextProbe[ch] = millis() + 5000UL;
        Serial.print(F("SPIN=OK,CLEAR,")); Serial.println(FAN_CHANNELS[ch].name);
      } else {
        uint8_t p = getChannelPercent(ch);
        if (fsState[ch] != FS_IDLE || p == 0 || p >= 100 || !tachActive[ch]) { Serial.println(F("SPIN=ERR,state")); return; }
        fsSeen[ch] = true;                       // проверка по просьбе: считаем, что вентилятор есть
        fsTgt[ch] = p;
        fsBeginBurst(ch, FS_PROBE);
        Serial.print(F("SPIN=OK,TEST,")); Serial.println(FAN_CHANNELS[ch].name);
      }
    }
    return;
  }
  if (!b) { Serial.println(F("SPIN=ERR,args")); return; }
  int v = atoi(b);
  if (v < 1 || v > 100) { Serial.println(F("SPIN=ERR,range")); return; }
  bool all = (strcmp(a, "ALL") == 0);
  uint8_t one = 0;
  if (!all && (!parseChannelToken(a, one) || !swMode[one])) { Serial.println(F("SPIN=ERR,channel")); return; }
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch] || (!all && ch != one)) continue;
    spinStart[ch] = (uint8_t)v;
    Serial.print(F("SPIN=OK,")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(','); Serial.println(v);
  }
}

// HOLD=<канал>,<проц>[,<сек>] / HOLD=<канал>,OFF / HOLD=ALL,OFF
void handleHoldCommand(char* args) {
  char* a = strtok(args, ",");
  char* b = strtok(NULL, ",");
  char* c = strtok(NULL, ",");
  if (!a || !b) { Serial.println(F("HOLD=ERR,args")); return; }
  if (strcmp(a, "ALL") == 0 && strcmp(b, "OFF") == 0) {
    for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) releaseHold(i, NULL);
    Serial.println(F("HOLD=OFF,ALL"));
    return;
  }
  uint8_t ch;
  if (!parseChannelToken(a, ch)) { Serial.println(F("HOLD=ERR,channel")); return; }
  if (strcmp(b, "OFF") == 0) {
    releaseHold(ch, NULL);
    Serial.print(F("HOLD=OFF,")); Serial.println(FAN_CHANNELS[ch].name);
    return;
  }
  if (watchdogTripped && channelSource[ch].type == SRC_SERIAL) { Serial.println(F("HOLD=ERR,failsafe")); return; }
  int pct = constrain(atoi(b), 0, 100);
  long sec = c ? atol(c) : (long)HOLD_DEFAULT_SEC;
  if (sec < 1) sec = 1;
  if (sec > (long)HOLD_MAX_SEC) sec = (long)HOLD_MAX_SEC;
  holdActive[ch] = true;
  holdUntilMs[ch] = millis() + (unsigned long)sec * 1000UL;
  setChannel(ch, (uint8_t)pct);
  Serial.print(F("HOLD=OK,")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
  Serial.print(pct); Serial.print(','); Serial.println(sec);
}

void dumpHolds() {
  unsigned long now = millis();
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!holdActive[ch]) continue;
    long left = (long)(holdUntilMs[ch] - now) / 1000L;
    if (left < 0) left = 0;
    Serial.print(F("HOLD=")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    Serial.print(getChannelPercent(ch)); Serial.print(','); Serial.println(left);
  }
  Serial.println(F("HOLD=END"));
}

void loopHeartbeat() {
  unsigned long now = millis();
  if (now - lastHeartbeatMs < HEARTBEAT_MS) return;
  lastHeartbeatMs = now;
  Serial.print(F("EVENT=HB,")); Serial.println(now / 1000UL);
}

// Всё сразу: режим, Serial-значения, термисторы, каналы, тахо, watchdog
void dumpStatus() {
  Serial.print(F("STATUS=MODE,")); Serial.println(CTRL_MODE_NAMES[controllerMode]);

  for (uint8_t i = 0; i < NUM_SERIAL_KEYS; i++) {
    Serial.print(F("STATUS=SERIAL,")); Serial.print(SERIAL_KEY_NAMES[i]); Serial.print(',');
    Serial.println(serialValues[i], 1);
  }

  for (uint8_t i = 0; i < NUM_THERMISTORS; i++) { Serial.print(F("STATUS=THERM,")); printThermLine(i); }

  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    Serial.print(F("STATUS=CH,")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    Serial.print(getChannelPercent(ch)); Serial.print(',');
    printSource(ch);
    Serial.print(isBindingLocked(ch) ? F(",LOCKED") : F(",FREE"));
    Serial.println(chActive[ch] ? F(",ACTIVE") : F(",INACTIVE"));
  }

  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) { Serial.print(F("STATUS=TACH,")); printTachLine(ch); }

  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (!swMode[ch]) continue;
    Serial.print(F("STATUS=SPIN,")); Serial.print(FAN_CHANNELS[ch].name); Serial.print(',');
    Serial.print(FS_NAMES[fsState[ch]]); Serial.print(','); Serial.println(fsTries[ch]);
  }

  Serial.print(F("STATUS=WATCHDOG,")); Serial.println(watchdogTripped ? F("TRIPPED") : F("OK"));
  Serial.println(F("STATUS=END"));
}

// CURVE=SAVE / CURVE=RESET / CURVE=<Имя>,<idx>,<tempC>,<percent>
void handleCurveCommand(char* args) {
  if (strcmp(args, "SAVE") == 0) { saveCurvesToEeprom(); Serial.println(F("CURVE=SAVED")); return; }
  if (strcmp(args, "RESET") == 0) { resetCurvesToDefaults(); Serial.println(F("CURVE=RESET_OK")); return; }

  char* name   = strtok(args, ",");
  char* idxStr = strtok(NULL, ",");
  char* tStr   = strtok(NULL, ",");
  char* pStr   = strtok(NULL, ",");
  if (!name || !idxStr || !tStr || !pStr) { Serial.println(F("CURVE=ERR,args")); return; }

  uint8_t ch;
  if (!channelIndexByName(name, ch)) { Serial.println(F("CURVE=ERR,channel")); return; }

  int idx = atoi(idxStr);
  if (idx < 0 || idx >= (int)CURVE_POINTS) { Serial.println(F("CURVE=ERR,idx")); return; }

  channelCurve[ch][idx].tempC   = (int16_t)atoi(tStr);
  channelCurve[ch][idx].percent = (uint8_t)constrain(atoi(pStr), 0, 100);

  Serial.print(F("CURVE=OK,")); Serial.print(name); Serial.print(',');
  Serial.print(idx); Serial.print(',');
  Serial.print(channelCurve[ch][idx].tempC); Serial.print(',');
  Serial.println(channelCurve[ch][idx].percent);
  // в EEPROM не сохраняется автоматически - отдельно CURVE=SAVE
}

// BIND=SAVE / BIND=RESET / BIND=<Имя>,THERM,<имя термистора> / BIND=<Имя>,SERIAL,<ключ>
void handleBindCommand(char* args) {
  if (strcmp(args, "SAVE") == 0) { saveBindsToEeprom(); Serial.println(F("BIND=SAVED")); return; }
  if (strcmp(args, "RESET") == 0) { resetBindsToDefaults(); Serial.println(F("BIND=RESET_OK")); return; }

  char* name   = strtok(args, ",");
  char* srcStr = strtok(NULL, ",");
  char* argStr = strtok(NULL, ",");
  if (!name || !srcStr || !argStr) { Serial.println(F("BIND=ERR,args")); return; }

  uint8_t ch;
  if (!channelIndexByName(name, ch)) { Serial.println(F("BIND=ERR,channel")); return; }

  if (isBindingLocked(ch)) {
    Serial.print(F("BIND=LOCKED,")); Serial.println(name); // переключись в TEMP_MANUAL (MODE=1)
    return;
  }

  if (strcmp(srcStr, "THERM") == 0) {
    uint8_t ti = 0; bool found = false;
    for (ti = 0; ti < NUM_THERMISTORS; ti++) if (strcmp(argStr, THERM_NAMES[ti]) == 0) { found = true; break; }
    if (!found) { Serial.println(F("BIND=ERR,source")); return; }
    channelSource[ch] = { SRC_THERM, ti };
  } else if (strcmp(srcStr, "SERIAL") == 0) {
    uint8_t si = 0; bool found = false;
    for (si = 0; si < NUM_SERIAL_KEYS; si++) if (strcmp(argStr, SERIAL_KEY_NAMES[si]) == 0) { found = true; break; }
    if (!found) { Serial.println(F("BIND=ERR,source")); return; }
    channelSource[ch] = { SRC_SERIAL, si };
  } else {
    Serial.println(F("BIND=ERR,source"));
    return;
  }

  Serial.print(F("BIND=OK,")); Serial.println(name);
}

void handleSerialLine(char* line) {
  if (strncmp(line, "SET=", 4) == 0) {
    char* rest = line + 4;
    char* comma = strchr(rest, ',');
    if (comma) {
      *comma = '\0';
      int ch = atoi(rest);
      int pct = atoi(comma + 1);
      feedWatchdog();
      setChannel(ch, (uint8_t)constrain(pct, 0, 100));
    }
  } else if (strncmp(line, "TEMP=", 5) == 0) {
    feedWatchdog();
    char* rest = line + 5;
    char* tok = strtok(rest, ",");
    while (tok != NULL) {
      char* colon = strchr(tok, ':');
      if (colon) {
        *colon = '\0';
        float val = atof(colon + 1);
        for (uint8_t i = 0; i < NUM_SERIAL_KEYS; i++) {
          if (strcmp(tok, SERIAL_KEY_NAMES[i]) == 0) { serialValues[i] = val; break; }
        }
        // неизвестный ключ - молча игнорируем
      }
      tok = strtok(NULL, ",");
    }
    applySerialCurves();
  } else if (strncmp(line, "MODE?", 5) == 0) {
    Serial.print(F("MODE=")); Serial.println((int)controllerMode);
  } else if (strncmp(line, "MODE=", 5) == 0) {
    int m = atoi(line + 5);
    if (m >= 0 && m < CTRL_MODE_COUNT) setControllerMode((ControllerMode)m);
  } else if (strncmp(line, "CURVES?", 7) == 0) {
    dumpCurves();
  } else if (strncmp(line, "CURVE=", 6) == 0) {
    handleCurveCommand(line + 6);
  } else if (strncmp(line, "BINDS?", 6) == 0) {
    dumpBinds();
  } else if (strncmp(line, "BIND=", 5) == 0) {
    handleBindCommand(line + 5);
  } else if (strncmp(line, "THERMS?", 7) == 0) {
    dumpTherms();
  } else if (strncmp(line, "SPIN?", 5) == 0) {
    dumpSpin();
  } else if (strncmp(line, "SPIN=", 5) == 0) {
    handleSpinCommand(line + 5);
  } else if (strncmp(line, "DCFREQ?", 7) == 0) {
    dumpDcFreq();
  } else if (strncmp(line, "DCFREQ=", 7) == 0) {
    handleDcFreqCommand(line + 7);
  } else if (strncmp(line, "HOLD?", 5) == 0) {
    dumpHolds();
  } else if (strncmp(line, "HOLD=", 5) == 0) {
    handleHoldCommand(line + 5);
  } else if (strncmp(line, "TACHDUMP=", 9) == 0) {
    char* a = strtok(line + 9, ",");
    char* b = strtok(NULL, ",");
    char* c = strtok(NULL, ",");
    uint8_t ch;
    if (!parseChannelToken(a, ch)) Serial.println(F("TACHDUMP=ERR,channel"));
    else runTachDump(ch, b ? (uint16_t)atoi(b) : TACHDUMP_DEF_BIN_US, c ? (uint16_t)atoi(c) : TACHDUMP_MAX_BINS);
  } else if (strncmp(line, "TACHFAST=", 9) == 0) {
    uint8_t ch;
    if (!parseChannelToken(line + 9, ch)) Serial.println(F("TACHDUMP=ERR,channel"));
    else runTachDump(ch, TACHFAST_BIN_US, TACHDUMP_MAX_BINS);
  } else if (strncmp(line, "TACHRAW=", 8) == 0) {
    dumpTachRaw(atoi(line + 8));
  } else if (strncmp(line, "TACHTEST", 8) == 0) {
    startTachTest();
  } else if (strncmp(line, "TACH?", 5) == 0) {
    dumpTach();
  } else if (strncmp(line, "STATUS?", 7) == 0) {
    dumpStatus();
  }
}

void pollSerial() {
  while (Serial.available() > 0) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (serialBufLen > 0) {
        serialBuf[serialBufLen] = '\0';
        handleSerialLine(serialBuf);
        serialBufLen = 0;
      }
    } else if (serialBufLen < sizeof(serialBuf) - 1) {
      serialBuf[serialBufLen++] = c;
    }
  }
}

// ============================================================================
//                              ФУНКЦИИ: КНОПКА
// ============================================================================

void onShortPress() {
  Serial.println(F("EVENT=BTN,SHORT"));
  currentScreen = (InfoScreen)((currentScreen + 1) % SCR_COUNT);
  lastScreenChangeMillis = millis();
}

void showModeChangeFlash(ControllerMode m); // прототип (определена ниже, в блоке дисплея)

void onLongPress() {
  Serial.println(F("EVENT=BTN,LONG"));
  ControllerMode next = (controllerMode == TEMP_SERIAL) ? TEMP_MANUAL : TEMP_SERIAL;
  setControllerMode(next);
  showModeChangeFlash(next);
}

// Опрос кнопки: вызывается из прерывания Timer0 (~1 кГц). Только ставит флаги событий.
void buttonTick() {
  bool raw = digitalRead(PIN_BUTTON);
  if (raw != buttonRawLast) {
    lastDebounceTime = millis();
  }
  buttonRawLast = raw;

  if (millis() - lastDebounceTime > DEBOUNCE_MS) {
    if (raw != buttonStable) {
      buttonStable = raw;
      if (buttonStable == LOW) {
        buttonIsDown = true;
        pressStartMillis = millis();
        longPressFired = false;
      } else {
        if (buttonIsDown && !longPressFired) {
          evShortPress = true;
        }
        buttonIsDown = false;
      }
    }
  }

  if (buttonIsDown && !longPressFired && (millis() - pressStartMillis >= LONG_PRESS_MS)) {
    longPressFired = true;
    evLongPress = true;
  }
}

ISR(TIMER0_COMPA_vect) { buttonTick(); }
ISR(TIMER2_COMPA_vect) { swPwmTick(); }

// Из loop(): выполняет действия, накопленные прерыванием
void loopButton() {
  bool s, l;
  noInterrupts();
  s = evShortPress; l = evLongPress;
  evShortPress = false; evLongPress = false;
  interrupts();
  if (s) onShortPress();
  if (l) onLongPress();
}

void loopDisplayRevert() {
  if (!INFO_IDLE_REVERT_ENABLED) return;
  if (currentScreen != SCR_DEFAULT && millis() - lastScreenChangeMillis > INFO_IDLE_REVERT_MS) {
    currentScreen = SCR_DEFAULT;
  }
}

// ============================================================================
//                              ФУНКЦИИ: ДИСПЛЕЙ
// ============================================================================

// Экран собирается в буфер текстовых строк (scr), потом выводится на дисплей.
// Так одни и те же экраны годятся для Nokia (6 строк x 14), OLED 128x64 (8 x 25) и OLED 128x32 (4 x 25).
// Если строк больше, чем помещается, экран листается: 1-я строка (заголовок) остаётся, остальные меняются.
const uint8_t SCR_MAX_ROWS = 8;
#if DISPLAY_TYPE == 1
const uint8_t SCR_COLS = 15;       // левая часть OLED: 15 символов по 5 пикселей, дальше разделитель и правая панель
const bool SPLIT_LAYOUT = true;
const uint8_t DIVIDER_X = 78;      // вертикальная линия
const uint8_t RIGHT_X = 82;        // начало правой панели (до 128 px = 9 символов)
#else
const uint8_t SCR_COLS = 24;
const bool SPLIT_LAYOUT = false;
#endif
const unsigned long SCR_PAGE_MS = 2500;

#if DISPLAY_TYPE == 1
const uint8_t VISIBLE_ROWS = OLED_HEIGHT / 8;
#else
const uint8_t VISIBLE_ROWS = 6;
#endif

class LineBuf : public Print {
 public:
  char rows[SCR_MAX_ROWS][SCR_COLS + 1];
  uint8_t row = 0, col = 0;
  void clear() { memset(rows, 0, sizeof(rows)); row = 0; col = 0; }
  uint8_t used() const { return (row >= SCR_MAX_ROWS) ? SCR_MAX_ROWS : (uint8_t)(row + (col > 0 ? 1 : 0)); }
  size_t write(uint8_t c) override {
    if (c == '\r') return 1;
    if (c == '\n') { if (row < SCR_MAX_ROWS) row++; col = 0; return 1; }
    if (row < SCR_MAX_ROWS && col < SCR_COLS) rows[row][col++] = (char)c;
    return 1;
  }
};
LineBuf scr;

bool displayOk = true;   // у программного I2C проверить наличие дисплея нельзя, всегда true

void drawRow(uint8_t y, const char* text) {
#if DISPLAY_TYPE == 1
  display.drawStr(0, y * 8, text);
#else
  display.setCursor(0, y * 8);
  display.print(text);
#endif
}

#if DISPLAY_TYPE == 1
// Правая панель OLED: статично режим, FailSafe и номер экрана
void drawRightPanel() {
  display.drawVLine(DIVIDER_X, 0, OLED_HEIGHT);
  display.setDrawColor(1);
  display.drawStr(RIGHT_X, 0, (controllerMode == TEMP_MANUAL) ? "Mode: MAN" : "Mode: SER");
  if (watchdogTripped) {
    display.drawBox(RIGHT_X - 2, 8, 128 - (RIGHT_X - 2), 9);   // инвертированная плашка
    display.setDrawColor(0);
    display.drawStr(RIGHT_X, 9, "FAILSAFE!");
    display.setDrawColor(1);
  } else {
    display.drawStr(RIGHT_X, 8, "FS: OK");
  }
  char b[10];
  snprintf(b, sizeof(b), "Scr %u/%u", (unsigned)(currentScreen + 1), (unsigned)SCR_COUNT);
  display.drawStr(RIGHT_X, OLED_HEIGHT - 8, b);
}
#endif

// Вывод буфера scr на дисплей. allowPaging=false - показать только первые VISIBLE_ROWS строк
void flushScreen(bool allowPaging) {
  uint8_t total = scr.used();
  uint8_t first = 0;      // с какой строки (после заголовка) начинать
  bool header = false;
  if (allowPaging && total > VISIBLE_ROWS && VISIBLE_ROWS >= 2) {
    uint8_t body = VISIBLE_ROWS - 1;
    uint8_t pages = (uint8_t)((total - 1 + body - 1) / body);
    uint8_t page = (uint8_t)((millis() / SCR_PAGE_MS) % pages);
    first = (uint8_t)(1 + page * body);
    header = true;
  }
#if DISPLAY_TYPE == 1
  display.clearBuffer();
#else
  display.clearDisplay();
  display.setTextSize(1);
  display.setTextColor(TEXT_ON);
#endif
  uint8_t y = 0;
  if (header) { drawRow(y++, scr.rows[0]); }
  for (uint8_t r = first; r < total && y < VISIBLE_ROWS; r++) drawRow(y++, scr.rows[r]);
#if DISPLAY_TYPE == 1
  drawRightPanel();
  display.sendBuffer();
#else
  display.display();
#endif
}

// Значение термистора для экрана: число, "--" (нет датчика), "ERR" (авария), "off"
void printThermField(uint8_t i) {
  switch (thermState[i]) {
    case TS_OK:    scr.print(thermValues[i], 1); break;
    case TS_FAULT: scr.print("ERR"); break;
    case TS_OFF:   scr.print("off"); break;
    default:       scr.print("--"); break;
  }
}

// Строка канала для экрана FANS: "0  45% 1234" / "0  45%   --" / "9 off"
void printFanRow(uint8_t ch) {
  char buf[16];
  if (!chActive[ch]) {
    snprintf(buf, sizeof(buf), "%u off", (unsigned)ch);
  } else {
    TachState ts = tachStateOf(ch);
    char rpm[8];
    if (ts == TACH_OK) snprintf(rpm, sizeof(rpm), "%4u", (unsigned)tachRpm[ch]);
    else if (ts == TACH_STALL) snprintf(rpm, sizeof(rpm), "STOP");
    else if (ts == TACH_PWM) snprintf(rpm, sizeof(rpm), " PWM");
    else snprintf(rpm, sizeof(rpm), "  --");
    snprintf(buf, sizeof(buf), "%u %3u%% %s", (unsigned)ch, (unsigned)getChannelPercent(ch), rpm);
  }
  scr.println(buf);
}

void renderDisplay() {
  if (!displayOk) return;
  scr.clear();

  if (tachTestActive) {
    unsigned long el = millis() - tachTestStart;
    unsigned long left = (el < TACHTEST_SPINUP_MS) ? (TACHTEST_SPINUP_MS - el) / 1000 + 1 : 0;
    scr.println("-- TACH TEST --");
    scr.print("3-pin 100% "); scr.print((unsigned)left); scr.println("s");
    flushScreen(false);
    return;
  }

  switch (currentScreen) {
    case SCR_DEFAULT:
      if (SPLIT_LAYOUT) {
        scr.print("P102:"); scr.print(serialValues[SK_P102], 0);
        scr.print(" GTX:"); scr.println(serialValues[SK_GTX], 0);
        scr.print("CPU:"); scr.print(serialValues[SK_CPU], 0);
        scr.print(" SSD:"); scr.println(serialValues[SK_SSD], 0);
        scr.print("Case:"); printThermField(0); scr.println();
        scr.print("GPU fan:"); scr.print(getChannelPercent(CH_GPU)); scr.println("%");
      } else {
        scr.print("Mode:");
        scr.println(CTRL_MODE_NAMES[controllerMode]);
        scr.print("P:"); scr.print(serialValues[SK_P102], 0);
        scr.print(" G:"); scr.println(serialValues[SK_GTX], 0);
        scr.print("C:"); scr.print(serialValues[SK_CPU], 0);
        scr.print(" S:"); scr.println(serialValues[SK_SSD], 0);
        scr.print("Ch0:"); scr.print(getChannelPercent(CH_GPU)); scr.println("%");
      }
      break;

    case SCR_GPU:
      scr.println("-- GPU --");
      scr.print("P102:"); scr.println(serialValues[SK_P102], 1);
      scr.print("1060:"); scr.println(serialValues[SK_GTX],  1);
      scr.print("Ch0 duty:"); scr.print(getChannelPercent(CH_GPU)); scr.println("%");
      break;

    case SCR_CPU:
      scr.println("-- CPU --");
      scr.print("Temp:"); scr.println(serialValues[SK_CPU], 1);
      break;

    case SCR_SSD:
      scr.println("-- SSD --");
      scr.print("Temp:"); scr.println(serialValues[SK_SSD], 1);
      break;

    case SCR_CASE:
      scr.println("-- CASE --");
      scr.print("Case:"); printThermField(0); scr.println();
      scr.print("In :"); scr.print(getChannelPercent(CH_INTAKE));  scr.println("%");
      scr.print("Out:"); scr.print(getChannelPercent(CH_EXHAUST)); scr.println("%");
      break;

    case SCR_TEMPS:
      scr.println((watchdogTripped && !SPLIT_LAYOUT) ? "** FAILSAFE **" : "-- TEMPS --");
      for (uint8_t i = 0; i < NUM_THERMISTORS; i++) {
        scr.print(THERM_NAMES[i]); scr.print(' ');
        if (strlen(THERM_NAMES[i]) < 4) scr.print(' ');
        printThermField(i); scr.println();
      }
      break;

    case SCR_FANS_A:
      scr.println((watchdogTripped && !SPLIT_LAYOUT) ? "** FAILSAFE **" : "FAN 0-4  % rpm");
      for (uint8_t ch = 0; ch < 5; ch++) printFanRow(ch);
      break;

    case SCR_FANS_B:
      scr.println((watchdogTripped && !SPLIT_LAYOUT) ? "** FAILSAFE **" : "FAN 5-9  % rpm");
      for (uint8_t ch = 5; ch < TOTAL_CHANNELS; ch++) printFanRow(ch);
      break;

    default:
      break;
  }

  // На экранах с собственным заголовком FAILSAFE показан в заголовке
  if (!SPLIT_LAYOUT && watchdogTripped && currentScreen != SCR_TEMPS && currentScreen != SCR_FANS_A && currentScreen != SCR_FANS_B) {
    scr.println("** FAILSAFE **");
  }

  flushScreen(true);
}

void showModeChangeFlash(ControllerMode m) {
  if (!displayOk) return;
  scr.clear();
  scr.println();
  scr.print("MODE -> ");
  scr.println(CTRL_MODE_NAMES[m]);
  flushScreen(false);
  delay(800); // короткая пауза после явного действия пользователя; на вентиляторы не влияет
}

// ============================================================================
//                              SETUP / LOOP
// ============================================================================

uint8_t resetCauseFlags = 0;

void setup() {
  resetCauseFlags = MCUSR;
  MCUSR = 0;
  Serial.begin(115200);

  pinMode(PIN_BUTTON, INPUT_PULLUP);
  OCR0A = 128;               // Timer0 COMPA - опрос кнопки (~1 кГц); D13 (OC0A) в проекте не используется
  TIMSK0 |= _BV(OCIE0A);

  // Какие каналы доступны (пин не занят дисплеем)
  for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) chActive[i] = !pinReservedByDisplay(FAN_CHANNELS[i].pin);

  // Состояние термисторов: включённые - ABSENT до первых нормальных отсчётов
  for (uint8_t i = 0; i < NUM_THERMISTORS; i++) {
    thermState[i] = THERM_ENABLED[i] ? TS_ABSENT : TS_OFF;
    thermValues[i] = NAN;
    thermGood[i] = 0; thermBad[i] = 0; thermFaultKind[i] = 0;
  }

  // Кривые и привязки: сперва заводские, затем сохранённые в EEPROM
  memcpy(channelCurve,  CURVE_DEFAULT,          sizeof(CURVE_DEFAULT));
  memcpy(channelSource, CHANNEL_SOURCE_DEFAULT, sizeof(CHANNEL_SOURCE_DEFAULT));
  loadCurvesFromEeprom();
  loadBindsFromEeprom();
  // Защита от повреждённой привязки в EEPROM
  for (uint8_t ch = 0; ch < TOTAL_CHANNELS; ch++) {
    if (channelSource[ch].type == SRC_THERM && channelSource[ch].index >= NUM_THERMISTORS)
      channelSource[ch] = CHANNEL_SOURCE_DEFAULT[ch];
    if (channelSource[ch].type == SRC_SERIAL && channelSource[ch].index >= NUM_SERIAL_KEYS)
      channelSource[ch] = CHANNEL_SOURCE_DEFAULT[ch];
  }

  for (uint8_t i = 0; i < TOTAL_CHANNELS; i++) if (chActive[i]) pinMode(FAN_CHANNELS[i].pin, OUTPUT);
  initSoftPwm();      // D7-D10: программный ШИМ - до настройки Timer4
  initTimer1_25kHz();
  initTimer3_25kHz();
  initTimer4_25kHz();
  initTimer5_25kHz();
  loadDcFreqFromEeprom();
  initSpinDefaults();
  loadSpinFromEeprom();
  initSoftPwmTimer();

  // На старте - fail-safe (100%) везде, пока не посчитаются первые значения.
  if (FAILSAFE_ENABLED) setAllChannels(100);

  controllerMode = loadControllerMode();
  feedWatchdog();

  tachSetup();

#if DISPLAY_TYPE == 1
  display.begin();
  display.setFont(u8g2_font_5x8_tr);   // 5x8, 25 символов в строке, 8 пикселей на строку
  display.setFontPosTop();
  display.clearBuffer();
  display.sendBuffer();
#else
  display.begin();
  display.setContrast(LCD_CONTRAST);
  display.setRotation(LCD_ROTATION);
  display.clearDisplay();
  display.display();
#endif

  lastScreenChangeMillis = millis();
  lastDisplayRefresh = millis();

  Serial.print(F("MKS Fan Hub v0.3.25 alive. CtrlMode="));
  Serial.print(CTRL_MODE_NAMES[controllerMode]);
  Serial.print(F(" Channels="));
  Serial.print(TOTAL_CHANNELS);
  Serial.print(F(" Display="));
  Serial.println(DISPLAY_TYPE == 1 ? F("OLED-I2C(AUX-3 D50/D52)") : DISPLAY_LAYOUT >= 2 ? F("AUX-2") : (DISPLAY_LAYOUT == 1 ? F("EXP1") : F("SERVOS2")));
  Serial.print(F("EVENT=BOOT,0.3.25,"));
  if (!resetCauseFlags) Serial.print('?');
  if (resetCauseFlags & _BV(PORF))  Serial.print('P');
  if (resetCauseFlags & _BV(EXTRF)) Serial.print('E');
  if (resetCauseFlags & _BV(BORF))  Serial.print('B');
  if (resetCauseFlags & _BV(WDRF))  Serial.print('W');
  if (resetCauseFlags & _BV(JTRF))  Serial.print('J');
  Serial.println();
  lastHeartbeatMs = millis();
}

void loop() {
  pollSerial();
  loopButton();
  loopDisplayRevert();
  checkWatchdog();
  loopSpin();         // автозапуск DC-вентиляторов
  loopHold();         // окончание ручного удержания (HOLD=)
  loopHeartbeat();    // EVENT=HB каждые 5 с
  applyThermCurves(); // термисторные каналы - всегда, throttled
  loopTachGuard();    // защита от шторма помех на tach-входах
  tachGateUpdate();   // прерывания tach 3-pin каналов - только на 100% PWM
  updateTach();       // тахометры - только информация
  loopTachTest();     // команда TACHTEST

  if (millis() - lastDisplayRefresh >= DISPLAY_REFRESH_MS) {
    lastDisplayRefresh = millis();
    renderDisplay();
  }
}
