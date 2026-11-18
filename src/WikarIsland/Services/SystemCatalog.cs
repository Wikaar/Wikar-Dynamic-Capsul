using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WikarIsland.Services;

/// <summary>
/// Каталог системных программ и разделов Windows для Капсулы: «открой диспетчер задач», «микшер громкости»,
/// «параметры Wi-Fi», «папка автозагрузки», «панель эмодзи», «закрой просмотр событий».
/// У каждого пункта — синонимы в любой форме слова (окончания не важны), как открыть и как найти окно, чтобы закрыть.
/// </summary>
public static class SystemCatalog
{
    /// <param name="Id">Короткий код для ИИ.</param>
    /// <param name="Name">Как Капсула называет пункт вслух.</param>
    /// <param name="Target">Что открыть: программа (с аргументами), .msc, .cpl, ms-settings:, shell:, URI или keys:сочетание.</param>
    /// <param name="Words">Как это могут назвать.</param>
    /// <param name="Process">Процесс окна — чтобы закрыть.</param>
    /// <param name="Titles">Слова из заголовка окна (ru/en/de) — когда процесс общий (mmc, проводник, параметры).</param>
    /// <param name="Admin">Нужны права администратора — Windows спросит.</param>
    /// <param name="OnVerb">Можно сказать «включи …» (экранная клавиатура, лупа).</param>
    public sealed record Entry(string Id, string Name, string Target, string[] Words, string? Process = null, string[]? Titles = null,
        bool Admin = false, bool OnVerb = false);

    private static readonly bool Win11 = Environment.OSVersion.Version.Build >= 22000;

    private static readonly string[] SettingsTitles = { "Параметры", "Settings", "Einstellungen" };
    private static readonly string[] ControlTitles = { "Панель управления", "Control Panel", "Systemsteuerung" };

    private static Entry S(string id, string name, string page, params string[] words) =>
        new(id, name, "ms-settings:" + page, words, "ApplicationFrameHost", SettingsTitles);

    private static Entry Shell(string id, string name, string folder, string[] titles, params string[] words) =>
        new(id, name, "shell:" + folder, words, "explorer", titles);

    private static Entry Keys(string id, string name, string keys, params string[] words) =>
        new(id, name, "keys:" + keys, words, OnVerb: true);

    private static Entry Mmc(string id, string name, string msc, string[] titles, bool admin, params string[] words) =>
        new(id, name, msc, words, "mmc", titles, admin);

    private static Entry Cp(string id, string name, string target, string[] titles, params string[] words) =>
        new(id, name, target, words, null, titles);

