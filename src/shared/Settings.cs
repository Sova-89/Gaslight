//  Settings.cs - ВСЕ настройки Gaslight в одном месте.
//
//  Что это:  статический класс, поля = настройки. Файл settings.txt лежит рядом
//            с Gaslight.dll, строки вида  Имя=Значение.
//  Как добавить настройку:  объяви ещё одно публичное статическое поле с
//            атрибутом [Info("пояснение", Section="Раздел")] - оно само
//            попадёт в settings.txt, в меню Hollow Knight и на экранную панель (PageUp).
//  Типы полей:  string, int, float, bool.
//  Для чисел задай Min/Max/Step - по ним строится список значений в меню.
//  Advanced=true - настройка не показывается в коротком меню Hollow Knight
//            (но есть в settings.txt и на экранной панели).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Gaslight
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class InfoAttribute : Attribute
    {
        public string Text;           // пояснение по-русски
        public string En = "";        // то же по-английски (если пусто - покажется русский)
        public string Section = "";
        public string SectionEn = ""; // название раздела по-английски
        public string Choices = "";   // варианты через запятую: "light,full"
        public double Min;
        public double Max;
        public double Step;
        public bool IsKey;            // поле хранит имя клавиши из KeyNames.Allowed
        public bool Advanced;         // не показывать в коротком меню
        public InfoAttribute(string text) { Text = text; }

        // Текст и раздел на текущем языке интерфейса.
        public string TextNow { get { return (L.Ru || En.Length == 0) ? Text : En; } }
        public string SectionNow { get { return (L.Ru || SectionEn.Length == 0) ? Section : SectionEn; } }
    }

    // Одна строка меню/панели: поле + список допустимых значений.
    public sealed class SettingRow
    {
        public string Name;
        public FieldInfo Field;
        public InfoAttribute Info;
        public string[] Values;
        public string Text { get { return Info.TextNow; } }   // пояснение на текущем языке
    }

    public static class Settings
    {
        // ---- Общее ----
        [Info("Язык интерфейса: auto = как язык системы, en, ru (график, меню, лог, отчёт)", En = "Interface language: auto = follow the system language, en, ru (HUD, menus, log, report)", Section = "Общее", SectionEn = "General", Choices = "auto,en,ru")]
        public static string Language = "auto";

        // ---- Клавиши ----
        [Info("Показать / спрятать график FPS в игре", En = "Show / hide the FPS graph in game", Section = "Клавиши", SectionEn = "Keys", IsKey = true)]
        public static string KeyToggleHud = "Insert";

        [Info("Сохранить отчёт прямо сейчас", En = "Save a report right now", Section = "Клавиши", SectionEn = "Keys", IsKey = true)]
        public static string KeyReport = "Home";

        [Info("Открыть / закрыть панель настроек (в Hollow Knight есть меню Options > Mods)", En = "Open / close the on-screen settings panel (Hollow Knight also has Options > Mods)", Section = "Клавиши", SectionEn = "Keys", IsKey = true)]
        public static string KeyPanel = "PageUp";

        [Info("Переключить вид графика: компактный / подробный (крупнее, все моды и методы)", En = "Switch the graph view: compact / detailed (bigger, all mods and methods)", Section = "Клавиши", SectionEn = "Keys", IsKey = true)]
        public static string KeyHudMode = "End";

        // ---- График ----
        [Info("Показывать график сразу при запуске игры", En = "Show the graph as soon as the game starts", Section = "График", SectionEn = "Display")]
        public static bool HudVisibleAtStart = false;

        [Info("Угол экрана для графика", En = "Screen corner for the graph", Section = "График", SectionEn = "Display", Choices = "TopLeft,TopRight,BottomLeft,BottomRight")]
        public static string HudCorner = "TopLeft";

        [Info("Размер компактного графика (1 = обычный)", En = "Size of the compact graph (1 = normal)", Section = "График", SectionEn = "Display", Min = 0.75, Max = 3, Step = 0.25)]
        public static float HudScale = 1.5f;

        [Info("Размер подробного графика", En = "Size of the detailed graph", Section = "График", SectionEn = "Display", Min = 1, Max = 4, Step = 0.25)]
        public static float HudScaleBig = 2f;

        [Info("Сколько модов показывать в подробном виде", En = "How many mods to show in the detailed view", Section = "График", SectionEn = "Display", Min = 3, Max = 15, Step = 1)]
        public static int HudBigMods = 8;

        [Info("Сколько методов показывать в подробном виде", En = "How many methods to show in the detailed view", Section = "График", SectionEn = "Display", Min = 0, Max = 10, Step = 1)]
        public static int HudBigMethods = 5;

        [Info("Начинать с подробного вида", En = "Start with the detailed view", Section = "График", SectionEn = "Display")]
        public static bool HudBigAtStart = false;

        // ---- Профайлер ----
        [Info("Замерять нагрузку модов. Выкл = только FPS без хуков (нужен перезапуск)", En = "Measure mod load. Off = FPS only, no hooks (restart needed)", Section = "Профайлер", SectionEn = "Profiler")]
        public static bool ProfilerEnabled = true;

        [Info("full = замерять всё, что игра вызывает в моде: Unity-методы, корутины, события, патчи, хуки (нужен перезапуск). light = только Unity-методы и корутины", En = "full = time everything the game calls in a mod: Unity messages, coroutines, events, patches, hooks (restart needed). light = only Unity messages and coroutines", Section = "Профайлер", SectionEn = "Profiler", Choices = "full,light")]
        public static string HookScope = "full";

        [Info("Через сколько секунд после старта ставить замеры (чтобы не было фриза при загрузке)", En = "Seconds after start before the stopwatches are installed (avoids a freeze while loading)", Section = "Профайлер", SectionEn = "Profiler", Min = 0, Max = 60, Step = 1, Advanced = true)]
        public static int HookDelaySec = 5;

        [Info("Какую часть времени кадра в среднем тратить на поиск методов и установку замеров, %. Чем меньше, тем меньше просадка FPS при старте, но тем дольше ставятся замеры; ничего не пропускается", En = "What share of the frame time, on average, to spend on finding methods and installing stopwatches, %. Lower = smaller FPS dip at start-up but a longer install; nothing is skipped", Section = "Профайлер", SectionEn = "Profiler", Min = 2, Max = 50, Step = 1, Advanced = true)]
        public static float StepBudgetPercent = 15f;

        [Info("Верхний предел того же бюджета, мс на кадр (на медленных кадрах процент мог бы дать слишком много)", En = "Upper limit of the same budget, ms per frame (on slow frames the percentage alone could allow too much)", Section = "Профайлер", SectionEn = "Profiler", Min = 0.2, Max = 10, Step = 0.2, Advanced = true)]
        public static float StepBudgetMs = 1.5f;

        [Info("Не больше стольких замеров за один кадр (вместе с лимитом по времени)", En = "At most this many stopwatches per frame (together with the time budget)", Section = "Профайлер", SectionEn = "Profiler", Min = 1, Max = 100, Step = 1, Advanced = true)]
        public static int HooksPerFrame = 10;

        [Info("Не больше стольких типов за один кадр при поиске (вместе с лимитом по времени)", En = "At most this many types scanned per frame (together with the time budget)", Section = "Профайлер", SectionEn = "Profiler", Min = 5, Max = 500, Step = 5, Advanced = true)]
        public static int ScanTypesPerFrame = 100;

        // ---- Самозащита ----
        [Info("Если сам Gaslight грузит игру больше порога — он отключает замер у мелких методов", En = "If Gaslight itself loads the game above the threshold, it stops timing the tiniest methods", Section = "Самозащита", SectionEn = "Self-protection", Advanced = true)]
        public static bool SelfProtect = true;

        [Info("Порог нагрузки самого Gaslight, % времени кадра", En = "Gaslight's own load threshold, % of frame time", Section = "Самозащита", SectionEn = "Self-protection", Min = 0.5, Max = 10, Step = 0.5, Advanced = true)]
        public static float SelfBudgetPercent = 3f;

        [Info("Сколько секунд подряд порог должен быть превышен, чтобы сработала самозащита", En = "Seconds in a row the threshold must be exceeded before self-protection acts", Section = "Самозащита", SectionEn = "Self-protection", Min = 2, Max = 30, Step = 1, Advanced = true)]
        public static int SelfProtectSeconds = 5;

        // ---- Лаги ----
        [Info("Лаг = кадр длиннее этого значения (мс) ...", En = "A lag spike = a frame longer than this (ms) ...", Section = "Лаги", SectionEn = "Lag spikes", Min = 10, Max = 200, Step = 5)]
        public static int SpikeMinMs = 40;

        [Info("... и длиннее обычного кадра в столько раз", En = "... and this many times longer than a normal frame", Section = "Лаги", SectionEn = "Lag spikes", Min = 1.5, Max = 6, Step = 0.5)]
        public static float SpikeFactor = 2.5f;

        [Info("Доля кадра, начиная с которой виноват один мод (0.5 = половина кадра)", En = "Share of the frame from which a single mod is blamed (0.5 = half a frame)", Section = "Лаги", SectionEn = "Lag spikes", Min = 0.3, Max = 0.9, Step = 0.05, Advanced = true)]
        public static float BlameShare = 0.5f;

        [Info("Кадры длиннее этого (секунды) считаются паузой и не учитываются", En = "Frames longer than this (seconds) count as a pause and are ignored", Section = "Лаги", SectionEn = "Lag spikes", Min = 2, Max = 60, Step = 1, Advanced = true)]
        public static int SpikeIgnoreSec = 10;

        // ---- Отчёт ----
        [Info("Сохранять отчёт при выходе из игры", En = "Save a report when the game quits", Section = "Отчёт", SectionEn = "Report")]
        public static bool ReportOnExit = true;

        [Info("Автосохранение отчёта: раз в столько секунд (на смене сцены, 0 = выключено). Ещё сохраняется при потере фокуса окна",
              En = "Autosave the report every this many seconds (on scene changes; 0 = off). It is also saved when the game window loses focus",
              Section = "Отчёт", SectionEn = "Report", Min = 0, Max = 600, Step = 10)]
        public static int ReportAutosaveSec = 60;

        [Info("Сколько лагов максимум попадёт в отчёт", En = "Maximum number of lag spikes in the report", Section = "Отчёт", SectionEn = "Report", Min = 20, Max = 500, Step = 20, Advanced = true)]
        public static int MaxSpikesInReport = 200;

        [Info("Подробный лог (по умолчанию только важное)", En = "Verbose log (by default only the important lines)", Section = "Отчёт", SectionEn = "Report", Advanced = true)]
        public static bool VerboseLog = false;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string FilePath = "";
        public static Action<string> LogSink;
        public static event Action Changed;
        static List<SettingRow> rows;

        public static void Init(string dataDir, Action<string> log)
        {
            FilePath = Path.Combine(dataDir, "settings.txt");
            LogSink = log;
        }

        static void Log(string s) { if (LogSink != null) LogSink(s); }

        public static List<SettingRow> Rows()
        {
            if (rows == null) Build();
            return rows;
        }

        static void Build()
        {
            rows = new List<SettingRow>();
            FieldInfo[] fields = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                object[] a = fields[i].GetCustomAttributes(typeof(InfoAttribute), false);
                if (a.Length == 0) continue;
                SettingRow r = new SettingRow();
                r.Field = fields[i];
                r.Name = fields[i].Name;
                r.Info = (InfoAttribute)a[0];
                r.Values = MakeValues(r);
                rows.Add(r);
            }
        }

        static string Fmt(double v, Type t)
        {
            if (t == typeof(int)) return ((int)Math.Round(v)).ToString(Inv);
            return Math.Round(v, 2).ToString("0.##", Inv);
        }

        static string[] MakeValues(SettingRow r)
        {
            InfoAttribute i = r.Info;
            Type t = r.Field.FieldType;
            if (i.IsKey) return KeyNames.Allowed;
            if (i.Choices.Length > 0) return i.Choices.Split(',');
            if (t == typeof(bool)) return new string[] { L.T("Выкл", "Off"), L.T("Вкл", "On") };
            if (i.Max > i.Min && i.Step > 0)
            {
                List<string> l = new List<string>();
                int n = (int)Math.Round((i.Max - i.Min) / i.Step);
                for (int k = 0; k <= n; k++) l.Add(Fmt(i.Min + k * i.Step, t));
                return l.ToArray();
            }
            return new string[] { Convert.ToString(r.Field.GetValue(null), Inv) };
        }

        // Индекс текущего значения в списке r.Values (для числа - ближайшее).
        public static int GetIndex(SettingRow r)
        {
            object v = r.Field.GetValue(null);
            Type t = r.Field.FieldType;
            if (t == typeof(bool)) return ((bool)v) ? 1 : 0;
            if (t == typeof(string))
            {
                string s = (string)v;
                for (int k = 0; k < r.Values.Length; k++)
                    if (string.Equals(r.Values[k], s, StringComparison.OrdinalIgnoreCase)) return k;
                return 0;
            }
            double d = Convert.ToDouble(v, Inv);
            int best = 0; double bestDiff = double.MaxValue;
            for (int k = 0; k < r.Values.Length; k++)
            {
                double x;
                if (!double.TryParse(r.Values[k], NumberStyles.Float, Inv, out x)) continue;
                double diff = Math.Abs(x - d);
                if (diff < bestDiff) { bestDiff = diff; best = k; }
            }
            return best;
        }

        // Установить значение по индексу, сохранить файл, сообщить подписчикам.
        public static void SetIndex(SettingRow r, int idx)
        {
            if (idx < 0) idx = 0;
            if (idx >= r.Values.Length) idx = r.Values.Length - 1;
            string s = r.Values[idx];
            Type t = r.Field.FieldType;
            if (t == typeof(bool)) r.Field.SetValue(null, idx == 1);
            else if (t == typeof(string)) r.Field.SetValue(null, s);
            else if (t == typeof(int)) r.Field.SetValue(null, (int)Math.Round(double.Parse(s, Inv)));
            else if (t == typeof(float)) r.Field.SetValue(null, (float)double.Parse(s, Inv));
            if (r.Name == "Language") rows = null;   // подписи значений зависят от языка
            Save();
            if (Changed != null) Changed();
        }

        // Установить значение текстом (как в settings.txt), сохранить, сообщить подписчикам.
        public static void SetByText(SettingRow r, string text)
        {
            if (!SetFromText(r, text)) { Log(L.T("Неверное значение ", "Invalid value ") + r.Name + "=" + text); return; }
            Save();
            if (Changed != null) Changed();
        }

        public static string ValueText(SettingRow r)
        {
            return r.Values[GetIndex(r)];
        }

        // ---- файл ----
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) { Save(); return; }
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                List<SettingRow> all = Rows();
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string name = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    for (int k = 0; k < all.Count; k++)
                        if (all[k].Name == name)
                        {
                            if (!SetFromText(all[k], val)) Log(L.T("settings.txt: неверное значение ", "settings.txt: invalid value ") + name + "=" + val + L.T(", оставлено прежнее", ", the previous value is kept"));
                            break;
                        }
                }
                rows = null;   // язык мог поменяться - подписи значений (Вкл / On) соберём заново
                Save(); // дописать новые настройки, которых не было в старом файле
            }
            catch (Exception e) { Log(L.T("settings.txt: ошибка чтения: ", "settings.txt: read error: ") + e.Message); }
        }

        static bool SetFromText(SettingRow r, string text)
        {
            try
            {
                Type t = r.Field.FieldType;
                InfoAttribute i = r.Info;
                if (t == typeof(bool))
                {
                    string l = text.ToLowerInvariant();
                    r.Field.SetValue(null, l == "1" || l == "true" || l == "вкл" || l == "yes" || l == "on");
                    return true;
                }
                if (t == typeof(int) || t == typeof(float))
                {
                    double d = double.Parse(text, NumberStyles.Float, Inv);
                    if (i.Max > i.Min) d = Math.Max(i.Min, Math.Min(i.Max, d));
                    if (t == typeof(int)) r.Field.SetValue(null, (int)Math.Round(d));
                    else r.Field.SetValue(null, (float)d);
                    return true;
                }
                if (i.IsKey)
                {
                    int k = KeyNames.IndexOf(text);
                    if (k < 0) return false;
                    r.Field.SetValue(null, KeyNames.Allowed[k]);
                    return true;
                }
                if (i.Choices.Length > 0)
                {
                    for (int k = 0; k < r.Values.Length; k++)
                        if (string.Equals(r.Values[k], text, StringComparison.OrdinalIgnoreCase))
                        {
                            r.Field.SetValue(null, r.Values[k]);
                            return true;
                        }
                    return false;
                }
                r.Field.SetValue(null, text);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(L.T("# Gaslight - настройки. Меняй значение после «=», сохрани файл и перезапусти игру.", "# Gaslight settings. Change the value after '=', save the file and restart the game."));
                sb.AppendLine(L.T("# Большинство настроек можно менять и прямо в игре (меню / панель), тогда файл обновится сам.", "# Most settings can also be changed in game (menu / panel); the file then updates itself."));
                string section = null;
                List<SettingRow> all = Rows();
                for (int k = 0; k < all.Count; k++)
                {
                    SettingRow r = all[k];
                    if (r.Info.SectionNow != section)
                    {
                        section = r.Info.SectionNow;
                        sb.AppendLine();
                        sb.AppendLine("# ---- " + section + " ----");
                    }
                    sb.AppendLine("# " + r.Text);
                    object v = r.Field.GetValue(null);
                    string text;
                    if (v is bool) text = ((bool)v) ? "true" : "false";
                    else if (v is float) text = ((float)v).ToString("0.##", Inv);
                    else text = Convert.ToString(v, Inv);
                    sb.AppendLine(r.Name + "=" + text);
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { Log(L.T("settings.txt: ошибка записи: ", "settings.txt: write error: ") + e.Message); }
        }
    }
}
