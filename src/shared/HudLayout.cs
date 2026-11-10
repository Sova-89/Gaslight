//  HudLayout.cs - как выглядит график в игре и какой текст под ним.
//
//  Что это:  рисует график времени кадра в Raster (одна текстура) и собирает
//            строки текста. Сама отрисовка на экран - в App.cs (Driver.OnGUI).
//  Что менять:  размеры и цвета - константы сразу ниже.
//  График: один столбик = FramesPerColumn последних кадров (берётся худший).
//          Высота столбика = время кадра. Цвет: зелёный < 17 мс, жёлтый < 34 мс,
//          красный дальше. Нижняя синяя часть столбика = доля, занятая модами.
//  Файл не зависит от Unity - проверяется без игры.
using System;
using System.Globalization;
using System.Text;

namespace Gaslight
{
    public static class HudLayout
    {
        // ---- размеры (пиксели) ----
        public const int ChartW = 296;          // ширина графика
        public const int ChartH = 70;           // высота графика
        public const int FramesPerColumn = 4;   // сколько кадров в одном столбике
        public const int Pad = 6;               // отступ панели
        public const int LineH = 15;            // высота строки текста
        public const int TextLines = 5;         // строк текста под графиком (компактный вид)
        public const int MaxLines = 36;         // максимум строк (подробный вид)
        public const int MethodNameMaxChars = 44;
        public const int NameMaxChars = 16;     // длинные имена модов обрезаются

        // ---- пороги и цвета (0xRRGGBBAA) ----
        const float GoodMs = 17f;               // до этого времени кадра столбик зелёный (~60 FPS)
        const float WarnMs = 34f;               // до этого - жёлтый (~30 FPS)
        const uint ColBackground = 0x0B0F14F0;
        const uint ColGood = 0x4FBF6AFF;
        const uint ColWarn = 0xE3BE45FF;
        const uint ColBad = 0xE5544DFF;
        const uint ColMods = 0x5B9BFFFF;        // доля модов
        const uint ColGrid = 0xFFFFFF38;
        static readonly float[] ScaleTiers = new float[] { 25f, 50f, 100f, 200f };

        public static float ChartScaleMs = 25f;     // верх графика, мс (выставляется при отрисовке)
        static float[] colMax = new float[ChartW];
        static float[] colMod = new float[ChartW];
        static string[] lines = new string[MaxLines];
        static int[] order = new int[Profiler.MaxMods + 1];
        static int[] hookOrder = new int[64];
        static long[] hookKey = new long[64];
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static StringBuilder sb = new StringBuilder(160);

        static float PickScale(float maxv)
        {
            for (int i = 0; i < ScaleTiers.Length; i++)
                if (maxv * 1.05f <= ScaleTiers[i]) return ScaleTiers[i];
            return maxv * 1.05f;
        }

        public static void RenderChart(Raster r)
        {
            r.Clear(ColBackground);
            int total = ChartW * FramesPerColumn;
            int n = Profiler.RingCount;
            float maxv = 0;
            for (int c = 0; c < ChartW; c++)
            {
                colMax[c] = 0; colMod[c] = 0;
                for (int k = 0; k < FramesPerColumn; k++)
                {
                    int back = total - 1 - (c * FramesPerColumn + k);   // сколько кадров назад
                    if (back >= n) continue;
                    int idx = Profiler.RingIndexBack(back);
                    float ms = Profiler.RingFrameMs[idx];
                    if (ms > colMax[c]) { colMax[c] = ms; colMod[c] = Profiler.RingModMs[idx]; }
                }
                if (colMax[c] > maxv) maxv = colMax[c];
            }
            float scale = PickScale(maxv);
            ChartScaleMs = scale;

            DrawGrid(r, 1000f / 60f, scale);
            DrawGrid(r, 1000f / 30f, scale);

            for (int c = 0; c < ChartW; c++)
            {
                float ms = colMax[c];
                if (ms <= 0) continue;
                int h = (int)Math.Round(ms / scale * ChartH);
                if (h < 1) h = 1;
                if (h > ChartH) h = ChartH;
                uint col = ms <= GoodMs ? ColGood : (ms <= WarnMs ? ColWarn : ColBad);
                r.FillRect(c, 0, 1, h, col);
                float mm = colMod[c];
                if (mm > 0.01f)
                {
                    int hm = (int)Math.Round(mm / scale * ChartH);
                    if (hm < 1) hm = 1;
                    if (hm > h) hm = h;
                    r.FillRect(c, 0, 1, hm, ColMods);
                }
            }
        }