    public static readonly Entry[] All =
    {
        // ---------- утилиты ----------
        new("taskmgr", "Диспетчер задач", "taskmgr.exe", new[] { "диспетчер задач", "таскменеджер", "таск менеджер", "task manager", "диспетчер процессов" }, "Taskmgr"),
        new("mixer", "Микшер громкости", "sndvol.exe", new[] { "микшер громкости", "микшер", "микшер звука", "громкость программ", "громкость приложений", "регулятор громкости" }, "SndVol"),
        new("resmon", "Монитор ресурсов", "resmon.exe", new[] { "монитор ресурсов", "ресурсы системы" }, "perfmon", new[] { "Монитор ресурсов", "Resource Monitor", "Ressourcenmonitor" }),
        new("perfmon", "Системный монитор", "perfmon.exe", new[] { "системный монитор", "монитор производительности", "perfmon" }, "mmc", new[] { "Системный монитор", "Performance Monitor", "Leistungsüberwachung" }),
        Mmc("eventvwr", "Просмотр событий", "eventvwr.msc", new[] { "Просмотр событий", "Event Viewer", "Ereignisanzeige" }, false,
            "просмотр событий", "журнал событий", "журналы windows", "события windows", "логи windows"),
        Mmc("taskschd", "Планировщик заданий", "taskschd.msc", new[] { "Планировщик заданий", "Task Scheduler", "Aufgabenplanung" }, false,
            "планировщик заданий", "планировщик задач", "планировщик"),
        Mmc("compmgmt", "Управление компьютером", "compmgmt.msc", new[] { "Управление компьютером", "Computer Management", "Computerverwaltung" }, true,
            "управление компьютером"),
        Mmc("devmgmt", "Диспетчер устройств", "devmgmt.msc", new[] { "Диспетчер устройств", "Device Manager", "Geräte-Manager" }, false,
            "диспетчер устройств", "устройства компьютера", "драйверы"),
        Mmc("diskmgmt", "Управление дисками", "diskmgmt.msc", new[] { "Управление дисками", "Disk Management", "Datenträgerverwaltung" }, true,
            "управление дисками", "разделы диска", "разметка диска"),
        Mmc("services", "Службы", "services.msc", new[] { "Службы", "Services", "Dienste" }, false,
            "службы", "службы windows", "сервисы windows", "список служб"),
        Mmc("gpedit", "Редактор локальной групповой политики", "gpedit.msc", new[] { "групповой политики", "Group Policy", "Gruppenrichtlinien" }, true,
            "редактор групповой политики", "групповые политики", "групповая политика", "гпедит", "gpedit"),
        Mmc("secpol", "Локальная политика безопасности", "secpol.msc", new[] { "политика безопасности", "Security Policy", "Sicherheitsrichtlinie" }, true,
            "локальная политика безопасности", "политика безопасности"),
        Mmc("lusrmgr", "Локальные пользователи и группы", "lusrmgr.msc", new[] { "Локальные пользователи", "Local Users", "Lokale Benutzer" }, true,
            "локальные пользователи и группы", "локальные пользователи", "пользователи и группы"),
        Mmc("certmgr", "Сертификаты", "certmgr.msc", new[] { "certmgr", "Сертификаты", "Certificates", "Zertifikate" }, false,
            "сертификаты", "менеджер сертификатов", "диспетчер сертификатов"),
        Mmc("fsmgmt", "Общие папки", "fsmgmt.msc", new[] { "Общие папки", "Shared Folders", "Freigegebene Ordner" }, false,
            "общие папки", "общий доступ к папкам"),
        Mmc("wfmsc", "Монитор брандмауэра в режиме повышенной безопасности", "wf.msc", new[] { "повышенной безопасности", "Advanced Security", "erweiterter Sicherheit" }, true,
            "брандмауэр в режиме повышенной безопасности", "брандмауэр повышенной безопасности", "правила брандмауэра", "расширенный брандмауэр"),
        new("msinfo", "Сведения о системе", "msinfo32.exe", new[] { "сведения о системе", "информация о системе", "msinfo" }, "msinfo32"),
        new("dxdiag", "Средство диагностики DirectX", "dxdiag.exe", new[] { "dxdiag", "дх диаг", "дикс диаг", "диагностика directx", "средство диагностики directx", "директ икс диагностика" }, "dxdiag"),
        new("cleanmgr", "Очистка диска", "cleanmgr.exe", new[] { "очистка диска", "очистить диск", "утилита очистки диска" }, "cleanmgr"),
        new("dfrgui", "Оптимизация дисков", "dfrgui.exe", new[] { "оптимизация дисков", "дефрагментация", "дефрагментатор", "дефрагментация диска" }, "dfrgui"),
        new("msconfig", "Конфигурация системы", "msconfig.exe", new[] { "конфигурация системы", "мсконфиг", "msconfig", "эм эс конфиг" }, "msconfig", Admin: true),
        new("regedit", "Редактор реестра", "regedit.exe", new[] { "редактор реестра", "реестр", "регедит", "regedit" }, "regedit", Admin: true),
        new("cmd", "Командная строка", "cmd.exe", new[] { "командная строка", "консоль", "cmd", "цмд", "командная консоль" }, "cmd"),
        new("cmdadmin", "Командная строка от администратора", "runas:cmd.exe", new[] { "командная строка от администратора", "командная строка от имени администратора", "консоль администратора" }, "cmd", Admin: true),
        new("powershell", "PowerShell", "powershell.exe", new[] { "powershell", "пауэршелл", "павершелл", "повершелл", "пауэр шелл", "power shell" }, "powershell"),
        new("terminal", "Терминал Windows", "wt.exe", new[] { "терминал", "терминал windows", "windows terminal", "виндовс терминал" }, "WindowsTerminal"),
        new("notepad", "Блокнот", "notepad.exe", new[] { "блокнот", "notepad", "нотпад" }, "notepad"),
        new("calc", "Калькулятор", "calc.exe", new[] { "калькулятор", "calculator" }, "CalculatorApp", new[] { "Калькулятор", "Calculator", "Rechner" }),
        new("paint", "Paint", "mspaint.exe", new[] { "пейнт", "paint", "паинт", "рисовалка" }, "mspaint"),
        new("wordpad", "WordPad", "write.exe", new[] { "вордпад", "wordpad", "ворд пад" }, "wordpad"),
        new("snipping", "Ножницы", "SnippingTool.exe", new[] { "ножницы", "snipping tool", "инструмент ножницы" }, "SnippingTool", OnVerb: true),
        new("osk", "Экранная клавиатура", "osk.exe", new[] { "экранная клавиатура", "виртуальная клавиатура", "клавиатура на экране" }, "osk", OnVerb: true),
        new("magnify", "Экранная лупа", "magnify.exe", new[] { "экранная лупа", "лупа", "увеличительное стекло" }, "Magnify", OnVerb: true),
        new("narrator", "Экранный диктор", "narrator.exe", new[] { "экранный диктор", "диктор windows", "narrator" }, "Narrator", OnVerb: true),
        new("charmap", "Таблица символов", "charmap.exe", new[] { "таблица символов", "специальные символы", "спецсимволы" }, "charmap"),
        new("mstsc", "Подключение к удалённому рабочему столу", "mstsc.exe",
            new[] { "подключение к удаленному рабочему столу", "удаленный рабочий стол", "удаленное подключение", "rdp", "эр дэ пэ" }, "mstsc"),
        new("winver", "О Windows", "winver.exe", new[] { "winver", "версия windows", "о windows", "какая версия windows", "сборка windows" }, "winver"),
        new("mdsched", "Средство проверки памяти Windows", "mdsched.exe", new[] { "проверка памяти", "диагностика памяти", "проверка оперативной памяти", "тест оперативки", "тест памяти" }, "MdSched", Admin: true),
        new("rstrui", "Восстановление системы", "rstrui.exe", new[] { "восстановление системы", "точки восстановления", "откат системы", "откатить систему" }, "rstrui", Admin: true),
        new("protection", "Защита системы", "SystemPropertiesProtection.exe", new[] { "защита системы", "создать точку восстановления", "точка восстановления" }, "SystemPropertiesProtection", Admin: true),
        new("sysadvanced", "Дополнительные параметры системы", "SystemPropertiesAdvanced.exe", new[] { "дополнительные параметры системы", "свойства системы дополнительно" }, "SystemPropertiesAdvanced", Admin: true),
        new("performance", "Параметры быстродействия", "SystemPropertiesPerformance.exe", new[] { "параметры быстродействия", "быстродействие", "визуальные эффекты", "файл подкачки" }, "SystemPropertiesPerformance", Admin: true),
        new("envvars", "Переменные среды", "rundll32.exe sysdm.cpl,EditEnvironmentVariables", new[] { "переменные среды", "переменные окружения", "path windows" }, "rundll32"),
        new("sysprops", "Свойства системы", "sysdm.cpl", new[] { "свойства системы", "имя компьютера", "переименовать компьютер" }, "rundll32"),
        new("msra", "Удалённый помощник Windows", "msra.exe", new[] { "удаленный помощник", "удаленная помощь" }, "msra"),
        new("quickassist", "Быстрая помощь", "ms-quick-assist:", new[] { "быстрая помощь", "quick assist" }),
        new("psr", "Средство записи действий", "psr.exe", new[] { "запись действий", "средство записи действий", "записать шаги" }, "psr"),
        new("explorer", "Проводник", "explorer.exe", new[] { "проводник", "explorer", "эксплорер" }, "explorer"),
        new("netplwiz", "Учётные записи пользователей", "netplwiz.exe", new[] { "учетные записи пользователей", "netplwiz", "автовход", "пользователи компьютера" }, "netplwiz", Admin: true),
        new("iscsi", "Инициатор iSCSI", "iscsicpl.exe", new[] { "iscsi", "инициатор iscsi" }, "iscsicpl", Admin: true),
        new("odbc", "Источники данных ODBC", "odbcad32.exe", new[] { "odbc", "источники данных odbc" }, "odbcad32", Admin: true),
        new("colorcpl", "Управление цветом", "colorcpl.exe", new[] { "управление цветом", "цветовые профили" }, "colorcpl"),
        new("dpiscaling", "Калибровка цветов экрана", "dccw.exe", new[] { "калибровка цветов", "калибровка экрана", "калибровка монитора" }, "dccw"),
        new("cleartype", "Настройка ClearType", "cttune.exe", new[] { "cleartype", "клир тайп", "сглаживание шрифтов" }, "cttune"),
        new("recorder", "Звукозапись", "ms-callrecording:", new[] { "звукозапись", "диктофон", "запись голоса" }),

        // ---------- панель управления ----------
        Cp("control", "Панель управления", "control.exe", ControlTitles, "панель управления", "control panel", "классическая панель управления"),
        Cp("appwiz", "Программы и компоненты", "appwiz.cpl", new[] { "Программы и компоненты", "Programs and Features", "Programme und Features" },
            "программы и компоненты", "удаление программ", "установка и удаление программ", "удалить программу", "удаление программы"),
        Cp("winfeatures", "Компоненты Windows", "optionalfeatures.exe", new[] { "Компоненты Windows", "Windows Features", "Windows-Features" },
            "компоненты windows", "включение компонентов windows", "включение и отключение компонентов"),
        Cp("ncpa", "Сетевые подключения", "ncpa.cpl", new[] { "Сетевые подключения", "Network Connections", "Netzwerkverbindungen" },
            "сетевые подключения", "сетевые адаптеры", "адаптеры", "настройки адаптера", "параметры адаптера"),
        Cp("netcenter", "Центр управления сетями и общим доступом", "control.exe /name Microsoft.NetworkAndSharingCenter",
            new[] { "Центр управления сетями", "Network and Sharing", "Netzwerk- und Freigabecenter" },
            "центр управления сетями", "центр управления сетями и общим доступом", "сети и общий доступ"),
        Cp("powercfg", "Электропитание", "powercfg.cpl", new[] { "Электропитание", "Power Options", "Energieoptionen" },
            "электропитание", "схема питания", "план электропитания", "схемы электропитания"),
        Cp("mousecpl", "Свойства мыши", "main.cpl", new[] { "Свойства: Мышь", "Mouse Properties", "Eigenschaften von Maus" },
            "свойства мыши", "чувствительность мыши", "скорость указателя", "курсор мыши"),
        Cp("soundcpl", "Звук: устройства воспроизведения", "mmsys.cpl", new[] { "Звук", "Sound" },
            "устройства воспроизведения", "панель звука", "старые настройки звука", "звуковые устройства", "свойства звука", "колонки и наушники"),
        Cp("recordcpl", "Звук: устройства записи", "rundll32.exe shell32.dll,Control_RunDLL mmsys.cpl,,1", new[] { "Звук", "Sound" },
            "устройства записи", "свойства микрофона", "настройки микрофона"),
        Cp("firewall", "Брандмауэр Защитника Windows", "firewall.cpl", new[] { "Брандмауэр", "Firewall" },
            "брандмауэр", "брандмауэр windows", "фаервол", "файрвол", "firewall"),
        Cp("inetcpl", "Свойства браузера", "inetcpl.cpl", new[] { "Свойства: Интернет", "Свойства браузера", "Internet Properties", "Internetoptionen" },
            "свойства браузера", "свойства интернета", "свойства обозревателя", "настройки интернета"),
        Cp("joy", "Игровые устройства", "joy.cpl", new[] { "Игровые устройства", "Game Controllers", "Gamecontroller" },
            "игровые устройства", "игровые контроллеры", "геймпад", "геймпады", "джойстик", "калибровка геймпада"),
        Cp("intl", "Региональные стандарты", "intl.cpl", new[] { "Регион", "Region" },
            "региональные стандарты", "региональные настройки", "формат даты", "формат чисел"),
        Cp("credman", "Диспетчер учётных данных", "control.exe /name Microsoft.CredentialManager", new[] { "Диспетчер учетных данных", "Credential Manager", "Anmeldeinformationsverwaltung" },
            "диспетчер учетных данных", "сохраненные пароли windows", "учетные данные windows"),
        Cp("devprinters", "Устройства и принтеры", "control.exe printers", new[] { "Устройства и принтеры", "Devices and Printers", "Geräte und Drucker" },
            "устройства и принтеры"),
        Cp("folderopts", "Параметры папок", "control.exe folders", new[] { "Параметры папок", "Параметры проводника", "Folder Options", "File Explorer Options", "Ordneroptionen" },
            "параметры папок", "параметры проводника", "показать скрытые файлы", "скрытые файлы", "расширения файлов"),
        Cp("admintools", Win11 ? "Инструменты Windows" : "Администрирование", "control.exe /name Microsoft.AdministrativeTools",
            new[] { "Администрирование", "Инструменты Windows", "Administrative Tools", "Windows Tools", "Windows-Tools", "Verwaltung" },
            "администрирование", "инструменты windows", "средства администрирования"),
        Cp("bitlocker", "Шифрование BitLocker", "control.exe /name Microsoft.BitLockerDriveEncryption", new[] { "BitLocker" },
            "bitlocker", "битлокер", "шифрование диска"),
        Cp("recoverycpl", "Восстановление", "control.exe /name Microsoft.Recovery", new[] { "Восстановление", "Recovery", "Wiederherstellung" },
            "диск восстановления", "создать диск восстановления"),
        Cp("indexing", "Параметры индексирования", "control.exe /name Microsoft.IndexingOptions", new[] { "Параметры индексирования", "Indexing Options", "Indizierungsoptionen" },
            "параметры индексирования", "индексирование", "индекс поиска"),
        Cp("fonts", "Шрифты", "control.exe fonts", new[] { "Шрифты", "Fonts", "Schriftarten" },
            "шрифты", "папка шрифтов", "установленные шрифты"),
        Cp("syncenter", "Центр синхронизации", "control.exe /name Microsoft.SyncCenter", new[] { "Центр синхронизации", "Sync Center", "Synchronisierungscenter" },
            "центр синхронизации"),
        Cp("autoplaycpl", "Автозапуск", "control.exe /name Microsoft.AutoPlay", new[] { "Автозапуск", "AutoPlay", "Automatische Wiedergabe" },
            "автозапуск дисков", "автозапуск флешек", "автозапуск носителей"),

        // ---------- параметры Windows ----------
        S("settings", "Параметры Windows", "", "параметры", "параметры windows", "настройки windows", "настройки компьютера", "настройки системы", "windows settings"),
        S("display", "Параметры экрана", "display", "параметры экрана", "настройки экрана", "разрешение экрана", "экран", "дисплей", "масштаб", "масштабирование", "яркость"),
        S("displayadv", "Дополнительные параметры дисплея", "display-advanced", "дополнительные параметры дисплея", "частота обновления", "герцовка", "частота экрана"),
        S("graphics", "Настройки графики", "display-advancedgraphics", "настройки графики", "параметры графики", "планирование графического процессора", "графический процессор для приложений"),
        S("nightlight", "Ночной свет", "nightlight", "ночной свет", "ночной режим экрана", "фильтр синего света"),
        S("sound", "Параметры звука", "sound", "параметры звука", "настройки звука", "звук", "звуки windows"),
        S("appsvolume", "Громкость приложений и параметры устройств", "apps-volume", "параметры громкости приложений", "громкость и устройства приложений", "вывод звука для приложений"),
        S("notifications", "Уведомления", "notifications", "параметры уведомлений", "настройки уведомлений"),
        S("focus", "Фокусировка внимания", "quiethours", "фокусировка внимания", "помощник по фокусировке", "не беспокоить windows"),
        S("power", "Питание и спящий режим", "powersleep", "питание и спящий режим", "настройки питания", "параметры питания", "настройки сна", "спящий режим настройки"),
        S("battery", "Экономия заряда", "batterysaver", "экономия заряда", "батарея", "аккумулятор"),
        S("storage", "Память устройства", "storagesense", "память устройства", "хранилище", "контроль памяти", "место на диске", "свободное место"),
        S("multitasking", "Многозадачность", "multitasking", "многозадачность", "прикрепление окон"),
        S("clipboardset", "Параметры буфера обмена", "clipboard", "параметры буфера обмена", "настройки буфера обмена"),
        S("rdpset", "Удалённый рабочий стол (параметры)", "remotedesktop", "настройки удаленного рабочего стола", "разрешить удаленный рабочий стол"),
        S("about", "О системе", "about", "о системе", "характеристики компьютера", "о компьютере", "сведения об устройстве", "характеристики устройства"),
        S("bluetooth", "Bluetooth и устройства", "bluetooth", "блютуз", "bluetooth", "блютус", "настройки блютуз", "параметры блютуз", "bluetooth и устройства", "устройства bluetooth"),
        S("printers", "Принтеры и сканеры", "printers", "принтеры", "принтеры и сканеры", "сканеры", "принтер"),
        S("mouse", "Параметры мыши", "mousetouchpad", "параметры мыши", "настройки мыши", "мышь", "тачпад", "сенсорная панель"),
        S("typing", "Ввод", "typing", "ввод текста", "настройки клавиатуры", "параметры клавиатуры", "автоисправление"),
        S("usb", "USB", "usb", "параметры usb", "настройки usb"),
        S("network", "Сеть и интернет", "network-status", "параметры сети", "настройки сети", "сеть и интернет", "состояние сети", "настройки интернета windows", "сеть"),
        S("wifi", "Wi-Fi", "network-wifi", "wi-fi", "wifi", "вайфай", "вай фай", "беспроводная сеть", "настройки вайфай", "параметры wi-fi"),
        S("ethernet", "Ethernet", "network-ethernet", "ethernet", "езернет", "проводная сеть"),
        S("vpnwin", "VPN (параметры Windows)", "network-vpn", "vpn windows", "параметры vpn", "впн windows", "впн виндовс", "настройки vpn windows"),
        S("proxy", "Прокси", "network-proxy", "прокси", "прокси сервер", "настройки прокси"),
        S("airplane", "Режим «в самолёте»", "network-airplanemode", "режим в самолете", "авиарежим", "режим полета"),
        S("hotspot", "Мобильный хот-спот", "network-mobilehotspot", "мобильный хотспот", "мобильный хот спот", "точка доступа", "раздать интернет", "раздача wi-fi"),
        S("dataUsage", "Использование данных", "datausage", "использование данных", "расход трафика", "трафик"),
        S("background", "Фон рабочего стола", "personalization-background", "фон", "обои", "фон рабочего стола", "обои рабочего стола", "сменить обои"),
        S("colors", "Цвета Windows", "colors", "цвета", "цвета windows", "темная тема windows", "светлая тема windows", "цвет акцента"),
        S("themes", "Темы", "themes", "темы", "темы windows", "персонализация"),
        S("lockscreen", "Экран блокировки", "lockscreen", "экран блокировки", "заставка блокировки"),
        S("startset", "Параметры меню «Пуск»", "personalization-start", "параметры пуска", "настройки пуска", "параметры меню пуск", "настройки меню пуск"),
        S("taskbarset", "Параметры панели задач", "taskbar", "параметры панели задач", "настройки панели задач", "панель задач"),
        S("apps", Win11 ? "Установленные приложения" : "Приложения и возможности", Win11 ? "appsfeatures-app" : "appsfeatures",
            "приложения и возможности", "установленные приложения", "приложения", "список программ"),
        S("defaultapps", "Приложения по умолчанию", "defaultapps", "приложения по умолчанию", "программы по умолчанию", "браузер по умолчанию", "плеер по умолчанию"),
        S("startup", "Автозагрузка", "startupapps", "автозагрузка", "автозапуск программ", "программы в автозагрузке", "автозагрузка программ"),
        S("optional", "Дополнительные компоненты", "optionalfeatures", "дополнительные компоненты", "дополнительные возможности"),
        S("video", "Воспроизведение видео", "videoplayback", "воспроизведение видео", "настройки видео", "hdr видео"),
        S("accounts", "Учётные записи", "yourinfo", "учетные записи", "аккаунт microsoft", "мой аккаунт windows", "ваши данные"),
        S("signin", "Варианты входа", "signinoptions", "варианты входа", "пин код", "пароль windows", "windows hello", "пароль при входе"),
        S("datetime", "Дата и время", "dateandtime", "дата и время", "время и дата", "часовой пояс", "настройки времени", "настройки даты", "синхронизация времени"),
        S("language", "Язык", "regionlanguage", "язык", "языки", "язык интерфейса", "раскладка", "раскладки", "добавить язык", "язык windows"),
        S("region", "Регион", "regionformatting", "регион", "регион и формат"),
        S("speech", "Речь", "speech", "распознавание речи", "речь", "голосовой доступ"),
        S("gamebarset", "Параметры Xbox Game Bar", "gaming-gamebar", "параметры game bar", "настройки game bar", "настройки гейм бар", "параметры гейм бар"),
        S("gamemode", "Игровой режим", "gaming-gamemode", "игровой режим", "режим игры"),
        S("captures", "Клипы", "gaming-gamedvr", "клипы", "запись игр", "захват игр"),
        S("access", "Специальные возможности", "easeofaccess", "специальные возможности", "доступность"),
        S("privacy", "Конфиденциальность", "privacy", "конфиденциальность", "приватность", "конфиденциальность и защита"),
        S("micaccess", "Доступ к микрофону", "privacy-microphone", "доступ к микрофону", "разрешения микрофона", "разрешение на микрофон"),
        S("camaccess", "Доступ к камере", "privacy-webcam", "доступ к камере", "разрешения камеры", "разрешение на камеру", "настройки камеры"),
        S("location", "Расположение", "privacy-location", "местоположение", "геолокация", "расположение"),
        S("update", "Центр обновления Windows", "windowsupdate", "центр обновления", "центр обновления windows", "обновления windows", "обновление windows", "обновления", "обновить windows"),
        S("updatehistory", "Журнал обновлений", "windowsupdate-history", "журнал обновлений", "история обновлений", "установленные обновления"),
        S("updateopts", "Дополнительные параметры обновлений", "windowsupdate-options", "дополнительные параметры обновлений", "параметры обновлений", "настройки обновлений"),
        S("delivery", "Оптимизация доставки", "delivery-optimization", "оптимизация доставки"),
        S("backup", "Резервное копирование", "backup", "резервное копирование", "архивация", "резервная копия windows"),
        S("troubleshoot", "Устранение неполадок", "troubleshoot", "устранение неполадок", "исправление проблем", "диагностика неполадок"),
        S("recovery", "Восстановление (параметры)", "recovery", "восстановление", "вернуть компьютер в исходное состояние", "сброс windows", "переустановка windows", "особые варианты загрузки"),
        S("activation", "Активация", "activation", "активация", "активация windows", "ключ windows"),
        S("findmy", "Поиск устройства", "findmydevice", "поиск устройства", "найти устройство"),
        S("developers", "Для разработчиков", "developers", "для разработчиков", "режим разработчика"),
        S("phone", "Ваш телефон", "mobile-devices", "связь с телефоном", "мобильные устройства", "телефон"),
        new("security", "Безопасность Windows", "windowsdefender:", new[] { "безопасность windows", "защитник", "защитник windows", "антивирус", "defender", "windows defender", "защита от вирусов" },
            "SecHealthUI", new[] { "Безопасность Windows", "Windows Security", "Windows-Sicherheit" }),

        // ---------- папки ----------
        Shell("thispc", "Этот компьютер", "MyComputerFolder", new[] { "Этот компьютер", "This PC", "Dieser PC" }, "этот компьютер", "мой компьютер", "компьютер"),
        Shell("startupfolder", "Папка автозагрузки", "Startup", new[] { "Автозагрузка", "Startup", "Autostart" }, "папка автозагрузки", "папку автозагрузки", "папка автозапуска"),
        Shell("appdata", "AppData", "AppData", new[] { "Roaming" }, "appdata", "аппдата", "апп дата", "папка appdata", "роуминг"),
        Shell("localappdata", "AppData\\Local", "Local AppData", new[] { "Local" }, "локальная appdata", "appdata local", "аппдата локал", "local appdata"),
        new("temp", "Временные файлы (Temp)", "%TEMP%", new[] { "temp", "папка temp", "папка темп", "временные файлы", "папка временных файлов" }, "explorer", new[] { "Temp" }),
        Shell("programfiles", "Program Files", "ProgramFiles", new[] { "Program Files" }, "program files", "программ файлс", "программные файлы", "папка программ"),
        Shell("windowsdir", "Папка Windows", "Windows", new[] { "Windows" }, "папка windows", "папку windows"),
        Shell("system32", "System32", "System", new[] { "System32" }, "system32", "систем 32", "систем32"),
        Shell("recent", "Недавние файлы", "Recent", new[] { "Recent", "Недавние" }, "недавние файлы", "последние файлы", "недавние документы"),
        Shell("network", "Сеть (сетевое окружение)", "NetworkPlacesFolder", new[] { "Сеть", "Network", "Netzwerk" }, "сетевое окружение", "компьютеры в сети"),
        Shell("startmenu", "Папка меню «Пуск»", "Programs", new[] { "Programs", "Программы" }, "папка меню пуск", "ярлыки меню пуск"),
        Shell("sendto", "Папка «Отправить»", "SendTo", new[] { "SendTo" }, "папка отправить", "меню отправить"),
        Shell("screenshots", "Снимки экрана", "Screenshots", new[] { "Снимки экрана", "Screenshots", "Bildschirmfotos" }, "папка снимков экрана", "снимки экрана windows"),

        // ---------- панели Windows (клавишами) ----------
        Keys("clipboardhistory", "История буфера обмена Windows", "win+v", "история буфера обмена", "историю буфера обмена", "журнал буфера обмена", "буфер обмена windows"),
        Keys("emoji", "Панель эмодзи", "win+period", "эмодзи", "смайлики", "смайлы", "панель эмодзи", "эмоджи"),
        Keys("notifycenter", "Центр уведомлений", Win11 ? "win+n" : "win+a", "центр уведомлений", "уведомления windows", "все уведомления"),
        Keys("quicksettings", Win11 ? "Быстрые настройки" : "Центр действий", "win+a", "быстрые настройки", "центр действий", "быстрые действия"),
        Keys("gamebar", "Xbox Game Bar", "win+g", "гейм бар", "game bar", "игровая панель", "xbox game bar", "иксбокс гейм бар", "геймбар"),
        Keys("taskview", "Представление задач", "win+tab", "представление задач", "все окна", "виртуальные рабочие столы", "временная шкала"),
        Keys("newdesktop", "Новый рабочий стол", "win+ctrl+d", "новый рабочий стол", "новый виртуальный рабочий стол", "еще один рабочий стол"),
        Keys("closedesktop", "Закрыть рабочий стол", "win+ctrl+f4", "закрыть рабочий стол"),
        Keys("winx", "Меню опытного пользователя", "win+x", "меню опытного пользователя", "меню win x", "меню вин икс"),
        Keys("search", "Поиск Windows", "win+s", "поиск windows", "поиск в windows", "поиск на компьютере"),
        Keys("run", "Выполнить", "win+r", "выполнить", "окно выполнить", "диалог выполнить"),
        Keys("start", "Меню «Пуск»", "win", "пуск", "меню пуск"),
        Keys("project", "Проецировать", "win+p", "проецировать", "второй экран", "режим проецирования", "дублировать экран", "расширить экран"),
        Keys("cast", "Беспроводной дисплей", "win+k", "беспроводной дисплей", "трансляция экрана", "подключиться к телевизору", "трансляция на телевизор", "миракаст"),
        Keys("dictation", "Голосовой ввод", "win+h", "голосовой ввод", "диктовка", "голосовой набор"),

        // ---------- приложения Windows ----------
        new("store", "Microsoft Store", "ms-windows-store:", new[] { "магазин", "microsoft store", "магазин приложений", "майкрософт стор", "магазин windows" }, "ApplicationFrameHost", new[] { "Microsoft Store" }),
        new("storeupdates", "Обновления приложений Microsoft Store", "ms-windows-store://downloadsandupdates", new[] { "обновления приложений", "обновить приложения из магазина", "загрузки и обновления" }),
        new("photos", "Фотографии", "ms-photos:", new[] { "фотографии windows", "приложение фотографии", "просмотр фотографий" }, "Microsoft.Photos", new[] { "Фотографии", "Photos", "Fotos" }),
        new("camera", "Камера", "microsoft.windows.camera:", new[] { "камера", "приложение камера", "камеру", "веб камера" }, "WindowsCamera", new[] { "Камера", "Camera", "Kamera" }, OnVerb: true),
        new("clock", Win11 ? "Часы" : "Будильники и часы", "ms-clock:", new[] { "будильник", "будильники", "будильники и часы", "часы windows", "секундомер" }, "Time", new[] { "Часы", "Будильники", "Clock", "Uhr" }),
        new("mediaplayer", "Медиаплеер", "mswindowsmusic:", new[] { "медиаплеер", "проигрыватель windows", "media player", "грув" }, "Microsoft.Media.Player", new[] { "Медиаплеер", "Media Player", "Groove" }),
        new("feedback", "Центр отзывов", "feedback-hub:", new[] { "центр отзывов", "feedback hub", "отзыв в microsoft" }),
        new("gethelp", "Техническая поддержка", "ms-contact-support:", new[] { "получить помощь", "техподдержка microsoft", "техническая поддержка windows", "помощь windows" }),
        new("xbox", "Xbox", "xbox:", new[] { "приложение xbox", "иксбокс", "xbox app" }),
    };

