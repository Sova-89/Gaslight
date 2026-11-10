//  Profiler.cs - ядро: секундомеры, статистика кадров, детектор лагов.
//
//  Как это работает:
//   * Hooker вставляет в начало каждого проверяемого метода вызов Profiler.Enter(id),
//     а перед каждым return - Profiler.Leave(id). Так считается ИСКЛЮЧИТЕЛЬНОЕ
//     время метода (без вызовов других проверяемых методов внутри).
//   * Driver.Update() (самый первый в кадре) вызывает Profiler.FrameBegin():
//     это граница кадра. Здесь же кадр разбирается: FPS, гистограмма, лаги.
//   * Горячий путь (Enter/Leave): 2 вызова Stopwatch.GetTimestamp + запись в массивы.
//     Никаких new, строк, LINQ и блокировок. Работает только на главном потоке.
//   * Свою нагрузку считаем так: число вызовов Enter/Leave * замеренную при старте
//     цену одной пары + время, потраченное Driver'ом (Update/OnGUI).
//
//  Что здесь менять:  пороги - в Settings.cs; константы ниже - размеры буферов.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Gaslight
{
    // Один зафиксированный лаг.
    public sealed class SpikeInfo
    {
        public double TimeSec;     // когда (секунды от старта)
        public float FrameMs;      // длина кадра
        public string Scene;       // сцена в момент лага
        public int GcDelta;        // сколько сборок мусора было в этом кадре
        public string TopMethods;  // 3 самых тяжёлых метода кадра
        public string TopMods;     // 3 самых тяжёлых мода кадра
        public string Verdict;     // словесный вердикт
        public string Culprit;     // коротко: кто виноват
        public int Category;       // 1 мод, 2 сцена, 3 GC, 4 несколько модов, 5 не моды
        public bool DuringInstall; // лаг случился, пока Gaslight ещё ставил свои замеры (мог влиять и он сам)
    }

    // Строка «по секундам» для отчёта.
    public sealed class SecRow
    {
        public int T;           // номер секунды от старта
        public int Frames;      // кадров за секунду
        public float SumMs;     // сумма длин кадров
        public float MaxMs;     // самый длинный кадр
        public float SelfMs;    // нагрузка самого Gaslight, мс на кадр
        public float[] ModMs;   // мс на кадр по каждому моду (индекс 0 = Gaslight)
    }

    public sealed class SceneEvent
    {
        public double TimeSec;
        public string Name;
    }

    public static class Profiler
    {
        // ---- константы ----
        public const int MaxHooks = 20000;     // максимум хуков (+ служебные слоты: калибровка и OrigId)
        // Служебный id: сюда идёт время вызова ОРИГИНАЛА игры внутри хука мода (On.* / MonoMod: orig(self, ...)).
        // Это работа самой игры, не мода. Hooker оборачивает такой вызов в Enter/Leave(OrigId), а Profiler уже
        // вычитает время вложенных замеров из родительского - так мод получает только время своего кода.
        public const int OrigId = MaxHooks + 1;
        public static long OrigTotalTicks;     // всего ушло на оригиналы игры внутри хуков (кроме пропущенных кадров)
        static long lastOrigTicks;             // то же за последний разобранный кадр (нужно для вердикта лага)
        public const int MaxMods = 255;        // максимум DLL
        public const int RingSize = 1200;      // сколько последних кадров хранит кольцо (для HUD)
        const int StackSize = 64;              // глубина стека вложенных замеров
        const double HistStepMs = 0.25;        // шаг гистограммы кадров, мс
        const int HistBins = 400;              // 400 * 0.25 = до 100 мс, дальше - одна общая корзина
        const int MaxSpikes = 500;             // сколько лагов помним
        const int MaxTimelineSecs = 10800;     // 3 часа секундных строк
        const double TinyCallUs = 30.0;        // «мелкий» метод: в среднем быстрее этого, мкс на вызов
        const int CalibratePairs = 20000;      // пар Enter/Leave для замера собственной цены
        const int MaxNotes = 100;
        const double SceneBlameSec = 2.0;      // лаг рядом со сменой сцены (± секунд) = загрузка сцены
        const double SeveralModsShare = 0.25;  // моды вместе заняли >= 25% кадра
        const int WarmupFrames = 30;           // первые кадры только настраивают «обычный» кадр
        const int SpikeRunReset = 30;          // столько лаг-кадров подряд = это новая норма
        const double StallMs = 1000.0;         // кадр длиннее этого = «остановка» (загрузка): в лаги попадает, в FPS и 1% low - нет

        // ---- состояние ----
        public static bool Enabled;            // false - Enter/Leave выходят сразу
        static int mainThreadId;
        public static long Freq;
        public static double TickMs;           // мс в одном тике Stopwatch
        static long startTicks;
        public static long StartTicks { get { return startTicks; } }
        static long pairCost = 1;              // цена пары Enter+Leave, тиков

        // ---- реестр DLL и методов
        public static string[] ModName = new string[MaxMods + 1];
        public static int ModCount;
        public static int[] HookMod = new int[MaxHooks + 2];
        public static string[] HookName = new string[MaxHooks + 2];
        public static int HookCount;
        public static bool[] HookDisabled = new bool[MaxHooks + 2];

        // ---- стек замера (только главный поток)
        static int depth;
        static int overflow;
        static int[] stId = new int[StackSize];
        static long[] stExcl = new long[StackSize];
        static long mark;

        // ---- накопители кадра (по хукам)
        static long[] hkFrameTicks = new long[MaxHooks + 2];
        static int[] hkFrameCalls = new int[MaxHooks + 2];
        static int[] touched = new int[MaxHooks + 2];
        static int touchedCount;
        static long pairCalls;
        static long drvTicks;

        // ---- накопители кадра (по модам)
        static long[] frameModTicks = new long[MaxMods + 1];
        static bool[] modSeen = new bool[MaxMods + 1];
        static int[] touchedMods = new int[MaxMods + 1];
        static int touchedModCount;

        // ---- итоги за сессию
        public static long[] HookTotalTicks = new long[MaxHooks + 2];
        public static long[] HookTotalCalls = new long[MaxHooks + 2];
        public static long[] HookMaxTicks = new long[MaxHooks + 2];
        public static long[] ModTotalTicks = new long[MaxMods + 1];
        public static long[] ModMaxTicks = new long[MaxMods + 1];
        public static long FramesTotal;
        public static long PausedFrames;
        public static long StallsTotal;        // кадров-остановок (> StallMs)
        public static double StallsSumMs;
        public static double SumFrameMs;
        public static double WorstFrameMs;
        public static long SumFrameTicks;
        public static long ModAllTotalTicks;
        public static long SelfTotalTicks;
        public static long CallsTotal;
        static int[] hist = new int[HistBins];
        static long overCount;
        static double overSumMs;

        // ---- кольцо последних кадров (для графика HUD)
        public static float[] RingFrameMs = new float[RingSize];
        public static float[] RingModMs = new float[RingSize];
        public static float[] RingSelfMs = new float[RingSize];
        static int ringHead;
        public static int RingCount;

        // ---- по секундам
        public static List<SecRow> Timeline = new List<SecRow>();
        public static SecRow LastSecRow;
        static int curSec = -1;
        static int curFrames;
        static double curSumMs;
        static double curMaxMs;
        static long curSelfTicks;
        static long[] secModTicks = new long[MaxMods + 1];
        static int overBudgetSeconds;

        // ---- лаги и сцены
        public static List<SpikeInfo> Spikes = new List<SpikeInfo>();
        public static int SpikesTotal;
        public static int[] SpikeCategoryCount = new int[6];
        public static List<SceneEvent> Scenes = new List<SceneEvent>();
        public static string CurrentScene = "";
        static double lastSceneSec = -1000;
        static double avgMs;
        static int spikeRun;
        static int lastGc;
        static int fakeGc = -1;
        static long lastFrameStart;

        // ---- сообщения
        public static List<string> Notes = new List<string>();
        public static string StatusText = "";
        public static Action<string> LogSink;
        public static int SelfProtectHits;
        public static bool Installing;       // true, пока Hooker ищет методы и ставит замеры (ставит Driver)
        // Обычная длина кадра, мс (без лагов). Hooker по ней считает бюджет установки в процентах от кадра.
        public static double TypicalFrameMs { get { return avgMs; } }
        public static int DisabledHooks;

        // ---- запуск ----
        public static void Init(int mainThread)
        {
            mainThreadId = mainThread;
            Freq = Stopwatch.Frequency;
            TickMs = 1000.0 / Freq;
            startTicks = Stopwatch.GetTimestamp();
            ModCount = 0; HookCount = 0;
            RegisterMod("Gaslight");
            RegisterHook(0, "Gaslight");
            Calibrate();
        }

        static void Calibrate()
        {
            bool was = Enabled;
            Enabled = true;
            int id = MaxHooks;
            for (int i = 0; i < 200; i++) { Enter(id); Leave(id); }   // прогрев JIT
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < CalibratePairs; i++) { Enter(id); Leave(id); }
            long t1 = Stopwatch.GetTimestamp();
            pairCost = (t1 - t0) / CalibratePairs;
            if (pairCost < 1) pairCost = 1;
            depth = 0; overflow = 0; touchedCount = 0; pairCalls = 0;
            hkFrameCalls[id] = 0; hkFrameTicks[id] = 0;
            Enabled = was;
        }

        public static double PairCostNs { get { return pairCost * TickMs * 1000000.0; } }

        public static int RegisterMod(string name)
        {
            for (int i = 0; i < ModCount; i++) if (ModName[i] == name) return i;
            if (ModCount >= MaxMods) return -1;
            ModName[ModCount] = name;
            return ModCount++;
        }

        public static int RegisterHook(int mod, string name)
        {
            if (HookCount >= MaxHooks) return -1;
            HookMod[HookCount] = mod;
            HookName[HookCount] = name;
            return HookCount++;
        }

        // ---- горячий путь ----
        public static void Enter(int id)
        {
            if (!Enabled) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
            if (HookDisabled[id]) return;
            long now = Stopwatch.GetTimestamp();
            int d = depth;
            if (d >= StackSize) { overflow++; return; }
            if (d > 0) stExcl[d - 1] += now - mark;
            stId[d] = id;
            stExcl[d] = 0;
            depth = d + 1;
            mark = now;
            if (hkFrameCalls[id]++ == 0) touched[touchedCount++] = id;
            pairCalls++;
        }

        public static void Leave(int id)
        {
            if (!Enabled) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
            if (HookDisabled[id]) return;
            long now = Stopwatch.GetTimestamp();
            if (overflow > 0) { overflow--; return; }
            int d = depth;
            if (d == 0) return;
            long ex;
            if (stId[d - 1] == id)
            {
                ex = stExcl[d - 1] + (now - mark);
            }
            else
            {
                // Между Enter и Leave кто-то вышел через исключение - разматываем стек до нашего id.
                int k = d - 2;
                while (k >= 0 && stId[k] != id) k--;
                if (k < 0) return;      // такого входа нет - игнорируем
                d = k + 1;
                ex = stExcl[k];
            }
            hkFrameTicks[id] += ex;
            depth = d - 1;
            mark = now;
        }

        // Driver добавляет сюда время своей работы (Update, OnGUI, установка хуков).
        public static void AddSelf(long ticks)
        {
            drvTicks += ticks;
        }

        // ---- граница кадра ----
        public static void FrameBegin(long now)
        {
            if (lastFrameStart != 0) EndFrame(now - lastFrameStart, now);
            lastFrameStart = now;
            depth = 0;
            overflow = 0;
            mark = now;
        }

        // Для проверки без игры: подать синтетический кадр.
        public static void FeedFrame(long now, double frameMs, int[] ids, double[] ms, int[] calls, int n, double driverMs, int gcTotal)
        {
            fakeGc = gcTotal;
            for (int i = 0; i < n; i++)
            {
                int id = ids[i];
                if (hkFrameCalls[id] == 0) touched[touchedCount++] = id;
                hkFrameCalls[id] += calls[i];
                hkFrameTicks[id] += (long)(ms[i] / TickMs);
                pairCalls += calls[i];
            }
            drvTicks += (long)(driverMs / TickMs);
            EndFrame((long)(frameMs / TickMs), now);
        }

        public static long TicksAtSec(double sec)
        {
            return startTicks + (long)(sec * 1000.0 / TickMs);
        }

        static void EndFrame(long frameTicks, long now)
        {
            double frameMs = frameTicks * TickMs;
            double tSec = (now - startTicks) * TickMs / 1000.0;
            int gcNow = fakeGc >= 0 ? fakeGc : GC.CollectionCount(0);
            int gcDelta = gcNow - lastGc;
            lastGc = gcNow;
            if (gcDelta < 0) gcDelta = 0;
            bool ignore = frameMs > Settings.SpikeIgnoreSec * 1000.0;

            // 1) разбор накопленного по хукам
            long modAllTicks = 0;
            long callsFrame = 0;
            int id0 = -1, id1 = -1, id2 = -1;
            long v0 = 0, v1 = 0, v2 = 0;
            long origFrame = 0;
            touchedModCount = 0;
            for (int i = 0; i < touchedCount; i++)
            {
                int id = touched[i];
                long tk = hkFrameTicks[id];
                int c = hkFrameCalls[id];
                hkFrameTicks[id] = 0;
                hkFrameCalls[id] = 0;
                if (ignore) continue;
                if (id == OrigId) { origFrame = tk; OrigTotalTicks += tk; continue; }   // это игра, а не мод
                int m = HookMod[id];
                HookTotalTicks[id] += tk;
                HookTotalCalls[id] += c;
                if (tk > HookMaxTicks[id]) HookMaxTicks[id] = tk;
                if (!modSeen[m]) { modSeen[m] = true; touchedMods[touchedModCount++] = m; }
                frameModTicks[m] += tk;
                modAllTicks += tk;
                callsFrame += c;
                if (tk > v2)
                {
                    if (tk > v1)
                    {
                        id2 = id1; v2 = v1;
                        if (tk > v0) { id1 = id0; v1 = v0; id0 = id; v0 = tk; }
                        else { id1 = id; v1 = tk; }
                    }
                    else { id2 = id; v2 = tk; }
                }
            }
            touchedCount = 0;
            lastOrigTicks = origFrame;

            long selfTicks = pairCalls * pairCost + drvTicks;
            pairCalls = 0;
            drvTicks = 0;

            if (ignore)
            {
                PausedFrames++;
                return;
            }

            // смена секунды: закрыть прошлую ДО того, как добавим сюда нагрузку этого кадра
            int sec = (int)tSec;
            if (curSec < 0) curSec = sec;
            if (sec != curSec) { CloseSecond(); curSec = sec; }

            // 2) итоги по модам
            int m0 = -1, m1 = -1, m2 = -1;
            long w0 = 0, w1 = 0, w2 = 0;
            for (int j = 0; j < touchedModCount; j++)
            {
                int m = touchedMods[j];
                long v = frameModTicks[m];
                ModTotalTicks[m] += v;
                if (v > ModMaxTicks[m]) ModMaxTicks[m] = v;
                secModTicks[m] += v;
                if (v > w2)
                {
                    if (v > w1)
                    {
                        m2 = m1; w2 = w1;
                        if (v > w0) { m1 = m0; w1 = w0; m0 = m; w0 = v; }
                        else { m1 = m; w1 = v; }
                    }
                    else { m2 = m; w2 = v; }
                }
                frameModTicks[m] = 0;
                modSeen[m] = false;
            }

            // остановка (загрузка сцены, GC на несколько секунд): записать как лаг и не портить статистику
            if (frameMs > StallMs)
            {
                StallsTotal++;
                StallsSumMs += frameMs;
                RecordSpike(tSec, frameMs, frameTicks, gcDelta, id0, v0, id1, v1, id2, v2, m0, w0, m1, w1, m2, w2, modAllTicks);
                return;
            }

            // 3) общая статистика
            FramesTotal++;
            SumFrameMs += frameMs;
            SumFrameTicks += frameTicks;
            ModAllTotalTicks += modAllTicks;
            SelfTotalTicks += selfTicks;
            CallsTotal += callsFrame;
            if (frameMs > WorstFrameMs) WorstFrameMs = frameMs;
            int bin = (int)(frameMs / HistStepMs);
            if (bin >= HistBins) { overCount++; overSumMs += frameMs; }
            else hist[bin]++;

            RingFrameMs[ringHead] = (float)frameMs;
            RingModMs[ringHead] = (float)(modAllTicks * TickMs);
            RingSelfMs[ringHead] = (float)(selfTicks * TickMs);
            ringHead = (ringHead + 1) % RingSize;
            if (RingCount < RingSize) RingCount++;

            // 4) по секундам (смена секунды уже обработана выше)
            curFrames++;
            curSumMs += frameMs;
            if (frameMs > curMaxMs) curMaxMs = frameMs;
            curSelfTicks += selfTicks;

            // 5) лаги
            if (FramesTotal <= WarmupFrames)
            {
                avgMs = avgMs <= 0 ? frameMs : avgMs * 0.9 + frameMs * 0.1;
            }
            else if (frameMs >= Settings.SpikeMinMs && frameMs >= Settings.SpikeFactor * avgMs)
            {
                RecordSpike(tSec, frameMs, frameTicks, gcDelta, id0, v0, id1, v1, id2, v2, m0, w0, m1, w1, m2, w2, modAllTicks);
                spikeRun++;
                if (spikeRun > SpikeRunReset) { avgMs = frameMs; spikeRun = 0; }
            }
            else
            {
                spikeRun = 0;
                avgMs = avgMs * 0.98 + frameMs * 0.02;
            }
        }

        // ---- лаги ----
        static string MsText(long ticks)
        {
            return (ticks * TickMs).ToString("0.0");
        }

        static void RecordSpike(double tSec, double frameMs, long frameTicks, int gcDelta,
            int id0, long v0, int id1, long v1, int id2, long v2,
            int m0, long w0, int m1, long w1, int m2, long w2, long modAllTicks)
        {
            SpikesTotal++;
            SpikeInfo s = new SpikeInfo();
            s.TimeSec = tSec;
            s.FrameMs = (float)frameMs;
            s.Scene = CurrentScene;
            s.GcDelta = gcDelta;

            string ms = L.T(" мс", " ms");
            string meth = "";
            if (id0 >= 0) meth = HookName[id0] + " " + MsText(v0) + ms;
            if (id1 >= 0) meth += "; " + HookName[id1] + " " + MsText(v1) + ms;
            if (id2 >= 0) meth += "; " + HookName[id2] + " " + MsText(v2) + ms;
            s.TopMethods = meth;

            string mods = "";
            if (m0 >= 0) mods = ModName[m0] + " " + MsText(w0) + ms;
            if (m1 >= 0) mods += "; " + ModName[m1] + " " + MsText(w1) + ms;
            if (m2 >= 0) mods += "; " + ModName[m2] + " " + MsText(w2) + ms;
            s.TopMods = mods;
            if (lastOrigTicks * TickMs >= 1.0)
                s.TopMethods += (s.TopMethods.Length > 0 ? "; " : "") + L.T("игра внутри хуков модов ", "game code inside mod hooks ") + MsText(lastOrigTicks) + ms;

            // Вердикт по порядку из плана.
            if (m0 >= 0 && w0 >= Settings.BlameShare * frameTicks)
            {
                s.Category = 1;
                s.Culprit = ModName[m0] + (id0 >= 0 ? " (" + HookName[id0] + ")" : "");
                s.Verdict = L.T("мод ", "mod ") + s.Culprit;
            }
            else if (Math.Abs(tSec - lastSceneSec) <= SceneBlameSec)
            {
                s.Category = 2;
                s.Culprit = L.T("загрузка сцены", "scene load");
                s.Verdict = s.Culprit;
            }
            else if (lastOrigTicks >= Settings.BlameShare * frameTicks)
            {
                // кадр почти целиком ушёл на оригинал игры, который вызывает хук мода (загрузка/сохранение и т.п.)
                s.Category = 5;
                s.Culprit = L.T("игра (внутри хука мода)", "game (inside a mod hook)");
                s.Verdict = L.T("не моды: игра выполняла свой код внутри перехвата мода (вызов оригинала)", "not mods: the game was running its own code inside a mod hook (call of the original)");
            }
            else if (gcDelta > 0)
            {
                s.Category = 3;
                s.Culprit = L.T("сборщик мусора", "garbage collector");
                s.Verdict = L.T("сборщик мусора (GC)", "garbage collector (GC)");
            }
            else if (modAllTicks >= SeveralModsShare * frameTicks)
            {
                s.Category = 4;
                s.Culprit = L.T("несколько модов вместе", "several mods together");
                s.Verdict = s.Culprit;
            }
            else
            {
                s.Category = 5;
                s.Culprit = L.T("не моды", "not mods");
                s.Verdict = L.T("не моды (игра / видеокарта / система)", "not mods (game / GPU / system)");
            }
            // Пока идёт установка замеров, часть лага могла дать и сама установка (JIT, мусор): честно пишем об этом.
            s.DuringInstall = Installing;
            if (Installing && s.Category != 1)
                s.Verdict += L.T(" · идёт установка замеров Gaslight", " · Gaslight is still installing its measurements");
            SpikeCategoryCount[s.Category]++;
            if (Spikes.Count < MaxSpikes) Spikes.Add(s);
        }

        public static void OnSceneChange(string name)
        {
            OnSceneChangeAt((Stopwatch.GetTimestamp() - startTicks) * TickMs / 1000.0, name);
        }

        // То же с явным временем (секунды от старта) - нужно для проверки без игры.
        public static void OnSceneChangeAt(double t, string name)
        {
            CurrentScene = name == null ? "" : name;
            lastSceneSec = t;
            if (Scenes.Count < 2000)
            {
                SceneEvent e = new SceneEvent();
                e.TimeSec = t;
                e.Name = CurrentScene;
                Scenes.Add(e);
            }
        }

        // ---- секунды и самозащита ----
        static void CloseSecond()
        {
            if (curFrames > 0)
            {
                SecRow r = new SecRow();
                r.T = curSec;
                r.Frames = curFrames;
                r.SumMs = (float)curSumMs;
                r.MaxMs = (float)curMaxMs;
                r.SelfMs = (float)(curSelfTicks * TickMs / curFrames);
                r.ModMs = new float[ModCount];
                for (int m = 1; m < ModCount; m++)
                    r.ModMs[m] = (float)(secModTicks[m] * TickMs / curFrames);
                r.ModMs[0] = r.SelfMs;
                Timeline.Add(r);
                if (Timeline.Count > MaxTimelineSecs + 600) Timeline.RemoveRange(0, 600);
                LastSecRow = r;

                double secTicks = curSumMs / TickMs;
                double selfPct = secTicks > 0 ? curSelfTicks * 100.0 / secTicks : 0;
                // Пока идёт установка замеров (первые секунды), нагрузка мода временная и самозащиту не включает:
                // иначе она навсегда отключала бы замеры из-за стартовой суеты. Смотрим только на «рабочую» нагрузку.
                if (Settings.SelfProtect && !Installing && selfPct > Settings.SelfBudgetPercent)
                {
                    overBudgetSeconds++;
                    if (overBudgetSeconds >= Settings.SelfProtectSeconds)
                    {
                        ApplySelfProtection(selfPct);
                        overBudgetSeconds = 0;
                    }
                }
                else overBudgetSeconds = 0;
            }
            for (int m = 0; m <= MaxMods; m++) secModTicks[m] = 0;
            curFrames = 0; curSumMs = 0; curMaxMs = 0; curSelfTicks = 0;
        }

        // Срез текущей (незакрытой) секунды для отчёта; null, если кадров ещё нет.
        public static SecRow PeekCurrentSecond()
        {
            if (curFrames <= 0) return null;
            SecRow r = new SecRow();
            r.T = curSec;
            r.Frames = curFrames;
            r.SumMs = (float)curSumMs;
            r.MaxMs = (float)curMaxMs;
            r.SelfMs = (float)(curSelfTicks * TickMs / curFrames);
            r.ModMs = new float[ModCount];
            for (int m = 1; m < ModCount; m++) r.ModMs[m] = (float)(secModTicks[m] * TickMs / curFrames);
            r.ModMs[0] = r.SelfMs;
            return r;
        }

        static void Note(string s)
        {
            if (Notes.Count < MaxNotes) Notes.Add(s);
            if (LogSink != null) LogSink(s);
        }

        // Отключает замер у «мелких» методов: их очень много вызовов, а времени ничтожно.
        static void ApplySelfProtection(double selfPct)
        {
            double total = 0;
            for (int i = 1; i < HookCount; i++) total += HookTotalCalls[i];
            if (total <= 0) return;
            double fraction = 1.0 - (Settings.SelfBudgetPercent * 0.5) / selfPct;
            if (fraction < 0.3) fraction = 0.3;
            if (fraction > 0.95) fraction = 0.95;
            double need = total * fraction;

            List<int> ids = new List<int>();
            List<long> keys = new List<long>();
            for (int i = 1; i < HookCount; i++)
            {
                if (HookDisabled[i] || HookTotalCalls[i] <= 0) continue;
                double us = HookTotalTicks[i] * TickMs * 1000.0 / HookTotalCalls[i];
                if (us >= TinyCallUs) continue;
                ids.Add(i);
                keys.Add(-HookTotalCalls[i]);
            }
            int[] idArr = ids.ToArray();
            long[] keyArr = keys.ToArray();
            Array.Sort(keyArr, idArr);

            double removed = 0;
            int n = 0;
            for (int k = 0; k < idArr.Length && removed < need; k++)
            {
                HookDisabled[idArr[k]] = true;
                removed += HookTotalCalls[idArr[k]];
                n++;
            }
            SelfProtectHits++;
            DisabledHooks += n;
            double t = (Stopwatch.GetTimestamp() - startTicks) * TickMs / 1000.0;
            Note(L.T("Самозащита (" + t.ToString("0") + " с): Gaslight занимал " + selfPct.ToString("0.0") + "% кадра, замер отключён у "
                + n + " мелких методов (всего отключено " + DisabledHooks + ").",
                "Self-protection (" + t.ToString("0") + " s): Gaslight was using " + selfPct.ToString("0.0") + "% of the frame, measuring turned off for "
                + n + " small methods (" + DisabledHooks + " turned off in total)."));
        }

        // ---- чтение для HUD и отчёта ----
        public static int RingIndexBack(int back)
        {
            return (ringHead - 1 - back + RingSize * 2) % RingSize;
        }

        static double RingAverage(float[] arr, int frames)
        {
            int n = Math.Min(frames, RingCount);
            if (n <= 0) return 0;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += arr[RingIndexBack(i)];
            return sum / n;
        }

        public static double RecentAvgMs(int frames) { return RingAverage(RingFrameMs, frames); }
        public static double RecentModMs(int frames) { return RingAverage(RingModMs, frames); }
        public static double RecentSelfMs(int frames) { return RingAverage(RingSelfMs, frames); }

        public static double AvgFps()
        {
            return SumFrameMs > 0 ? FramesTotal * 1000.0 / SumFrameMs : 0;
        }

        // 1% low: средний FPS самых медленных 1% кадров за сессию.
        public static double Low1Fps()
        {
            if (FramesTotal <= 0) return 0;
            long want = (long)Math.Ceiling(FramesTotal * 0.01);
            if (want < 1) want = 1;
            long got = 0;
            double sum = 0;
            if (overCount > 0)
            {
                long use = Math.Min(overCount, want);
                sum += overSumMs / overCount * use;
                got += use;
            }
            for (int b = HistBins - 1; b >= 0 && got < want; b--)
            {
                if (hist[b] == 0) continue;
                long use = Math.Min(hist[b], want - got);
                sum += (b + 0.5) * HistStepMs * use;
                got += use;
            }
            if (got <= 0 || sum <= 0) return 0;
            return 1000.0 / (sum / got);
        }

        public static double SimDuration = -1;     // только для проверки без игры

        public static double DurationSec()
        {
            if (SimDuration >= 0) return SimDuration;
            return (Stopwatch.GetTimestamp() - startTicks) * TickMs / 1000.0;
        }
    }
}