        static void DrawGrid(Raster r, float ms, float scale)
        {
            if (ms >= scale) return;
            int y = (int)Math.Round(ms / scale * ChartH);
            r.HLine(0, y, ChartW, ColGrid);
        }

        static string Short(string s)
        {
            if (s == null) return "?";
            return s.Length <= NameMaxChars ? s : s.Substring(0, NameMaxChars - 1) + "…";
        }

        static double Pct(double part, double whole)
        {
            return whole > 0 ? part * 100.0 / whole : 0;
        }

        // Строки текста под графиком (вызывать ~5 раз в секунду, не каждый кадр).
        // big = подробный вид. Возвращает число заполненных строк в Lines.
        public static string[] Lines { get { return lines; } }

        public static int BuildLines(bool big)
        {
            BuildCompact();
            if (!big) return TextLines;
            return BuildBig(TextLines);
        }

        static void BuildCompact()
        {
            double avg30 = Profiler.RecentAvgMs(30);
            double fps = avg30 > 0 ? 1000.0 / avg30 : 0;
            double low = Profiler.Low1Fps();
            double total = Profiler.RecentAvgMs(60);
            double mods = Profiler.RecentModMs(60);
            double self = Profiler.RecentSelfMs(60);

            lines[0] = string.Format(Inv, L.T("FPS {0:0}   1% low {1:0}   кадр {2:0.0} мс   [0–{3:0} мс]", "FPS {0:0}   1% low {1:0}   frame {2:0.0} ms   [0-{3:0} ms]"),
                fps, low, avg30, ChartScaleMs);
            lines[1] = string.Format(Inv, L.T("Моды {0:0.00} мс/кадр ({1:0.0}%)   Gaslight {2:0.00} мс ({3:0.0}%)", "Mods {0:0.00} ms/frame ({1:0.0}%)   Gaslight {2:0.00} ms ({3:0.0}%)"),
                mods, Pct(mods, total), self, Pct(self, total));
            lines[2] = TopModsLine();
            lines[3] = SpikeLine();
            lines[4] = Profiler.StatusText;
        }

        // ---- подробный вид ----
        static int BuildBig(int n)
        {
            SecRow r = Profiler.LastSecRow;
            double frameMs = r != null && r.Frames > 0 ? r.SumMs / r.Frames : 0;
            lines[n++] = "";
            lines[n++] = L.T("МОДЫ за последнюю секунду (мс/кадр, % кадра):", "MODS over the last second (ms/frame, % of frame):");
            if (r != null && r.ModMs.Length > 1)
            {
                int cnt = 0;
                for (int m = 1; m < r.ModMs.Length; m++) order[cnt++] = m;
                // сортировка вставками по убыванию (модов мало)
                for (int i = 1; i < cnt; i++)
                {
                    int v = order[i]; int j = i - 1;
                    while (j >= 0 && r.ModMs[order[j]] < r.ModMs[v]) { order[j + 1] = order[j]; j--; }
                    order[j + 1] = v;
                }
                int shown = 0;
                for (int i = 0; i < cnt && shown < Settings.HudBigMods && n < MaxLines - 2; i++)
                {
                    int m = order[i];
                    if (r.ModMs[m] <= 0.0005f) break;
                    lines[n++] = string.Format(Inv, "  {0,-22} {1,6:0.000}   {2,5:0.0}%", Short22(Profiler.ModName[m]), r.ModMs[m], Pct(r.ModMs[m], frameMs));
                    shown++;
                }
                if (shown == 0) lines[n++] = "  —";
            }
            if (Settings.HudBigMethods > 0 && n < MaxLines - 2)
            {
                lines[n++] = L.T("МЕТОДЫ, самые тяжёлые за сессию (мс/кадр, пик мс):", "METHODS, heaviest this session (ms/frame, peak ms):");
                int k = TopHooks(Settings.HudBigMethods);
                long frames = Math.Max(1, Profiler.FramesTotal);
                for (int i = 0; i < k && n < MaxLines - 1; i++)
                {
                    int h = hookOrder[i];
                    lines[n++] = string.Format(Inv, "  {0,-44} {1,6:0.000}  {2,6:0.0}", ShortMethod(Profiler.HookName[h]),
                        Profiler.HookTotalTicks[h] * Profiler.TickMs / frames, Profiler.HookMaxTicks[h] * Profiler.TickMs);
                }
                if (k == 0) lines[n++] = "  —";
            }
            if (n < MaxLines)
            {
                int[] c = Profiler.SpikeCategoryCount;
                lines[n++] = string.Format(Inv, L.T("ЛАГИ: {0} — мод {1}, сцена {2}, GC {3}, несколько модов {4}, не моды {5}; остановок >1 с: {6}", "SPIKES: {0} - mod {1}, scene {2}, GC {3}, several mods {4}, not mods {5}; stalls >1 s: {6}"),
                    Profiler.SpikesTotal, c[1], c[2], c[3], c[4], c[5], Profiler.StallsTotal);
            }
            return n;
        }