    // ---------- поиск по словам ----------

    private static List<(Entry entry, Regex regex)>? _patterns;

    private static List<(Entry, Regex)> Patterns => _patterns ??= All
        .SelectMany(e => e.Words.Select(w => (e, new Regex("^" + Pattern(Normalize(w)) + "$", RegexOptions.CultureInvariant))))
        .ToList();

    /// <summary>Слово без окончания: «диспетчер задач» найдёт и «диспетчера задач».</summary>
    private static string Pattern(string phrase) =>
        string.Join(@"\s+", phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            if (!Regex.IsMatch(w, @"^[а-я]+$")) return Regex.Escape(w).Replace("\\-", "[\\s\\-]?") + @"\w*";
            int keep = w.Length >= 7 ? w.Length - 2 : w.Length >= 4 ? w.Length - 1 : w.Length;
            return Regex.Escape(w[..keep]) + (keep < w.Length ? @"\w{0,4}" : ""); // окончание, но не другое слово («звук» ≠ «звукозапись»)
        }));

    public static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Replace('ё', 'е');
        s = Regex.Replace(s, @"[«»""'!?,.;:()]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Пункт каталога по фразе («диспетчера задач», «окно микшера громкости») или null.</summary>
    public static Entry? Find(string phrase)
    {
        string p = Normalize(phrase);
        p = Regex.Replace(p, @"^(?:мне\s+)?(?:окно|программу|программа|утилиту|утилита|приложение|раздел|вкладку|страницу|системн\w+\s+(?:программу|утилиту))\s+(?=\S)", "");
        if (p.Length < 2) return null;
        foreach ((Entry e, Regex r) in Patterns)
            if (r.IsMatch(p)) return e;
        // небольшие ошибки распознавания («диспечер задач»), но не другое слово целиком («ворд» ≠ «вордпад»)
        Entry? best = null;
        double bestScore = 0;
        string fp = AppIndex.Fold(p);
        if (fp.Length < 7) return null;
        foreach (Entry e in All)
        {
            foreach (string w in e.Words)
            {
                string fw = AppIndex.Fold(w);
                if (fw.Length < 7 || Math.Abs(fw.Length - fp.Length) > Math.Max(2, fp.Length / 8)) continue;
                double s = AppIndex.Similarity(p, w);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = e;
                }
            }
        }
        return bestScore >= 0.86 ? best : null;
    }

    public static Entry? ById(string id) => All.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ---------- открыть ----------

    /// <summary>Есть ли программа на этом компьютере (gpedit нет в Windows Home, WordPad убрали в новых сборках Windows 11).</summary>
    public static bool Exists(Entry e)
    {
        string target = e.Target.StartsWith("runas:", StringComparison.Ordinal) ? e.Target[6..] : e.Target;
        if (target.Contains(':') || target.StartsWith('%')) return true; // ms-settings:, shell:, keys:, URI, %TEMP%
        string file = target.Split(' ')[0];
        if (!Regex.IsMatch(file, @"\.(exe|msc|cpl)$", RegexOptions.IgnoreCase)) return true;
        var dirs = new List<string>
        {
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (string d in dirs)
        {
            try
            {
                if (File.Exists(Path.Combine(d.Trim(), file))) return true;
            }
            catch
            {
                // кривой путь в PATH
            }
        }
        return false;
    }

    /// <summary>Открыть. Возвращает, что сказать (пусто — молча), или причину, почему нельзя.</summary>
    public static string Open(Entry e)
    {
        if (!Exists(e))
            return e.Id is "gpedit" or "secpol" or "lusrmgr"
                ? $"{e.Name} есть только в Windows Pro и выше, сэр, а здесь домашняя версия."
                : $"{e.Name} на этом компьютере нет, сэр.";
        try
        {
            string target = e.Target;
            if (target.StartsWith("keys:", StringComparison.Ordinal))
            {
                AppAutomation.Press(target[5..]);
                return "";
            }
            bool runAs = target.StartsWith("runas:", StringComparison.Ordinal);
            if (runAs) target = target[6..];
            if (target.StartsWith("shell:", StringComparison.Ordinal))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                return "";
            }
            if (target.StartsWith('%'))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", Environment.ExpandEnvironmentVariables(target)) { UseShellExecute = true });
                return "";
            }
            int space = target.IndexOf(' ');
            bool isUri = Regex.IsMatch(target, @"^[a-z][a-z0-9.\-]*:", RegexOptions.IgnoreCase) && !target.Contains('\\');
            var psi = space > 0 && !isUri
                ? new ProcessStartInfo(target[..space], target[(space + 1)..]) { UseShellExecute = true }
                : new ProcessStartInfo(target) { UseShellExecute = true };
            if (runAs) psi.Verb = "runas";
            Process.Start(psi);
            return e.Admin && !SystemMonitorService.IsAdmin ? $"Открываю {e.Name}. Подтвердите запрос Windows, сэр." : "";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Запрос администратора отклонён, сэр.";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return $"Не получилось открыть {e.Name}: {ex.Message}";
        }
    }

    // ---------- закрыть ----------

    /// <summary>Окна этого пункта, если открыты.</summary>
    public static List<OpenWindow> Windows(Entry e)
    {
        if (e.Process == null && e.Titles == null) return new List<OpenWindow>();
        List<OpenWindow> all = WindowControl.All();
        IEnumerable<OpenWindow> found = all;
        if (e.Process != null) found = found.Where(w => w.ProcessName.Equals(e.Process, StringComparison.OrdinalIgnoreCase));
        // у общих процессов (mmc, проводник, параметры, rundll32) — только окна с нужным заголовком
        bool shared = e.Process == null || e.Process is "mmc" or "explorer" or "ApplicationFrameHost" or "rundll32" or "perfmon";
        if (e.Titles != null && shared)
        {
            List<OpenWindow> byTitle = found.Where(w => e.Titles.Any(t => w.Title.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (byTitle.Count > 0 || e.Process != "mmc") return byTitle;
            // заголовок консоли на другом языке: если открыта одна консоль — это она
            List<OpenWindow> consoles = found.ToList();
            return consoles.Count == 1 ? consoles : new List<OpenWindow>();
        }
        if (e.Process == "explorer" && e.Id == "explorer") return found.ToList(); // «закрой проводник» — все папки
        return shared && e.Titles == null && e.Process != "rundll32" ? new List<OpenWindow>() : found.ToList();
    }

    public static string Close(Entry e)
    {
        List<OpenWindow> w = Windows(e);
        if (w.Count == 0) return $"{e.Name} не открыт, сэр.";
        WindowControl.Close(w, force: false);
        return "";
    }

    /// <summary>Список для ИИ, если он не знает, как назвать.</summary>
    public static string Describe() => string.Join("; ", All.Select(e => e.Name).Distinct());
}
