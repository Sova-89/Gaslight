//  App.cs - склейка с Unity: Driver (объект в сцене), клавиши, HUD, таймеры.
//  Всё, что зависит от игры, вынесено в IHost (hk/GaslightMod.cs).
//
//  Driver.Update() идёт первым в кадре ([DefaultExecutionOrder(-32000)]) и
//  отмечает границу кадра. HUD рисуется одной текстурой + несколько строк
//  текста; картинка и строки обновляются HudRefreshPerSec раз в секунду.
//  Когда HUD скрыт - на отрисовку не тратится ничего.
//  Что менять:  частоту обновления HUD и шрифт - константы ниже;
//               вид графика - HudLayout.cs; настройки - Settings.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace Gaslight
{
    // Четыре вещи, которые отличаются у игр.
    public interface IHost
    {
        void Log(string s);
        bool KeyDown(KeyCode k);
        List<ModAssembly> FindModAssemblies();
        string DataDir { get; }
        string GameName { get; }
    }

    public static class App
    {
        public const string Version = "1.1.0";
        public static IHost Host;
        public static Driver Drv;
        public static volatile bool SceneChanged;       // сцена сменилась после последнего автосохранения
        public static float SceneChangedAt;             // когда (Time.unscaledTime читает Driver)
        static long savedFrames = -1;                   // сколько кадров было в последнем сохранённом отчёте сессии
        static bool autosaveAnnounced;

        // Вызвать один раз из адаптера игры.
        public static void Start(IHost host)
        {
            Host = host;
            Settings.Init(host.DataDir, host.Log);
            Settings.Load();
            Profiler.LogSink = host.Log;
            Hooker.Log = host.Log;
            Profiler.Init(Thread.CurrentThread.ManagedThreadId);
            Profiler.Enabled = true;
            ReportWriter.StartSession();
            // Окно закрыли крестиком / Alt+F4: OnApplicationQuit может не сработать, поэтому ещё и на выходе процесса.
            try { AppDomain.CurrentDomain.ProcessExit += delegate { WriteFinal(); }; }
            catch (Exception e) { host.Log(L.T("Не удалось подписаться на выход процесса: ", "Could not subscribe to process exit: ") + e.Message); }
            try
            {
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnScene;
            }
            catch (Exception e) { host.Log(L.T("Не удалось подписаться на смену сцены: ", "Could not subscribe to scene changes: ") + e.Message); }
            GameObject go = new GameObject("Gaslight.Driver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            Drv = go.AddComponent<Driver>();
            host.Log("Gaslight " + Version + L.T(" запущен. HUD: ", " started. HUD: ") + Settings.KeyToggleHud + L.T(", отчёт: ", ", report: ") + Settings.KeyReport
                + L.T(", панель: ", ", panel: ") + Settings.KeyPanel + L.T(", цена замера ", ", cost per measurement ") + Profiler.PairCostNs.ToString("0") + L.T(" нс", " ns"));
        }

        static void OnScene(UnityEngine.SceneManagement.Scene a, UnityEngine.SceneManagement.Scene b)
        {
            Profiler.OnSceneChange(b.name);
            SceneChanged = true;
            SceneChangedAt = Time.unscaledTime;
        }

        // Отчёт по клавише: каждый раз новая папка-снимок.
        public static string WriteReport()
        {
            string f = ReportWriter.Write(Host.DataDir, Host.GameName, Version, false);
            Host.Log(f != null ? L.T("Отчёт сохранён: ", "Report saved: ") + f : L.T("Отчёт не сохранён", "Report was not saved"));
            return f;
        }

        // Автосохранение: данные собираем сейчас (быстро), на диск пишет фоновый поток - игра не ждёт.
        // Пишется в папку этого запуска игры и каждый раз перезаписывает её файлы.
        public static void Autosave()
        {
            if (Profiler.FramesTotal == savedFrames) return;
            long t0 = Stopwatch.GetTimestamp();
            ReportWriter.Pending p = ReportWriter.Prepare(Host.DataDir, Host.GameName, Version, true);
            if (p == null) return;
            savedFrames = Profiler.FramesTotal;
            ThreadPool.QueueUserWorkItem(delegate { ReportWriter.Commit(p); });
            if (!autosaveAnnounced)
            {
                autosaveAnnounced = true;
                Host.Log(L.T("Автосохранение отчёта включено: ", "Report autosave is on: ") + p.Folder
                    + L.T(" (раз в ", " (every ") + Settings.ReportAutosaveSec + L.T(" с на смене сцены и при потере фокуса окна)", " s on scene changes and when the window loses focus)"));
            }
            else if (Settings.VerboseLog)
                Host.Log(L.T("Автосохранение отчёта, сборка ", "Report autosaved, built in ") + ((Stopwatch.GetTimestamp() - t0) * Profiler.TickMs).ToString("0.0") + L.T(" мс", " ms"));
            if (ReportWriter.LastError.Length > 0) Host.Log(L.T("Прошлое автосохранение не записалось: ", "The previous autosave was not written: ") + ReportWriter.LastError);
        }

        // Итоговый отчёт при выходе. Безопасно вызывать несколько раз: если новых кадров не было, ничего не пишет.
        public static void WriteFinal()
        {
            try
            {
                if (!Settings.ReportOnExit || Profiler.FramesTotal <= 60 || Profiler.FramesTotal == savedFrames) return;
                savedFrames = Profiler.FramesTotal;
                string f = ReportWriter.Write(Host.DataDir, Host.GameName, Version, true);
                Host.Log(f != null ? L.T("Отчёт сохранён: ", "Report saved: ") + f : L.T("Отчёт не сохранён", "Report was not saved"));
            }
            catch (Exception) { }
        }
    }

    [DefaultExecutionOrder(-32000)]
    public sealed class Driver : MonoBehaviour
    {
        const float HudRefreshPerSec = 5f;      // обновлений картинки и текста HUD в секунду
        const int FontSize = 12;
        const float ReportFlashSec = 3f;        // сколько секунд показывать «отчёт сохранён»
        const float ReportDebounceSec = 2f;     // не писать отчёт чаще, чем раз в столько секунд
        const float CaptureTimeoutSec = 15f;    // сколько ждать нажатия новой клавиши (меню HK)
        const float AutosaveAfterSceneSec = 3f; // после смены сцены подождать столько секунд (загрузка уже закончилась)
        const float AutosaveForceFactor = 3f;   // если сцены не меняются, всё равно сохранить через интервал * это число
        const float AutosaveMinGapSec = 5f;     // потеря фокуса: не чаще, чем раз в столько секунд

        bool hudVisible;
        bool hudBig;
        int lineCount = HudLayout.TextLines;
        bool hooksStarted;
        float nextHudRefresh;
        float reportFlashUntil;
        float lastReportAt = -100f;
        float lastAutosaveAt;
        bool focusLost;
        SettingRow captureRow;                  // строка настройки, для которой ждём нажатия клавиши
        float captureUntil;
        string reportFlash = "";
        Texture2D chartTex;
        Texture2D whiteTex;
        Raster raster;
        string[] lines = new string[HudLayout.MaxLines];
        GUIStyle style;
        GUIStyle panelStyle;
        SettingsPanel panel = new SettingsPanel();
        Dictionary<string, KeyCode> keyCache = new Dictionary<string, KeyCode>();
        KeyCode kHud, kReport, kPanel, kMode;
        Func<string, bool> panelKeyDown;

        void Awake()
        {
            hudVisible = Settings.HudVisibleAtStart;
            hudBig = Settings.HudBigAtStart;
            ReadKeys();
            Settings.Changed += ReadKeys;
            panelKeyDown = PanelKeyDown;
            raster = new Raster(HudLayout.ChartW, HudLayout.ChartH);
        }

        void ReadKeys()
        {
            kHud = Parse(Settings.KeyToggleHud);
            kReport = Parse(Settings.KeyReport);
            kPanel = Parse(Settings.KeyPanel);
            kMode = Parse(Settings.KeyHudMode);
        }

        KeyCode Parse(string name)
        {
            KeyCode k;
            if (keyCache.TryGetValue(name, out k)) return k;
            try { k = (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }
            catch (Exception) { k = KeyCode.None; }
            keyCache[name] = k;
            return k;
        }

        // Меню HK: «нажми клавишу» для строки r. Следующая нажатая клавиша из KeyNames.Allowed станет значением.
        public void BeginCapture(SettingRow r)
        {
            captureRow = r;
            captureUntil = Time.unscaledTime + CaptureTimeoutSec;
            Flash(r.Name + L.T(": нажми новую клавишу (Esc — отмена)", ": press the new key (Esc to cancel)"), CaptureTimeoutSec);
        }

        void Flash(string text, float sec)
        {
            reportFlash = text;
            reportFlashUntil = Time.unscaledTime + sec;
        }

        void UpdateCapture()
        {
            if (captureRow == null) return;
            IHost host = App.Host;
            if (Time.unscaledTime > captureUntil || host.KeyDown(KeyCode.Escape))
            {
                Flash(captureRow.Name + L.T(": осталось ", ": unchanged, ") + Settings.ValueText(captureRow), 2f);
                captureRow = null;
                return;
            }
            for (int i = 1; i < KeyNames.Allowed.Length; i++)
            {
                KeyCode k = Parse(KeyNames.Allowed[i]);
                if (k == KeyCode.None || !host.KeyDown(k)) continue;
                Settings.SetIndex(captureRow, i);
                Flash(captureRow.Name + " = " + KeyNames.Allowed[i] + L.T(" (в меню обновится при повторном входе)", " (the menu updates when you reopen it)"), 4f);
                captureRow = null;
                return;
            }
        }

        // Когда сохранять отчёт, не дожидаясь выхода: окно потеряло фокус (свернули, Alt+Tab, закрывают),
        // сменилась сцена (и прошло достаточно времени с прошлого раза) или просто давно не сохраняли.
        void UpdateAutosave()
        {
            int every = Settings.ReportAutosaveSec;
            if (every <= 0 || Profiler.FramesTotal <= 60) { focusLost = false; return; }
            float now = Time.unscaledTime;
            float since = now - lastAutosaveAt;
            bool save = false;
            if (focusLost && since >= AutosaveMinGapSec) save = true;
            else if (App.SceneChanged && since >= every && now - App.SceneChangedAt >= AutosaveAfterSceneSec) save = true;
            else if (since >= every * AutosaveForceFactor) save = true;
            if (!save) return;
            focusLost = false;
            App.SceneChanged = false;
            lastAutosaveAt = now;
            App.Autosave();
        }

        void OnApplicationFocus(bool focus) { if (!focus) focusLost = true; }
        void OnApplicationPause(bool paused) { if (paused) focusLost = true; }

        bool PanelKeyDown(string name)
        {
            KeyCode k = Parse(name);
            return k != KeyCode.None && App.Host.KeyDown(k);
        }

        void Update()
        {
            long t0 = Stopwatch.GetTimestamp();
            Profiler.FrameBegin(t0);

            IHost host = App.Host;
            if (captureRow != null) { UpdateCapture(); Profiler.AddSelf(Stopwatch.GetTimestamp() - t0); return; }
            if (kHud != KeyCode.None && host.KeyDown(kHud)) hudVisible = !hudVisible;
            if (kReport != KeyCode.None && host.KeyDown(kReport) && Time.unscaledTime - lastReportAt > ReportDebounceSec)
            {
                lastReportAt = Time.unscaledTime;
                string f = App.WriteReport();
                Flash(f != null ? L.T("Отчёт сохранён: Reports\\", "Report saved: Reports\\") + System.IO.Path.GetFileName(f) : L.T("Ошибка записи отчёта (см. лог)", "Could not write the report (see the log)"), ReportFlashSec);
            }
            UpdateAutosave();
            if (kPanel != KeyCode.None && host.KeyDown(kPanel)) panel.Toggle();
            if (kMode != KeyCode.None && host.KeyDown(kMode)) { hudBig = !hudBig; if (!hudVisible) hudVisible = true; nextHudRefresh = 0; }
            if (panel.Open) panel.Update(panelKeyDown);

            // хуки: после задержки, по чуть-чуть
            if (Settings.ProfilerEnabled && Time.unscaledTime >= Settings.HookDelaySec)
            {
                if (!hooksStarted)
                {
                    hooksStarted = true;
                    List<ModAssembly> mods = new List<ModAssembly>();
                    try { mods = host.FindModAssemblies(); }
                    catch (Exception e) { host.Log(L.T("Не удалось получить список модов: ", "Could not get the mod list: ") + e.Message); }
                    host.Log(L.T("Найдено модов для замера: ", "Mods found to measure: ") + mods.Count);
                    Hooker.Begin(mods, host.DataDir);
                }
                else if (!Hooker.Done)
                {
                    bool more = Hooker.Step();
                    if (!more) host.Log(L.T("Хуки поставлены: ", "Hooks installed: ") + Hooker.Status() + (Hooker.LastError.Length > 0 ? L.T("; последняя ошибка: ", "; last error: ") + Hooker.LastError : ""));
                }
                Profiler.StatusText = Hooker.Status();
                Profiler.Installing = hooksStarted && !Hooker.Done;
            }
            else if (!Settings.ProfilerEnabled) Profiler.StatusText = L.T("замер модов выключен (ProfilerEnabled)", "mod profiling is off (ProfilerEnabled)");

            // текст и картинка HUD - не каждый кадр
            if (hudVisible && Time.unscaledTime >= nextHudRefresh)
            {
                nextHudRefresh = Time.unscaledTime + 1f / HudRefreshPerSec;
                HudLayout.RenderChart(raster);
                if (chartTex == null)
                {
                    chartTex = new Texture2D(HudLayout.ChartW, HudLayout.ChartH, TextureFormat.RGBA32, false);
                    chartTex.filterMode = FilterMode.Point;
                }
                chartTex.LoadRawTextureData(raster.Rgba);
                chartTex.Apply(false, false);
                lineCount = HudLayout.BuildLines(hudBig);
                string[] src = HudLayout.Lines;
                for (int i = 0; i < lineCount; i++) lines[i] = src[i];
            }
            Profiler.AddSelf(Stopwatch.GetTimestamp() - t0);
        }

        void OnGUI()
        {
            if (!hudVisible && !panel.Open && Time.unscaledTime >= reportFlashUntil) return;
            if (Event.current.type != EventType.Repaint) return;
            long t0 = Stopwatch.GetTimestamp();
            EnsureStyles();
            float s = hudBig ? Settings.HudScaleBig : Settings.HudScale;
            float w = (HudLayout.ChartW + HudLayout.Pad * 2) * s;
            if (hudBig) w = Mathf.Max(w, 420 * s);
            float hChart = HudLayout.ChartH * s;
            float hText = HudLayout.LineH * lineCount * s;
            float h = hChart + hText + HudLayout.Pad * 3 * s;
            float x = 10, y = 10;
            if (Settings.HudCorner.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) x = Screen.width - w - 10;
            if (Settings.HudCorner.IndexOf("Bottom", StringComparison.OrdinalIgnoreCase) >= 0) y = Screen.height - h - 10;

            if (hudVisible && chartTex != null)
            {
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(new Rect(x, y, w, h), whiteTex);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(x + HudLayout.Pad * s, y + HudLayout.Pad * s, HudLayout.ChartW * s, hChart), chartTex);
                style.fontSize = (int)(FontSize * s);
                float ty = y + HudLayout.Pad * 2 * s + hChart;
                for (int i = 0; i < lineCount; i++)
                {
                    if (lines[i] != null) GUI.Label(new Rect(x + HudLayout.Pad * s, ty, w, HudLayout.LineH * s + 4), lines[i], style);
                    ty += HudLayout.LineH * s;
                }
            }
            if (Time.unscaledTime < reportFlashUntil)
            {
                style.fontSize = FontSize + 2;
                GUI.color = new Color(0, 0, 0, 0.7f);
                GUI.DrawTexture(new Rect(Screen.width / 2 - 330, 20, 660, 30), whiteTex);
                GUI.color = Color.white;
                GUI.Label(new Rect(Screen.width / 2 - 320, 24, 640, 26), reportFlash, style);
            }
            if (panel.Open)
            {
                int n;
                string[] pl = panel.Lines(out n);
                float pw = Mathf.Min(900, Screen.width - 40);
                float ph = 20 + n * 18 + 10;
                float px = (Screen.width - pw) / 2, py = (Screen.height - ph) / 2;
                GUI.color = new Color(0, 0, 0, 0.85f);
                GUI.DrawTexture(new Rect(px, py, pw, ph), whiteTex);
                GUI.color = Color.white;
                panelStyle.fontSize = FontSize + 1;
                for (int i = 0; i < n; i++)
                    GUI.Label(new Rect(px + 12, py + 10 + i * 18, pw - 24, 20), pl[i], panelStyle);
            }
            Profiler.AddSelf(Stopwatch.GetTimestamp() - t0);
        }

        void EnsureStyles()
        {
            if (style != null) return;
            style = new GUIStyle(GUI.skin.label);
            style.normal.textColor = Color.white;
            style.alignment = TextAnchor.UpperLeft;
            style.wordWrap = false;
            style.richText = false;
            panelStyle = new GUIStyle(style);
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
        }

        void OnApplicationQuit()
        {
            App.WriteFinal();
            Hooker.UndoAll();
        }
    }
}