        // top-N хуков по суммарному времени; результат в hookOrder, возвращает количество
        static int TopHooks(int want)
        {
            if (want > hookOrder.Length) want = hookOrder.Length;
            int k = 0;
            for (int h = 1; h < Profiler.HookCount; h++)
            {
                long v = Profiler.HookTotalTicks[h];
                if (v <= 0) continue;
                if (k < want) { hookOrder[k] = h; hookKey[k] = v; k++; SiftUp(k - 1); }
                else if (v > hookKey[k - 1]) { hookOrder[k - 1] = h; hookKey[k - 1] = v; SiftUp(k - 1); }
            }
            return k;
        }

        static void SiftUp(int i)
        {
            while (i > 0 && hookKey[i] > hookKey[i - 1])
            {
                long tk = hookKey[i]; hookKey[i] = hookKey[i - 1]; hookKey[i - 1] = tk;
                int th = hookOrder[i]; hookOrder[i] = hookOrder[i - 1]; hookOrder[i - 1] = th;
                i--;
            }
        }

        static string Short22(string s)
        {
            if (s == null) return "?";
            if (s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.Length <= 22 ? s : s.Substring(0, 21) + "…";
        }

        static string ShortMethod(string s)
        {
            if (s == null) return "?";
            if (s.Length <= MethodNameMaxChars) return s;
            return "…" + s.Substring(s.Length - MethodNameMaxChars + 1);
        }

        static string TopModsLine()
        {
            SecRow r = Profiler.LastSecRow;
            if (r == null || r.ModMs.Length < 2) return L.T("Топ модов: —", "Top mods: -");
            int a = -1, b = -1, c = -1;
            for (int m = 1; m < r.ModMs.Length; m++)
            {
                float v = r.ModMs[m];
                if (v <= 0.0005f) continue;
                if (a < 0 || v > r.ModMs[a]) { c = b; b = a; a = m; }
                else if (b < 0 || v > r.ModMs[b]) { c = b; b = m; }
                else if (c < 0 || v > r.ModMs[c]) { c = m; }
            }
            if (a < 0) return L.T("Топ модов: —", "Top mods: -");
            sb.Length = 0;
            sb.Append(L.T("Топ: ", "Top: "));
            AppendMod(r, a);
            if (b >= 0) { sb.Append("  ·  "); AppendMod(r, b); }
            if (c >= 0) { sb.Append("  ·  "); AppendMod(r, c); }
            return sb.ToString();
        }

        static void AppendMod(SecRow r, int m)
        {
            sb.Append(Short(Profiler.ModName[m]));
            sb.Append(' ');
            sb.Append(r.ModMs[m].ToString("0.00", Inv));
        }

        static string SpikeLine()
        {
            if (Profiler.SpikesTotal <= 0) return L.T("Лагов нет", "No spikes");
            SpikeInfo s = Profiler.Spikes.Count > 0 ? Profiler.Spikes[Profiler.Spikes.Count - 1] : null;
            if (s == null) return L.T("Лагов: ", "Spikes: ") + Profiler.SpikesTotal;
            // коротко: мод / сцена / GC называем по имени; «не моды» поясняем, а во время установки замеров - предупреждаем
            string who = s.Culprit;
            if (s.Category != 1 && s.DuringInstall) who += L.T(" · идёт установка замеров", " · measurements still installing");
            else if (s.Category == 5) who = s.Verdict;
            return string.Format(Inv, L.T("Лагов: {0}   последний {1:0} мс — {2}", "Spikes: {0}   last {1:0} ms - {2}"), Profiler.SpikesTotal, s.FrameMs, who);
        }
    }
}
