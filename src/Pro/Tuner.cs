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
        /// <summary>Has a catch (Warn): never ticked by itself, and applied only after the user confirms it.</summary>
        public bool Risky { get { return Warn != null; } }
        /// <summary>Processes of this program running now, and their memory (0 = none or not known).</summary>
        public int Procs;
        public long ProcMemory;

        public bool Actionable { get { return Kind == TuneKind.Change || Kind == TuneKind.Return; } }
        /// <summary>Can be run (again): not started yet, or failed last time.</summary>
        public bool Ready { get { return State == TuneState.Idle || State == TuneState.Failed; } }
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
                case TuneCat.Background: return L.T("Фоновые процессы", "Background processes");
                case TuneCat.Apps: return L.T("Предустановленные приложения", "Preinstalled apps");
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

        /// <summary>A background item and how much of a nuisance it is (levels as for startup programs: 1 clutter, 2 heavy, 3 your call).</summary>
        class Extra
        {
            public Tweak Tweak;
            public string Value;
            public int Level;
            public Func<string> Reason, Warn;
            public Func<Machine, bool> When = m => true;
        }

        static readonly List<Extra> extras = new List<Extra>();

        static void Background(string family, int level, string ru, string en, Func<string> reason, Func<string> warn, params string[] processes)
        {
            var t = new BackgroundApp(family) { Name = () => L.T(ru, en), Processes = processes };
            Catalog.Add(t);
            extras.Add(new Extra { Tweak = t, Value = "0", Level = level, Reason = reason, Warn = warn });
        }

        static void ExtraService(string name, string value, int level, Func<string> title, Func<string> reason, Func<string> warn = null, Func<Machine, bool> when = null)
        {
            var s = Service(name, title);
            extras.Add(new Extra { Tweak = s, Value = value, Level = level, Reason = reason, Warn = warn, When = when ?? (m => true) });
        }

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
                When = m => !m.Managed && !m.SecureBiometrics && Reg.Dword(true, Hvci, "Enabled") == 1 && Reg.Dword(true, Hvci, "Locked") != 1 &&
                            Reg.Get(true, HvciPolicy, "HypervisorEnforcedCodeIntegrity") == null,
            }, "hvci", TuneCat.System, "\uE72E", () => L.T("Целостность памяти", "Memory integrity"));
            integrity.NeedsReboot = true;

            // ----- updates -----
            var hours = Add(new ActiveHours(), "activehours", TuneCat.Updates, "\uE823", () => L.T("Время без перезагрузок", "Active hours"));

            // ----- background processes: Windows features and Store apps that run when nobody asked -----
            var widgets = Add(new RegSwitch(true, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 1, 0)
            { OnWhenAbsent = () => true, When = m => m.Windows11, OnText = () => L.T("включены", "on"), OffText = () => L.T("выключены", "off") },
                "widgets", TuneCat.Background, "\uE8A1", () => L.T("Виджеты (погода, новости, лента)", "Widgets (weather, news, feed)"));
            widgets.NeedsReboot = true;
            widgets.Processes = new[] { "Widgets", "WidgetService" };
            extras.Add(new Extra { Tweak = widgets, Value = "0", Level = 1, Reason = () => L.T("Лента новостей держит несколько процессов браузерного движка", "The news feed keeps several browser engine processes") });
            var feeds = Add(new RegSwitch(true, @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 1, 0)
            { OnWhenAbsent = () => true, When = m => !m.Windows11, OnText = () => L.T("включены", "on"), OffText = () => L.T("выключены", "off") },
                "newsfeed", TuneCat.Background, "\uE8A1", () => L.T("Новости и интересы на панели задач", "News and interests on the taskbar"));
            feeds.NeedsReboot = true;
            extras.Add(new Extra { Tweak = feeds, Value = "0", Level = 1, Reason = () => L.T("Лента новостей на панели задач грузится в фоне", "The taskbar news feed loads in the background") });
            const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
            var edge = Add(new RegSet { When = m => System.IO.File.Exists(Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe")) || Reg.TestRoot != null }
                    .Add(true, EdgePolicy, "StartupBoostEnabled", 0).Add(true, EdgePolicy, "BackgroundModeEnabled", 0),
                "edgebg", TuneCat.Background, "\uE774", () => L.T("Предзапуск и фоновая работа Edge", "Edge startup boost and background mode"));
            extras.Add(new Extra { Tweak = edge, Value = "1", Level = 1,
                Reason = () => L.T("Не запускается заранее и закрывается вместе с окном (в настройках Edge появится «управляется организацией»)",
                                   "Does not start in advance and closes with its window (Edge settings will say \"managed by your organisation\")") });
            Background("Microsoft.BingWeather_8wekyb3d8bbwe", 1, "Погода", "Weather", () => L.T("Обновляет погоду в фоне", "Refreshes the weather in the background"), null, "Microsoft.Msn.Weather");
            Background("Microsoft.BingNews_8wekyb3d8bbwe", 1, "Новости", "News", () => L.T("Подгружает новости в фоне", "Loads news in the background"), null, "Microsoft.Msn.News");
            Background("Microsoft.Getstarted_8wekyb3d8bbwe", 1, "Советы", "Tips", () => L.T("Советы Windows в фоне не нужны", "Windows tips need not run in the background"), null, "WhatsNew.Store");
            Background("Microsoft.GetHelp_8wekyb3d8bbwe", 1, "Техническая поддержка", "Get Help", () => L.T("Откроется, когда понадобится", "Opens when you need it"), null, "GetHelp");
            Background("Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe", 1, "Центр отзывов", "Feedback Hub", () => L.T("Откроется, когда понадобится", "Opens when you need it"), null, "PilotshubApp");
            Background("Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe", 1, "Microsoft Solitaire Collection", "Microsoft Solitaire Collection", () => L.T("Игре не нужно работать в фоне", "A card game need not run in the background"), null, "Solitaire");
            Background("Microsoft.WindowsMaps_8wekyb3d8bbwe", 1, "Карты", "Maps", () => L.T("Откроется, когда понадобится", "Opens when you need it"), null, "Maps");
            Background("Microsoft.People_8wekyb3d8bbwe", 1, "Люди", "People", () => L.T("Синхронизация контактов в фоне", "Syncs contacts in the background"), null, "PeopleApp");
            Background("Microsoft.ZuneVideo_8wekyb3d8bbwe", 1, "Кино и ТВ", "Movies & TV", () => L.T("Откроется, когда понадобится", "Opens when you need it"), null, "Video.UI");
            Background("Clipchamp.Clipchamp_yxz26nhyzhsrt", 1, "Clipchamp", "Clipchamp", () => L.T("Видеоредактору не нужно работать в фоне", "A video editor need not run in the background"), null, "Clipchamp");
            Background("Microsoft.549981C3F5F10_8wekyb3d8bbwe", 1, "Cortana", "Cortana", () => L.T("Cortana закрыта Microsoft", "Microsoft retired Cortana"), null, "Cortana");
            Background("Microsoft.Windows.Photos_8wekyb3d8bbwe", 1, "Фотографии", "Photos", () => L.T("Перестанет индексировать фото в фоне; открывается как обычно", "Stops indexing photos in the background; opens as usual"), null, "Microsoft.Photos", "PhotosService");
            Background("Microsoft.YourPhone_8wekyb3d8bbwe", 3, "Связь с телефоном", "Phone Link", () => L.T("Держит связь с телефоном постоянно", "Keeps the phone connected all the time"),
                () => L.T("уведомления и звонки с телефона не придут", "phone notifications and calls stop"), "PhoneExperienceHost", "YourPhone");
            Background("microsoft.windowscommunicationsapps_8wekyb3d8bbwe", 3, "Почта и Календарь", "Mail and Calendar", () => L.T("Проверяет почту в фоне", "Checks mail in the background"),
                () => L.T("уведомлений о письмах и событиях не будет", "no mail and event notifications"), "HxTsr", "HxOutlook", "HxCalendarAppImm");
            Background("Microsoft.Todos_8wekyb3d8bbwe", 3, "Microsoft To Do", "Microsoft To Do", () => L.T("Синхронизирует задачи в фоне", "Syncs tasks in the background"),
                () => L.T("напоминания не придут", "no reminders"), "Todo");
            Background("Microsoft.GamingApp_8wekyb3d8bbwe", 3, "Приложение Xbox", "Xbox app", () => L.T("Обновляет игры Game Pass в фоне", "Updates Game Pass games in the background"),
                () => L.T("игры будут обновляться, только когда Xbox открыт", "games update only while Xbox is open"), "XboxPcApp", "XboxPcAppFT");
            ExtraService("WMPNetworkSvc", "disabled", 1, () => L.T("Сетевой доступ Windows Media Player", "Windows Media Player network sharing"),
                () => L.T("Раздача медиатеки по сети, почти никому не нужна", "Shares the media library on the network; hardly anyone needs it"));
            ExtraService("ESRV_SVC_QUEENCREEK", "disabled", 1, () => L.T("Intel: программа улучшения (телеметрия)", "Intel computing improvement (telemetry)"),
                () => L.T("Собирает данные об использовании для Intel", "Collects usage data for Intel"));
            ExtraService("SystemUsageReportSvc_QUEENCREEK", "disabled", 1, () => L.T("Intel: отчёты об использовании", "Intel system usage reports"),
                () => L.T("Собирает данные об использовании для Intel", "Collects usage data for Intel"));
            ExtraService("HpTouchpointAnalyticsService", "disabled", 1, () => L.T("HP: аналитика (телеметрия)", "HP analytics (telemetry)"),
                () => L.T("Отправляет данные об использовании в HP", "Sends usage data to HP"));
            ExtraService("AdobeARMservice", "manual", 3, () => L.T("Обновление Adobe Acrobat", "Adobe Acrobat updates"),
                () => L.T("Служба обновления работает постоянно", "The update service runs all the time"),
                () => L.T("Acrobat придётся обновлять вручную", "update Acrobat by hand"));
            ExtraService("Spooler", "disabled", 3, () => L.T("Диспетчер печати", "Print Spooler"),
                () => L.T("Нужен только для печати", "Only needed for printing"),
                () => L.T("не будет печати и «Печати в PDF»", "no printing and no Print to PDF"), m => !m.Managed);

            // ----- more settings -----
            var vrr = Add(new WindowedGames("VRROptimizeEnable") { When = m => m.Build >= 19041 },
                "vrr", TuneCat.Gaming, "\uE7F4", () => L.T("Оптимизация переменной частоты (VRR)", "Variable refresh rate optimization"));
            var capture = Add(new RegSet { OnText = () => L.T("включены", "on"), OffText = () => L.T("выключены", "off") }
                    .Add(false, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)
                    .Add(false, @"System\GameConfigStore", "GameDVR_Enabled", 0),
                "gamecapture", TuneCat.Gaming, "\uE722", () => L.T("Запись и скриншоты Game Bar", "Game Bar captures"));
            var menu = Add(new MenuDelay(), "menudelay", TuneCat.Interface, "\uE700", () => L.T("Задержка открытия меню", "Menu show delay"));
            var delay = Add(new RegSwitch(false, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, 10000)
            { OnText = () => L.T("без паузы", "no pause"), OffText = () => L.T("с паузой ~10 с", "~10 s pause") },
                "startupdelay", TuneCat.Startup, "\uE916", () => L.T("Пауза перед автозагрузкой", "Startup apps delay"));
            var throttle = Add(new RegSwitch(true, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, 0)
            { OnText = () => L.T("выключено", "off"), OffText = () => L.T("включено", "on") },
                "throttling", TuneCat.Power, "\uE945", () => L.T("Замедление фоновых программ (EcoQoS)", "Power throttling of background apps (EcoQoS)"));
            throttle.NeedsReboot = true;
            var hiber = Add(new Hibernation(), "hibernation", TuneCat.System, "\uE708", () => L.T("Гибернация (файл hiberfil.sys)", "Hibernation (hiberfil.sys)"));
            var bgApps = Add(new RegSet { OnText = () => L.T("разрешено", "allowed"), OffText = () => L.T("запрещено", "off") }
                    .Add(false, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)
                    .Add(false, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0),
                "bgapps", TuneCat.Background, "\uE71D", () => L.T("Фоновая работа всех приложений Store", "Background activity of all Store apps"));
            var activity = Add(new RegSet { OnText = () => L.T("собирается", "collected"), OffText = () => L.T("не собирается", "off") }
                    .Add(true, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0)
                    .Add(true, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
                "activity", TuneCat.Privacy, "\uE81C", () => L.T("Журнал действий", "Activity history"));
            var tailored = Add(new RegSwitch(false, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1, 0) { OnWhenAbsent = () => true },
                "tailored", TuneCat.Privacy, "\uE8D7", () => L.T("Персональные советы по диагностическим данным", "Tailored experiences"));
            var wer = Add(new RegSwitch(true, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, 0)
            { OnText = () => L.T("не собираются", "off"), OffText = () => L.T("собираются и отправляются", "collected and sent") },
                "wer", TuneCat.System, "\uE783", () => L.T("Отчёты об ошибках Windows", "Windows Error Reporting"));

            // ----- Windows Update -----
            var noReboot = Add(new RegSwitch(true, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, 0)
            { OnText = () => L.T("ждёт, пока вы выйдете", "waits until you sign out"), OffText = () => L.T("может перезагрузить сама", "may restart by itself") },
                "noautoreboot", TuneCat.Updates, "\uE777", () => L.T("Перезагрузка после обновлений", "Restart after updates"));
            var autoDrivers = Add(new RegSwitch(true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 1, 0)
            { OnWhenAbsent = () => true, OnText = () => L.T("Windows ставит сама", "Windows installs them"), OffText = () => L.T("не ставит сама", "not by itself") },
                "autodrivers", TuneCat.Updates, "\uE7F8", () => L.T("Драйверы для новых устройств из Windows Update", "Drivers for new devices from Windows Update"));
            var pause = Add(new PauseUpdates(), "pauseupdates", TuneCat.Updates, "\uE769", () => L.T("Пауза обновлений на 7 дней", "Pause updates for 7 days"));

            // ----- preinstalled apps: only by choice, each one named -----
            var apps = new List<StoreApp>();
            foreach (var a in new[]
            {
                new[] { "king.com.CandyCrushSaga_", "Candy Crush Saga" }, new[] { "king.com.CandyCrushSodaSaga_", "Candy Crush Soda Saga" },
                new[] { "king.com.BubbleWitch3Saga_", "Bubble Witch 3 Saga" }, new[] { "king.com.CandyCrushFriends_", "Candy Crush Friends" },
                new[] { "BytedancePte.Ltd.TikTok_", "TikTok" }, new[] { "Disney.37853FC22B2CE_", "Disney+" }, new[] { "AmazonVideo.PrimeVideo_", "Prime Video" },
                new[] { "Facebook.Facebook_", "Facebook" }, new[] { "Facebook.InstagramBeta_", "Instagram" }, new[] { "5A894077.McAfeeSecurity_", "McAfee Security" },
                new[] { "Clipchamp.Clipchamp_", "Clipchamp" }, new[] { "Microsoft.MicrosoftSolitaireCollection_", "Microsoft Solitaire Collection" },
                new[] { "Microsoft.BingNews_", "Microsoft News" }, new[] { "Microsoft.GetHelp_", "Get Help" }, new[] { "Microsoft.Getstarted_", "Tips" },
                new[] { "Microsoft.MicrosoftOfficeHub_", "Microsoft 365 (Office)" }, new[] { "Microsoft.WindowsFeedbackHub_", "Feedback Hub" },
                new[] { "Microsoft.ZuneVideo_", "Movies & TV" }, new[] { "Microsoft.549981C3F5F10_", "Cortana" }, new[] { "Microsoft.People_", "People" },
                new[] { "Microsoft.MixedReality.Portal_", "Mixed Reality Portal" }, new[] { "Microsoft.SkypeApp_", "Skype" },
                new[] { "Microsoft.PowerAutomateDesktop_", "Power Automate" }, new[] { "MicrosoftCorporationII.MicrosoftFamily_", "Microsoft Family" },
            })
            {
                var app = new StoreApp(a[0], a[1]);
                Catalog.Add(app);
                apps.Add(app);
            }

            // Shared by all modes: what only costs and never helps.
            Func<Machine, List<TuneStep>> common = m =>
            {
                var s = new List<TuneStep>
                {
                    Step(maps, "manual", () => L.T("Запустится сама, если откроете «Карты»", "Starts by itself if you open Maps")),
                    Step(nvTele, "disabled", () => L.T("Телеметрия драйвера NVIDIA, на работу видеокарты не влияет", "NVIDIA driver telemetry, does not affect the card")),
                    Step(ads, "1", () => L.T("Windows не ставит рекламные приложения сама и не показывает советы", "Windows stops installing promoted apps and showing tips")),
                    Step(adId, "0", () => L.T("Приложения не получают идентификатор для рекламы", "Apps get no ID for ads")),
                    Step(bing, "1", () => L.T("Пуск ищет только на компьютере — быстрее и без рекламы", "Start searches this PC only — faster, no ads")),
                    Step(activity, "1", () => L.T("Windows не собирает журнал действий в фоне", "Windows keeps no activity log in the background")),
                    Step(tailored, "0", () => L.T("Без подбора советов и рекламы по данным диагностики", "No tips and ads picked from diagnostic data")),
                    Opt(autoDrivers, "0", () => L.T("Windows не заменит сама драйвер, который вы поставили", "Windows will not replace a driver you installed"),
                        () => L.T("для нового устройства драйвер ставьте через DriveUP", "install drivers for new devices with DriveUP")),
                };
                // The same in every mode, so switching modes never brings them back.
                foreach (var app in apps)
                    s.Add(Opt(app, "0", () => L.T("Предустановлено, в фоне обновляется и присылает уведомления", "Preinstalled, updates in the background and sends notifications"),
                        () => L.T("вернуть можно только из Microsoft Store", "can only come back from the Microsoft Store")));
                if (!m.Managed)
                    s.Add(Step(wer, "1", () => L.T("После сбоев программ Windows не собирает и не отправляет отчёты — меньше нагрузки на диск", "After crashes Windows does not collect and send reports — less disk load")));
                // A managed PC (domain, Entra ID, Intune) gets its commands through the push routing service and
                // reports through telemetry: those stay as the organisation set them.
                if (!m.Managed)
                    s.AddRange(new[]
                    {
                        Step(diag, "disabled", () => L.T("Постоянно собирает и отправляет данные: лишняя нагрузка на диск и сеть", "Keeps collecting and sending data: extra disk and network load")),
                        Step(wap, "disabled", () => L.T("Нужна только компьютерам, которыми управляет организация (Intune)", "Only needed on PCs an organisation manages (Intune)")),
                        Step(tasks, "0", () => L.T("Совместимость и сбор данных: периодически грузят процессор и диск на минуты", "Compatibility and data collection: now and then load the processor and disk for minutes")),
                    });
                return s;
            };
            // Distributed link tracking: only domain networks use it.
            Func<Machine, TuneStep> linksStep = m => m.Managed ? null : Step(links, "manual", () => L.T("Нужна только в сетях с доменом", "Only needed on domain networks"));

            Modes.Add(new TuneMode
            {
                Id = "games", Glyph = "\uE7FC",
                Name = () => L.T("Игры", "Games"),
                Desc = () => L.T("Максимум скорости: больше энергии и тепла", "Top speed: more power and heat"),
                StartupOff = new[] { 1, 2 }, StartupOffer = new[] { 3 },
                Steps = m =>
                {
                    // Laptops: the mains side only, the battery side is left as it is.
                    Func<string, string> pw = v => m.Mobile ? v + ",*" : v + "," + v;
                    Func<string> onMains = () => m.Mobile ? L.T(" (от сети, батарею не трогаем)", " (on mains, battery left as is)") : "";
                    var s = new List<TuneStep>
                    {
                        m.OnBattery
                            ? Opt(power, "perf", () => L.T("Ноутбук сейчас от батареи — лучше подключить зарядку и применить снова", "The laptop is on battery — plug in and apply again"),
                                () => L.T("батарея сядет быстрее", "the battery drains faster"))
                            : Step(power, "perf", () => L.T("Частоты не сбрасываются посреди игры", "Clocks stay up mid-game")),
                        Step(boost, pw("2"), () => L.T("Процессор разгоняется сразу и до предела", "The processor boosts right away and all the way") + onMains()),
                        Step(minState, pw("100"), () => L.T("Без падения частоты между кадрами — меньше микрофризов", "No clock drops between frames — fewer micro-stutters") + onMains()),
                        Step(cores, pw("100"), () => L.T("Все ядра всегда наготове, без задержки на пробуждение", "All cores always ready, no wake-up delay") + onMains()),
                        Step(epp, pw("0"), () => L.T("Процессор выбирает скорость, а не экономию", "The processor favours speed over saving") + onMains()),
                        Step(aspm, pw("0"), () => L.T("Видеокарта и NVMe не ждут выхода линии PCIe из сна", "Graphics card and NVMe skip PCIe link wake-up delays") + onMains()),
                        Step(usb, pw("0"), () => L.T("Мышь и гарнитура не засыпают — без пропусков ввода", "Mouse and headset never doze — no missed input") + onMains()),
                        Step(gameMode, "1", () => L.T("Приоритет игре, обновления не ставятся во время игры", "Games get priority, no updates while playing")),
                        Step(record, "0", () => L.T("Постоянная запись видео в фоне отнимает кадры", "Recording video all the time costs frames")),
                        m.Laptop
                            ? Opt(hags, "1", () => L.T("Меньше задержка кадра, но на ноутбуках с двумя видеокартами бывает нестабильно", "Lower frame latency, but can be unstable on laptops with two graphics cards"),
                                () => L.T("проверьте игры после перезагрузки", "check your games after a restart"))
                            : Opt(hags, "1", () => L.T("Меньше задержка кадра — видеокарта это поддерживает", "Lower frame latency — the graphics card supports it"),
                                () => L.T("со старыми драйверами бывают сбои", "old drivers may misbehave")),
                        Step(vrr, "1", () => L.T("Плавнее в играх без своей поддержки G-Sync / FreeSync", "Smoother in games without their own G-Sync / FreeSync support")),
                        Opt(capture, "1", () => L.T("Game Bar не держит запись наготове", "Game Bar keeps no recording ready"),
                            () => L.T("не будет записи и скриншотов Game Bar", "no Game Bar clips or screenshots")),
                        Step(windowed, "1", () => L.T("Меньше задержка в играх в окне и без рамки", "Lower latency in windowed and borderless games")),
                        Step(mouse, "0", () => L.T("Прицел движется ровно как рука", "Aim moves exactly as your hand does")),
                        Step(anim, "0", () => L.T("Окна и меню открываются мгновенно", "Windows and menus open instantly")),
                        Step(menu, "100", () => L.T("Подменю открываются без паузы", "Submenus open without a pause")),
                        Step(glass, "0", () => L.T("Меньше работы видеокарте в интерфейсе", "Less work for the graphics card in the interface")),
                        linksStep(m),
                        Opt(search, "disabled", () => m.SystemDisk == "HDD"
                                ? L.T("Индексирование нагружает жёсткий диск", "Indexing keeps the hard disk busy")
                                : L.T("Нет фонового индексирования", "No background indexing"),
                            () => L.T("поиск файлов медленнее", "file search gets slower")),
                        Step(fast, "0", () => L.T("Каждое включение — чистый запуск драйверов, меньше сбоев", "Every power-on is a clean driver start, fewer glitches")),
                        new TuneStep
                        {
                            Tweak = integrity, Value = "0", Optional = true,
                            Reason = () => L.T("Совет Microsoft для игр: +5–10 % кадров", "Microsoft's gaming tip: 5–10 % more frames"),
                            Warn = () => L.T("слабее защита; некоторые античиты её требуют", "weaker protection; some anti-cheats need it"),
                        },
                    };
                    if (m.AnyHdd) s.Add(Step(disk, pw("0"), () => L.T("Диск не останавливается — без подвисаний при загрузке", "The disk never stops — no hitches while loading")));
                    // Desktops: background programs (voice chat, streaming) are not slowed down to save power.
                    if (!m.Mobile) s.Add(Step(throttle, "1", () => L.T("Фоновые программы (голосовой чат, стрим) не замедляются ради экономии", "Background programs (voice chat, streaming) are not slowed to save power")));
                    if (!m.Mobile) s.Add(HibernationStep(hiber));
                    if (m.SystemSsd) s.Add(Step(delay, "1", () => L.T("Программы автозагрузки стартуют сразу, без искусственной паузы", "Startup programs start at once, without the built-in pause")));
                    if (!m.Windows11) s.Add(Opt(bgApps, "1", () => L.T("Приложения из Store не работают в фоне", "Store apps do not run in the background"), () => L.T("их уведомления не придут", "their notifications stop")));
                    // SysMain also runs memory compression: keep it where memory is tight or the drive is slow.
                    if (m.SystemSsd && m.RamGb >= 15.5)
                        s.Add(Step(sysMain, "disabled", () => L.T("На SSD и ", "On an SSD with ") + Math.Round(m.RamGb) + L.T(" ГБ памяти предзагрузка почти не нужна", " GB of memory preloading is hardly needed")));
                    s.Add(Step(noReboot, "1", () => L.T("Обновления не перезагрузят компьютер посреди игры", "Updates will not restart the PC mid-game")));
                    s.Add(Opt(pause, "1", () => L.T("Неделю без скачивания и установки обновлений в фоне", "A week without updates downloading and installing in the background"),
                        () => L.T("обновления безопасности тоже подождут", "security updates wait too")));
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
                        Step(boost, m.Mobile ? "2,*" : "2,2", () => L.T("Быстрый отклик в тяжёлых программах", "Quick response in heavy programs")),
                        Step(record, "0", () => L.T("Фоновая запись видео в работе не нужна", "Background video recording is not needed for work")),
                        Step(fast, "0", () => L.T("Чистый запуск при каждом включении: обновления и драйверы применяются полностью", "A clean start every time: updates and drivers apply fully")),
                        Step(hours, "8-20", () => L.T("Windows не перезагрузится для обновлений посреди рабочего дня", "Windows will not restart for updates mid-workday")),
                        Step(menu, "100", () => L.T("Подменю открываются без паузы", "Submenus open without a pause")),
                        Step(noReboot, "1", () => L.T("Обновления не перезагрузят компьютер, пока вы работаете", "Updates will not restart the PC while you work")),
                    };
                    if (m.SystemSsd) s.Add(Step(delay, "1", () => L.T("Программы автозагрузки стартуют сразу", "Startup programs start at once")));
                    if (!m.Mobile) s.Add(HibernationStep(hiber));
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
                        Step(power, "saver", () => m.Mobile
                            ? L.T("Режим питания «Лучшая энергоэффективность» — как в Параметрах Windows", "\"Best power efficiency\" power mode — as in Windows Settings")
                            : L.T("Меньше нагрев — вентиляторы медленнее", "Less heat — fans spin slower")),
                        Step(boost, m.Mobile ? "0,*" : "0,0", () => L.T("Без разгона: заметно холоднее и тише, тяжёлые задачи медленнее", "No boost: noticeably cooler and quieter, heavy tasks slower") +
                            (m.Mobile ? L.T(" (от сети; от батареи решает Windows)", " (on mains; on battery Windows decides)") : "")),
                        Step(epp, m.Mobile ? "60,*" : "60,60", () => L.T("Процессор выбирает экономию", "The processor favours saving") +
                            (m.Mobile ? L.T(" (от сети)", " (on mains)") : "")),
                        Step(record, "0", () => L.T("Запись видео в фоне — лишняя нагрузка", "Background video recording is extra load")),
                        Step(glass, "0", () => L.T("Меньше работы видеокарте", "Less work for the graphics card")),
                        linksStep(m),
                        Opt(search, "disabled", () => L.T("Нет фонового индексирования — диск и процессор отдыхают", "No background indexing — disk and processor rest"), () => L.T("поиск файлов медленнее", "file search gets slower")),
                    };
                    if (m.SystemSsd && m.RamGb >= 15.5)
                        s.Add(Step(sysMain, "disabled", () => L.T("Меньше фоновой работы диска", "Less background disk work")));
                    if (!m.Windows11) s.Add(Opt(bgApps, "1", () => L.T("Приложения из Store не работают в фоне", "Store apps do not run in the background"), () => L.T("их уведомления не придут", "their notifications stop")));
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

        static TuneStep HibernationStep(Tweak hiber)
        {
            long size = Hibernation.FileSize();
            return Opt(hiber, "0", () => size > 0
                    ? L.T("Освободит ", "Frees ") + L.Size(size) + L.T(" на системном диске", " on the system drive")
                    : L.T("Не держит файл гибернации на системном диске", "No hibernation file on the system drive"),
                () => L.T("не будет гибернации и быстрого запуска", "no hibernation and no fast startup"));
        }

        /// <summary>A setting by id: from the catalog, or a startup program (those are found at run time).</summary>
        public static Tweak Find(string id)
        {
            return Catalog.FirstOrDefault(t => t.Id == id) ?? (Tweak)StartupTweak.FromId(id) ?? PackagedStartupTweak.FromId(id);
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
                if (s == null) return false;
                // Laptops: the power mode is changed for mains only, so not while on battery.
                if (s.Tweak is PowerPlan && m.Mobile && m.OnBattery) return false;
                if (s.Warn != null) s.Optional = true;
                try { return s.Tweak.Available(m); }
                catch { return false; }
            }).ToList();
            foreach (var x in extras)
            {
                bool off = mode.StartupOff.Contains(x.Level), offer = mode.StartupOffer.Contains(x.Level) || x.Level == 3;
                if (!off && !offer) continue;
                try { if (!x.When(m) || !x.Tweak.Available(m)) continue; }
                catch { continue; }
                steps.Add(new TuneStep { Tweak = x.Tweak, Value = x.Value, Reason = x.Reason, Optional = !off || x.Warn != null, Warn = x.Warn });
            }
            List<StartupFound> found;
            try { found = StartupScan.Find(); }
            catch { found = new List<StartupFound>(); }
            foreach (var f in found)
            {
                bool off = mode.StartupOff.Contains(f.Rule.Level), offer = mode.StartupOffer.Contains(f.Rule.Level);
                if (!off && !offer) continue;
                // A program found twice (Run key and Store task) is offered once per entry; both are real.
                steps.Add(new TuneStep { Tweak = f.Tweak, Value = "0", Reason = f.Rule.Reason, Optional = !off || f.Rule.Warn != null, Warn = f.Rule.Warn });
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
                        Now = now, To = step.Value, Reason = step.Reason, Checked = !same && !step.Optional && step.Warn == null, Warn = step.Warn,
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
            // When the mode switches the plan, values read now are those of the old plan: write them all.
            if (rows.Any(r => r.Tweak is PowerPlan && r.Kind == TuneKind.Change))
                foreach (var r in rows.Where(r => r.Tweak is PowerSetting && r.Kind == TuneKind.Same))
                {
                    r.Kind = TuneKind.Change;
                    r.Checked = r.Warn == null;
                }
            CountRunning(rows);
            return WithHeaders(rows);
        }

        /// <summary>Adds how many processes of each program run now and how much memory they hold.</summary>
        static void CountRunning(List<TuneRow> rows)
        {
            Dictionary<string, Tuple<int, long>> now;
            try { now = Running.Snapshot(); }
            catch { return; }
            foreach (var r in rows)
                foreach (var name in r.Tweak.Processes)
                {
                    Tuple<int, long> p;
                    if (!now.TryGetValue(name, out p)) continue;
                    r.Procs += p.Item1;
                    r.ProcMemory += p.Item2;
                }
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
            string extended = fresh ? null : t.Extend(raw);
            if (fresh || extended != null)
            {
                if (fresh) raw = t.Capture();
                else raw = extended;
                lock (Settings.Sync)
                {
                    Settings.TuneSaved[t.Id] = raw;
                    Settings.TuneWhen[t.Id] = stamp;
                }
                // Nothing changes unless the old state is safely stored.
                if (!Settings.Save())
                {
                    if (fresh) lock (Settings.Sync) { Settings.TuneSaved.Remove(t.Id); Settings.TuneWhen.Remove(t.Id); }
                    throw new System.IO.IOException(L.T("Не удалось сохранить прежнее значение — ничего не изменено", "Could not store the old value — nothing changed"));
                }
            }
            try { t.Set(value); }
            catch
            {
                if (fresh)
                {
                    // Forgotten only once really back as it was; otherwise it stays under Changed.
                    bool back = false;
                    try { t.Restore(raw); back = true; } catch { }
                    if (back) Forget(t.Id);
                }
                throw;
            }
            lock (Settings.Sync) Settings.TuneWhen[t.Id] = stamp;
            // Set back to exactly how it was before DriveUP Pro: nothing left to return.
            string now = null;
            try { now = t.Capture(); } catch { }
            if (now == raw) Forget(t.Id);
            else Settings.Save();
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

        /// <summary>
        /// Every changed setting back as it was, no window (DriveUP_Pro.exe /restore). Returns the
        /// number that failed; the result is shown in a message and written to the journal.
        /// </summary>
        public static int ReturnAll()
        {
            Pc = Machine.Read();
            int done = 0, failed = 0;
            var errors = new List<string>();
            foreach (var r in Order(Changed().Where(x => x.Kind == TuneKind.Changed)))
            {
                try { Return(r.Tweak); done++; }
                catch (Exception ex) { failed++; errors.Add(r.Tweak.Name() + ": " + ex.Message); }
            }
            Journal.Add(JournalKind.TuneReturn, done, failed, "/restore");
            string text = done + failed == 0
                ? L.T("DriveUP Pro ничего не менял — возвращать нечего.", "DriveUP Pro changed nothing — there is nothing to return.")
                : L.T("Возвращено как было: ", "Returned: ") + done + (failed > 0 ? L.T("\nНе удалось: ", "\nFailed: ") + failed + "\n" + string.Join("\n", errors) : "") +
                  L.T("\n\nПерезагрузите компьютер, чтобы всё применилось.", "\n\nRestart the computer for everything to apply.");
            System.Windows.Forms.MessageBox.Show(text, "DriveUP Pro", System.Windows.Forms.MessageBoxButtons.OK,
                failed > 0 ? System.Windows.Forms.MessageBoxIcon.Warning : System.Windows.Forms.MessageBoxIcon.Information);
            return failed;
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
