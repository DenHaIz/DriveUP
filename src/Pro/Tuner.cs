using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DriveUp
{
    /// <summary>A mode sets these values, each with the reason it suits this computer.</summary>
    class TuneStep
    {
        public Tweak Tweak;
        public string Value;
        public Func<string> Reason;
        /// <summary>A trade-off: offered, but not ticked until the user decides (shown with Warn).</summary>
        public bool Optional;
        public Func<string> Warn;
    }

    class TuneMode
    {
        public string Id, Glyph;
        public Func<string> Name, Desc;
        public Func<Machine, List<TuneStep>> Steps;
        /// <summary>Startup programs by level (1 clutter, 2 heavy, 3 cloud): turned off ticked / offered unticked.</summary>
        public int[] StartupOff = new int[0], StartupOffer = new int[0];
    }

    enum TuneKind
    {
        /// <summary>The mode changes this setting.</summary>
        Change,
        /// <summary>Already as the mode wants it.</summary>
        Same,
        /// <summary>Changed earlier by another mode; this one puts it back as it was.</summary>
        Return,
        /// <summary>A row of the "Changed" list: was → now, with its own "Return" button.</summary>
        Changed,
        /// <summary>A group title ("Power and processor"): ticks or unticks the whole group.</summary>
        Header,
    }

    enum TuneState { Idle, Working, Done, Failed }

    class TuneRow
    {
        public Tweak Tweak;
        public TuneKind Kind;
        public TuneCat Category;
        /// <summary>Change: the value to set.</summary>
        public string Target = "";
        /// <summary>Values (shown through Tweak.Describe): now, what it gets, what it was before DriveUP Pro.</summary>
        public string Now = "", To = "", Was = "";
        public Func<string> Reason = () => "";
        public bool Checked = true;
        /// <summary>The catch of an optional change ("lowers protection"), shown as a warning.</summary>
        public Func<string> Warn;
        public TuneState State;
        public string Error = "";
        /// <summary>Changed rows: which mode changed it and when.</summary>
        public string ModeId = "";
        public DateTime When;

        public bool Actionable { get { return Kind == TuneKind.Change || Kind == TuneKind.Return; } }
    }

    /// <summary>
    /// The modes of DriveUP Pro and what they change. Rules, not guesses: every value is a known
    /// Windows setting, chosen from what the computer is (laptop or desktop, on battery, processor,
    /// memory, system drive, graphics card, Windows version). Before a setting is first changed its
    /// exact old state is saved (Settings.TuneSaved), so each one can be put back on its own.
    /// </summary>
    static class Tuner
    {
        public static readonly List<Tweak> Catalog = new List<Tweak>();
        public static readonly List<TuneMode> Modes = new List<TuneMode>();
        /// <summary>The computer the rows were made for (texts show the battery side only on laptops).</summary>
        public static Machine Pc;

        public static string CategoryName(TuneCat c)
        {
            switch (c)
            {
                case TuneCat.Power: return L.T("Питание и процессор", "Power and processor");
                case TuneCat.Gaming: return L.T("Игры и графика", "Games and graphics");
                case TuneCat.Interface: return L.T("Интерфейс", "Interface");
                case TuneCat.Services: return L.T("Службы", "Services");
                case TuneCat.Tasks: return L.T("Фоновые задачи", "Background tasks");
                case TuneCat.Startup: return L.T("Автозагрузка", "Startup");
                case TuneCat.Privacy: return L.T("Реклама и подсказки", "Ads and suggestions");
                case TuneCat.System: return L.T("Система и защита", "System and security");
                default: return L.T("Обновления", "Updates");
            }
        }

        static T Add<T>(T t, string id, TuneCat cat, string glyph, Func<string> name) where T : Tweak
        {
            t.Id = id;
            t.Category = cat;
            t.Glyph = glyph;
            t.Name = name;
            Catalog.Add(t);
            return t;
        }

        static ServiceTweak Service(string name, Func<string> title)
        {
            var s = new ServiceTweak(name) { Name = title };
            Catalog.Add(s);
            return s;
        }

        static string Percent(uint v) { return v.ToString(CultureInfo.InvariantCulture) + " %"; }

        static Tuner()
        {
            // ----- power: the plan first, then the settings inside it (they go to the plan that ends up active) -----
            var power = Add(new PowerPlan(), "power", TuneCat.Power, "\uE945", () => L.T("Схема питания", "Power plan"));
            var boost = Add(new PowerSetting(PowerApi.SubProcessor, PowerApi.BoostMode)
            {
                Text = v =>
                {
                    switch (v)
                    {
                        case 0: return L.T("выключен", "off");
                        case 1: return L.T("включён", "on");
                        case 2: return L.T("агрессивный", "aggressive");
                        case 3: return L.T("экономный", "efficient");
                        case 4: return L.T("экономный агрессивный", "efficient aggressive");
                        default: return v.ToString(CultureInfo.InvariantCulture);
                    }
                },
            }, "boost", TuneCat.Power, "\uEC4A", () => L.T("Турбо-буст процессора", "Processor boost"));
            var minState = Add(new PowerSetting(PowerApi.SubProcessor, PowerApi.MinState) { Text = Percent },
                "minstate", TuneCat.Power, "\uE9D9", () => L.T("Минимальная частота процессора", "Minimum processor state"));
            var cores = Add(new PowerSetting(PowerApi.SubProcessor, PowerApi.MinCores)
            {
                Text = v => v >= 100 ? L.T("все ядра активны", "all cores active") : L.T("парковка, от ", "parking, at least ") + v + L.T(" % ядер", " % of cores"),
            }, "cores", TuneCat.Power, "\uE950", () => L.T("Парковка ядер", "Core parking"));
            var epp = Add(new PowerSetting(PowerApi.SubProcessor, PowerApi.EnergyPreference)
            {
                Text = v => v + (v <= 10 ? L.T(" — скорость", " — speed") : v >= 60 ? L.T(" — экономия", " — saving") : L.T(" — баланс", " — balance")),
            }, "epp", TuneCat.Power, "\uE9F5", () => L.T("Приоритет энергии процессора (EPP)", "Processor energy preference (EPP)"));
            var aspm = Add(new PowerSetting(PowerApi.SubPciExpress, PowerApi.LinkPower)
            {
                Text = v => v == 0 ? L.T("выключено", "off") : v == 1 ? L.T("умеренное", "moderate") : L.T("максимальное", "maximum"),
            }, "aspm", TuneCat.Power, "\uE964", () => L.T("Энергосбережение PCI Express", "PCI Express power saving"));
            var usb = Add(new PowerSetting(PowerApi.SubUsb, PowerApi.UsbSuspend)
            {
                Text = v => v == 0 ? L.T("выключено", "off") : L.T("включено", "on"),
            }, "usb", TuneCat.Power, "\uE88E", () => L.T("Засыпание USB-устройств", "USB selective suspend"));
            var disk = Add(new PowerSetting(PowerApi.SubDisk, PowerApi.DiskIdle)
            {
                Text = v => v == 0 ? L.T("никогда", "never") : L.T("через ", "after ") + Math.Max(1, v / 60) + L.T(" мин", " min"),
            }, "diskidle", TuneCat.Power, "\uEDA2", () => L.T("Остановка жёсткого диска", "Hard disk spin-down"));

            // ----- games and graphics -----
            var gameMode = Add(new RegSwitch(false, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0) { OnWhenAbsent = () => true, When = m => m.Build >= 15063 },
                "gamemode", TuneCat.Gaming, "\uE7FC", () => L.T("Игровой режим Windows", "Windows Game Mode"));
            var record = Add(new RegSwitch(false, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 1, 0)
            { OnText = () => L.T("включена", "on"), OffText = () => L.T("выключена", "off") },
                "bgrecord", TuneCat.Gaming, "\uE714", () => L.T("Фоновая запись игр", "Background game recording"));
            Machine seen = null;
            var hags = Add(new RegSwitch(true, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1) { When = m => { seen = m; return m.HagsSupported; } },
                "hags", TuneCat.Gaming, "\uE7F4", () => L.T("Аппаратное планирование GPU", "Hardware-accelerated GPU scheduling"));
            hags.NeedsReboot = true;
            hags.OnWhenAbsent = () => seen != null && seen.HagsEnabled;
            var windowed = Add(new WindowedGames(), "windowed", TuneCat.Gaming, "\uE737", () => L.T("Оптимизация оконных игр", "Optimizations for windowed games"));
            var mouse = Add(new MouseAcceleration(), "mouse", TuneCat.Gaming, "\uE962", () => L.T("Повышенная точность указателя", "Enhance pointer precision"));

            // ----- interface -----
            var anim = Add(new Animations(), "animations", TuneCat.Interface, "\uE7C4", () => L.T("Анимации окон и панели задач", "Window and taskbar animations"));
            var glass = Add(new RegSwitch(false, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1, 0)
            { OnWhenAbsent = () => true, Changed = () => SettingsBroadcast.Send("ImmersiveColorSet") },
                "transparency", TuneCat.Interface, "\uE790", () => L.T("Прозрачность окон", "Transparency effects"));

            // ----- services: only ones that are safe to stop; Xbox, security and update services are never touched -----
            var diag = Service("DiagTrack", () => L.T("Телеметрия Windows (DiagTrack)", "Windows telemetry (DiagTrack)"));
            var wap = Service("dmwappushservice", () => L.T("Маршрутизация push-сообщений WAP", "WAP push message routing"));
            var maps = Service("MapsBroker", () => L.T("Диспетчер скачанных карт", "Downloaded maps manager"));
            var links = Service("TrkWks", () => L.T("Отслеживание изменившихся связей", "Distributed link tracking"));
            var nvTele = Service("NvTelemetryContainer", () => L.T("Телеметрия NVIDIA", "NVIDIA telemetry"));
            var sysMain = Service("SysMain", () => L.T("SysMain (Superfetch)", "SysMain (Superfetch)"));
            var search = Service("WSearch", () => L.T("Индексирование поиска Windows", "Windows Search indexing"));

            // ----- background tasks -----
            var tasks = Add(new TaskSet(
                    @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                    @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                    @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                    @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                    @"\Microsoft\Windows\Autochk\Proxy",
                    @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
                    @"\Microsoft\Windows\Feedback\Siuf\DmClient",
                    @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
                    @"\Microsoft\Office\OfficeTelemetryAgentLogOn2016",
                    @"\Microsoft\Office\OfficeTelemetryAgentFallBack2016"),
                "telemetrytasks", TuneCat.Tasks, "\uE9F9", () => L.T("Задачи сбора данных", "Data collection tasks"));

            // ----- ads and suggestions -----
            const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            var ads = Add(new RegSet()
                    .Add(false, Cdm, "SilentInstalledAppsEnabled", 0).Add(false, Cdm, "SystemPaneSuggestionsEnabled", 0)
                    .Add(false, Cdm, "SoftLandingEnabled", 0).Add(false, Cdm, "SubscribedContent-338388Enabled", 0)
                    .Add(false, Cdm, "SubscribedContent-338389Enabled", 0).Add(false, Cdm, "SubscribedContent-353694Enabled", 0)
                    .Add(false, Cdm, "SubscribedContent-353696Enabled", 0).Add(false, Cdm, "SubscribedContent-310093Enabled", 0)
                    .Add(false, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
                "ads", TuneCat.Privacy, "\uE789", () => L.T("Реклама, советы и автоустановка приложений", "Ads, tips and silent app installs"));
            var adId = Add(new RegSwitch(false, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1, 0) { OnWhenAbsent = () => true },
                "adid", TuneCat.Privacy, "\uE8D7", () => L.T("Рекламный идентификатор", "Advertising ID"));
            var bing = Add(new RegSwitch(false, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, 0)
            { OnText = () => L.T("только этот компьютер", "this PC only"), OffText = () => L.T("с подсказками Bing", "with Bing suggestions"), When = m => m.Build >= 19041 },
                "bing", TuneCat.Privacy, "\uE721", () => L.T("Поиск в меню «Пуск»", "Start menu search"));
            bing.NeedsReboot = true;

            // ----- system and security -----
            var fast = Add(new RegSwitch(true, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1, 0)
            {
                OnWhenAbsent = () => true, OnText = () => L.T("включён", "on"), OffText = () => L.T("выключен", "off"),
                When = m => Reg.Dword(true, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") != 0,
            }, "faststartup", TuneCat.System, "\uE7E8", () => L.T("Быстрый запуск", "Fast startup"));
            const string HvciPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";
            const string Hvci = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
            var integrity = Add(new RegSwitch(true, Hvci, "Enabled", 1, 0)
            {
                // Only where it is on now and not set by the organisation (policy, or locked by UEFI).
                When = m => Reg.Dword(true, Hvci, "Enabled") == 1 && Reg.Dword(true, Hvci, "Locked") != 1 &&
                            Reg.Get(true, HvciPolicy, "HypervisorEnforcedCodeIntegrity") == null,
            }, "hvci", TuneCat.System, "\uE72E", () => L.T("Целостность памяти", "Memory integrity"));
            integrity.NeedsReboot = true;

            // ----- updates -----
            var hours = Add(new ActiveHours(), "activehours", TuneCat.Updates, "\uE823", () => L.T("Время без перезагрузок", "Active hours"));

            // Shared by all modes: what only costs and never helps.
            Func<Machine, List<TuneStep>> common = m => new List<TuneStep>
            {
                Step(diag, "disabled", () => L.T("Постоянно собирает и отправляет данные: лишняя нагрузка на диск и сеть", "Keeps collecting and sending data: extra disk and network load")),
                Step(wap, "disabled", () => L.T("Работает только на телеметрию", "Serves telemetry only")),
                Step(maps, "manual", () => L.T("Запустится сама, если откроете «Карты»", "Starts by itself if you open Maps")),
                Step(nvTele, "disabled", () => L.T("Телеметрия драйвера NVIDIA, на работу видеокарты не влияет", "NVIDIA driver telemetry, does not affect the card")),
                Step(tasks, "0", () => L.T("Совместимость и сбор данных: периодически грузят процессор и диск на минуты", "Compatibility and data collection: now and then load the processor and disk for minutes")),
                Step(ads, "1", () => L.T("Windows не ставит рекламные приложения сама и не показывает советы", "Windows stops installing promoted apps and showing tips")),
                Step(adId, "0", () => L.T("Приложения не получают идентификатор для рекламы", "Apps get no ID for ads")),
                Step(bing, "1", () => L.T("Пуск ищет только на компьютере — быстрее и без рекламы", "Start searches this PC only — faster, no ads")),
            };

            Modes.Add(new TuneMode
            {
                Id = "games", Glyph = "\uE7FC",
                Name = () => L.T("Игры", "Games"),
                Desc = () => L.T("Максимум скорости: больше энергии и тепла", "Top speed: more power and heat"),
                StartupOff = new[] { 1, 2 }, StartupOffer = new[] { 3 },
                Steps = m =>
                {
                    // Laptops: the mains side only, the battery side is left as it is.
                    Func<string, string> pw = v => m.Laptop ? v + ",*" : v + "," + v;
                    Func<string> onMains = () => m.Laptop ? L.T(" (от сети, батарею не трогаем)", " (on mains, battery left as is)") : "";
                    var s = new List<TuneStep>
                    {
                        Step(power, "perf", () => m.OnBattery
                            ? L.T("Ноутбук сейчас от батареи: она сядет быстрее, для игр лучше подключить зарядку", "The laptop is on battery: it will drain faster, plug in for games")
                            : L.T("Частоты не сбрасываются посреди игры", "Clocks stay up mid-game")),
                        Step(boost, pw("2"), () => L.T("Процессор разгоняется сразу и до предела", "The processor boosts right away and all the way") + onMains()),
                        Step(minState, pw("100"), () => L.T("Без падения частоты между кадрами — меньше микрофризов", "No clock drops between frames — fewer micro-stutters") + onMains()),
                        Step(cores, pw("100"), () => L.T("Все ядра всегда наготове, без задержки на пробуждение", "All cores always ready, no wake-up delay") + onMains()),
                        Step(epp, pw("0"), () => L.T("Процессор выбирает скорость, а не экономию", "The processor favours speed over saving") + onMains()),
                        Step(aspm, pw("0"), () => L.T("Видеокарта и NVMe не ждут выхода линии PCIe из сна", "Graphics card and NVMe skip PCIe link wake-up delays") + onMains()),
                        Step(usb, pw("0"), () => L.T("Мышь и гарнитура не засыпают — без пропусков ввода", "Mouse and headset never doze — no missed input") + onMains()),
                        Step(gameMode, "1", () => L.T("Приоритет игре, обновления не ставятся во время игры", "Games get priority, no updates while playing")),
                        Step(record, "0", () => L.T("Постоянная запись видео в фоне отнимает кадры", "Recording video all the time costs frames")),
                        Step(hags, "1", () => L.T("Меньше задержка кадра — видеокарта это поддерживает", "Lower frame latency — the graphics card supports it")),
                        Step(windowed, "1", () => L.T("Меньше задержка в играх в окне и без рамки", "Lower latency in windowed and borderless games")),
                        Step(mouse, "0", () => L.T("Прицел движется ровно как рука", "Aim moves exactly as your hand does")),
                        Step(anim, "0", () => L.T("Окна и меню открываются мгновенно", "Windows and menus open instantly")),
                        Step(glass, "0", () => L.T("Меньше работы видеокарте в интерфейсе", "Less work for the graphics card in the interface")),
                        Step(links, "manual", () => L.T("Нужна только в сетях с доменом", "Only needed on domain networks")),
                        Opt(search, "disabled", () => m.SystemDisk == "HDD"
                                ? L.T("Индексирование нагружает жёсткий диск", "Indexing keeps the hard disk busy")
                                : L.T("Нет фонового индексирования", "No background indexing"),
                            () => L.T("поиск файлов медленнее", "file search gets slower")),
                        Step(fast, "0", () => L.T("Каждое включение — чистый запуск драйверов, меньше сбоев", "Every power-on is a clean driver start, fewer glitches")),
                        new TuneStep
                        {
                            Tweak = integrity, Value = "0", Optional = true,
                            Reason = () => L.T("Совет Microsoft для игр: +5–10 % кадров", "Microsoft's gaming tip: 5–10 % more frames"),
                            Warn = () => L.T("слабее защита от вредоносных драйверов", "weaker protection from bad drivers"),
                        },
                    };
                    if (m.AnyHdd) s.Add(Step(disk, pw("0"), () => L.T("Диск не останавливается — без подвисаний при загрузке", "The disk never stops — no hitches while loading")));
                    // SysMain also runs memory compression: keep it where memory is tight or the drive is slow.
                    if (m.SystemSsd && m.RamGb >= 15.5)
                        s.Add(Step(sysMain, "disabled", () => L.T("На SSD и ", "On an SSD with ") + Math.Round(m.RamGb) + L.T(" ГБ памяти предзагрузка почти не нужна", " GB of memory preloading is hardly needed")));
                    s.AddRange(common(m));
                    return s;
                },
            });
            Modes.Add(new TuneMode
            {
                Id = "work", Glyph = "\uE821",
                Name = () => L.T("Работа", "Work"),
                Desc = () => L.T("Баланс скорости и стабильности", "Balance of speed and stability"),
                StartupOff = new[] { 1 }, StartupOffer = new[] { 2 },
                Steps = m =>
                {
                    var s = new List<TuneStep>
                    {
                        Step(power, "balanced", () => m.Laptop
                            ? L.T("Скорость, когда нужна, и долгая работа от батареи", "Speed when needed and long battery life")
                            : L.T("Почти без потери скорости, но тише и экономнее", "Nearly as fast, but quieter and cheaper to run")),
                        Step(boost, m.Laptop ? "2,*" : "2,2", () => L.T("Быстрый отклик в тяжёлых программах", "Quick response in heavy programs")),
                        Step(record, "0", () => L.T("Фоновая запись видео в работе не нужна", "Background video recording is not needed for work")),
                        Step(fast, "0", () => L.T("Чистый запуск при каждом включении: обновления и драйверы применяются полностью", "A clean start every time: updates and drivers apply fully")),
                        Step(hours, "8-20", () => L.T("Windows не перезагрузится для обновлений посреди рабочего дня", "Windows will not restart for updates mid-workday")),
                    };
                    s.AddRange(common(m));
                    return s;
                },
            });
            Modes.Add(new TuneMode
            {
                Id = "quiet", Glyph = "\uE708",
                Name = () => L.T("Тишина", "Quiet"),
                Desc = () => L.T("Холоднее, тише, дольше от батареи", "Cooler, quieter, longer on battery"),
                StartupOff = new[] { 1, 2 }, StartupOffer = new[] { 3 },
                Steps = m =>
                {
                    var s = new List<TuneStep>
                    {
                        Step(power, "saver", () => m.Laptop ? L.T("Ноутбук дольше проработает от батареи", "The laptop lasts longer on battery") : L.T("Меньше нагрев — вентиляторы медленнее", "Less heat — fans spin slower")),
                        Step(boost, "0,0", () => L.T("Без разгона: заметно холоднее и тише, тяжёлые задачи медленнее", "No boost: noticeably cooler and quieter, heavy tasks slower")),
                        Step(epp, m.Laptop ? "50,80" : "60,60", () => L.T("Процессор выбирает экономию", "The processor favours saving")),
                        Step(aspm, "2,2", () => L.T("Линии PCIe засыпают при простое", "PCIe links sleep when idle")),
                        Step(usb, "1,1", () => L.T("Неиспользуемые USB-устройства засыпают", "Idle USB devices sleep")),
                        Step(record, "0", () => L.T("Запись видео в фоне — лишняя нагрузка", "Background video recording is extra load")),
                        Step(glass, "0", () => L.T("Меньше работы видеокарте", "Less work for the graphics card")),
                        Step(links, "manual", () => L.T("Нужна только в сетях с доменом", "Only needed on domain networks")),
                        Opt(search, "disabled", () => L.T("Нет фонового индексирования — диск и процессор отдыхают", "No background indexing — disk and processor rest"), () => L.T("поиск файлов медленнее", "file search gets slower")),
                    };
                    if (m.SystemSsd && m.RamGb >= 15.5)
                        s.Add(Step(sysMain, "disabled", () => L.T("Меньше фоновой работы диска", "Less background disk work")));
                    s.AddRange(common(m));
                    return s;
                },
            });
        }

        static TuneStep Step(Tweak t, string value, Func<string> reason)
        {
            return new TuneStep { Tweak = t, Value = value, Reason = reason };
        }

        static TuneStep Opt(Tweak t, string value, Func<string> reason, Func<string> warn)
        {
            return new TuneStep { Tweak = t, Value = value, Reason = reason, Optional = true, Warn = warn };
        }

        /// <summary>A setting by id: from the catalog, or a startup program (those are found at run time).</summary>
        public static Tweak Find(string id)
        {
            return Catalog.FirstOrDefault(t => t.Id == id) ?? (Tweak)StartupTweak.FromId(id);
        }

        public static TuneMode Mode(string id) { return Modes.FirstOrDefault(m => m.Id == id); }

        /// <summary>The mode that suits this computer best, and why (lower case, to follow the mode's name).</summary>
        public static TuneMode Recommend(Machine m, out string reason)
        {
            if (m.OnBattery)
            {
                reason = L.T("ноутбук работает от батареи, так она продержится дольше", "the laptop is on battery, this makes it last longer");
                return Mode("quiet");
            }
            if (m.GamingGpu.Length > 0)
            {
                reason = L.T("найдена игровая видеокарта ", "gaming graphics card found: ") + m.GamingGpu;
                return Mode("games");
            }
            reason = m.Laptop
                ? L.T("ноутбук без игровой видеокарты", "a laptop without a gaming graphics card")
                : L.T("игровой видеокарты нет", "no gaming graphics card");
            return Mode("work");
        }

        static string Saved(string id)
        {
            string raw;
            lock (Settings.Sync) return Settings.TuneSaved.TryGetValue(id, out raw) ? raw : null;
        }

        static int OrderKey(Tweak t)
        {
            int i = Catalog.IndexOf(t);
            return (int)t.Category * 1000 + (i < 0 ? 999 : i);
        }

        /// <summary>The mode's steps on this computer, startup programs included.</summary>
        static List<TuneStep> StepsFor(TuneMode mode, Machine m)
        {
            var steps = mode.Steps(m).Where(s =>
            {
                try { return s.Tweak.Available(m); }
                catch { return false; }
            }).ToList();
            List<StartupFound> found;
            try { found = StartupScan.Find(); }
            catch { found = new List<StartupFound>(); }
            foreach (var f in found)
            {
                bool off = mode.StartupOff.Contains(f.Rule.Level), offer = mode.StartupOffer.Contains(f.Rule.Level);
                if (!off && !offer) continue;
                steps.Add(new TuneStep { Tweak = f.Tweak, Value = "0", Reason = f.Rule.Reason, Optional = !off, Warn = f.Rule.Warn });
            }
            return steps;
        }

        /// <summary>What the mode would do on this computer, in groups. Reads the settings: call off the UI thread.</summary>
        public static List<TuneRow> Plan(TuneMode mode, Machine m)
        {
            Pc = m;
            var rows = new List<TuneRow>();
            var steps = StepsFor(mode, m);
            var ids = new HashSet<string>(steps.Select(s => s.Tweak.Id));
            List<string> saved;
            lock (Settings.Sync) saved = Settings.TuneSaved.Keys.ToList();
            var all = steps.Select(s => s.Tweak).Concat(saved.Where(id => !ids.Contains(id)).Select(Find).Where(t => t != null))
                .GroupBy(t => t.Id).Select(g => g.First()).OrderBy(OrderKey).ToList();
            foreach (var t in all)
            {
                var step = steps.FirstOrDefault(s => s.Tweak.Id == t.Id);
                string was = Saved(t.Id);
                string now;
                try { now = t.Current(); }
                catch { continue; }
                if (step != null)
                {
                    bool same = t.Matches(now, step.Value);
                    rows.Add(new TuneRow
                    {
                        Tweak = t, Category = t.Category, Kind = same ? TuneKind.Same : TuneKind.Change, Target = step.Value,
                        Now = now, To = step.Value, Reason = step.Reason, Checked = !same && !step.Optional, Warn = step.Warn,
                    });
                }
                else if (was != null)
                {
                    // Another mode changed it, this one does not need it: back to how it was.
                    string old = t.ValueOf(was);
                    rows.Add(new TuneRow
                    {
                        Tweak = t, Category = t.Category, Kind = TuneKind.Return, Target = old, Now = now, To = old,
                        Reason = () => L.T("Этому режиму не нужно — вернётся как было до DriveUP Pro", "Not needed here — goes back to how it was before DriveUP Pro"),
                    });
                }
            }
            return WithHeaders(rows);
        }

        static List<TuneRow> WithHeaders(List<TuneRow> rows)
        {
            var list = new List<TuneRow>();
            TuneCat? cat = null;
            foreach (var r in rows)
            {
                if (cat != r.Category)
                {
                    cat = r.Category;
                    list.Add(new TuneRow { Kind = TuneKind.Header, Category = r.Category, Checked = false });
                }
                list.Add(r);
            }
            return list;
        }

        /// <summary>Everything DriveUP Pro has changed and not put back yet: was → now.</summary>
        public static List<TuneRow> Changed()
        {
            var rows = new List<TuneRow>();
            Dictionary<string, string> saved, when;
            lock (Settings.Sync)
            {
                saved = new Dictionary<string, string>(Settings.TuneSaved);
                when = new Dictionary<string, string>(Settings.TuneWhen);
            }
            foreach (var kv in saved)
            {
                var t = Find(kv.Key);
                if (t == null) continue;
                var row = new TuneRow { Tweak = t, Category = t.Category, Kind = TuneKind.Changed, Was = t.ValueOf(kv.Value), Checked = false };
                try { row.Now = t.Current(); }
                catch { row.Now = ""; }
                string w;
                if (when.TryGetValue(kv.Key, out w))
                {
                    var parts = w.Split('|');
                    row.ModeId = parts[0];
                    if (parts.Length > 1) DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out row.When);
                }
                rows.Add(row);
            }
            return WithHeaders(rows.OrderBy(r => OrderKey(r.Tweak)).ToList());
        }

        public static int ChangedCount
        {
            get { lock (Settings.Sync) return Settings.TuneSaved.Count; }
        }

        /// <summary>
        /// Sets one value. The old state is saved (and written to the .ini) before anything changes,
        /// so it can always be put back; if the change fails, a freshly saved state is restored and dropped.
        /// </summary>
        public static void Change(Tweak t, string value, TuneMode mode)
        {
            string raw = Saved(t.Id);
            bool fresh = raw == null;
            string stamp = mode.Id + "|" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            if (fresh)
            {
                raw = t.Capture();
                lock (Settings.Sync)
                {
                    Settings.TuneSaved[t.Id] = raw;
                    Settings.TuneWhen[t.Id] = stamp;
                }
                Settings.Save();
            }
            try { t.Set(value); }
            catch
            {
                if (fresh)
                {
                    try { t.Restore(raw); } catch { }
                    Forget(t.Id);
                }
                throw;
            }
            lock (Settings.Sync) Settings.TuneWhen[t.Id] = stamp;
            Settings.Save();
        }

        /// <summary>Puts one setting back exactly as it was before DriveUP Pro changed it.</summary>
        public static void Return(Tweak t)
        {
            string raw = Saved(t.Id);
            if (raw == null) return;
            t.Restore(raw);
            Forget(t.Id);
        }

        static void Forget(string id)
        {
            lock (Settings.Sync)
            {
                Settings.TuneSaved.Remove(id);
                Settings.TuneWhen.Remove(id);
                if (Settings.TuneSaved.Count == 0) Settings.TuneMode = "";
            }
            Settings.Save();
        }

        public static void SetMode(string id)
        {
            lock (Settings.Sync) Settings.TuneMode = id;
            Settings.Save();
        }

        /// <summary>The order to run rows in: returns first (plan last), then changes (plan first).</summary>
        public static List<TuneRow> Order(IEnumerable<TuneRow> rows)
        {
            var list = rows.Where(r => r.Kind != TuneKind.Header).ToList();
            return list.Where(r => r.Kind == TuneKind.Return || r.Kind == TuneKind.Changed).OrderByDescending(r => OrderKey(r.Tweak))
                .Concat(list.Where(r => r.Kind == TuneKind.Change).OrderBy(r => OrderKey(r.Tweak))).ToList();
        }
    }
}
