//  ReportWriter.cs - записывает отчёт сессии в папку Reports\<дата-время>\.
//
//  Что получается:
//    report.html   - главный отчёт (график FPS, нагрузка по DLL, лаги, вердикт)
//    mods.csv      - нагрузка по DLL и по методам (открывается в Excel)
//    timeline.csv  - по секундам: FPS, худший кадр, нагрузка каждого мода
//    spikes.csv    - лаги и кто в них виноват
//  Как устроен HTML:  report_template.html лежит РЯДОМ с DLL. В него вместо
//    метки /*DATA*/ подставляется JSON с данными. Хочешь поменять вид отчёта -
//    правь report_template.html, пересобирать мод не нужно.
//  Папка: Write(..., false) - новая папка «снимок» (клавиша отчёта), Write(..., true) - папка текущего запуска игры
//    (автосохранение и отчёт при выходе пишут в неё же, перезаписывая файлы, чтобы не плодить папки).
//  Файл не зависит от Unity - проверяется без игры.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gaslight
{
    public static class ReportWriter
    {
        const string Placeholder = "/*DATA*/";
        const double VerdictBarelyMs = 0.5;       // меньше стольких мс/кадр от модов - «почти не влияют», какой бы ни была доля
        const double VerdictHeavyMs = 1.5;        // «сильно нагружают» - только если модов не меньше стольких мс/кадр (и 15% кадра)
        const int MaxHooksPerModInJson = 80;      // сколько методов на DLL попадёт в HTML (в CSV - все)
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string FallbackHtml
        {
            get
            {
                return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Gaslight</title></head><body>" +
                    L.T("<h2>Gaslight: не найден report_template.html</h2><p>Данные отчёта ниже (JSON). Положи report_template.html рядом с Gaslight.dll.</p>",
                        "<h2>Gaslight: report_template.html not found</h2><p>The report data is below (JSON). Put report_template.html next to Gaslight.dll.</p>") +
                    "<pre id=\"data\" style=\"white-space:pre-wrap\">/*DATA*/</pre></body></html>";
            }
        }

        // Отчёт, собранный в памяти: сборка быстрая и идёт в главном потоке (читает данные профайлера),
        // а запись на диск (Commit) можно отдать фоновому потоку, чтобы не дёргать игру.
        public sealed class Pending
        {
            public string Folder;
            public string[] Names;
            public string[] Texts;
            public bool[] Bom;
        }

        static string sessionName;                         // имя папки этого запуска игры
        static readonly object ioLock = new object();      // чтобы две записи не мешали друг другу
        public static string LastError = "";               // последняя ошибка записи (читает главный поток)

        // Запомнить момент старта: от него зависит имя папки сессии.
        public static void StartSession()
        {
            sessionName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
        }

        static string SessionName()
        {
            if (sessionName == null) StartSession();
            return sessionName;
        }

        // Собирает файлы отчёта в памяти. sessionFolder=true - папка этого запуска игры, false - новая папка-снимок.
        public static Pending Prepare(string dataDir, string game, string version, bool sessionFolder)
        {
            try
            {
                string name = sessionFolder ? SessionName() : DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
                string tplPath = Path.Combine(dataDir, "report_template.html");
                string tpl = File.Exists(tplPath) ? File.ReadAllText(tplPath, Encoding.UTF8) : FallbackHtml;
                string safe = BuildJson(game, version).Replace("</", "<\\/");

                Pending p = new Pending();
                p.Folder = Path.Combine(Path.Combine(dataDir, "Reports"), name);
                p.Names = new string[] { "report.html", "mods.csv", "timeline.csv", "spikes.csv" };
                p.Texts = new string[] { tpl.Replace(Placeholder, safe), BuildModsCsv(), BuildTimelineCsv(), BuildSpikesCsv() };
                p.Bom = new bool[] { false, true, true, true };
                return p;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                if (Profiler.LogSink != null) Profiler.LogSink(L.T("Не удалось собрать отчёт: ", "Could not build the report: ") + e.Message);
                return null;
            }
        }

        // Пишет собранные файлы. Можно вызывать из любого потока (ничего не логирует, ошибку кладёт в LastError).
        // Каждый файл пишется во временный и подменяется целиком, поэтому закрытие игры посреди записи не оставит обрезанный отчёт.
        public static bool Commit(Pending p)
        {
            lock (ioLock)
            {
                try
                {
                    Directory.CreateDirectory(p.Folder);
                    for (int i = 0; i < p.Names.Length; i++)
                    {
                        string path = Path.Combine(p.Folder, p.Names[i]);
                        string tmp = path + ".tmp";
                        File.WriteAllText(tmp, p.Texts[i], new UTF8Encoding(p.Bom[i]));
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmp, path);
                    }
                    LastError = "";
                    return true;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    return false;
                }
            }
        }

        // Пишет отчёт сразу, возвращает путь к папке (или null при ошибке).
        public static string Write(string dataDir, string game, string version, bool sessionFolder)
        {
            Pending p = Prepare(dataDir, game, version, sessionFolder);
            if (p == null) return null;
            if (Commit(p)) return p.Folder;
            if (Profiler.LogSink != null) Profiler.LogSink(L.T("Не удалось записать отчёт: ", "Could not write the report: ") + LastError);
            return null;
        }

        // Отчёт в новую папку-снимок.
        public static string Write(string dataDir, string game, string version)
        {
            return Write(dataDir, game, version, false);
        }

        // ---- вспомогательное ----
        static double Ms(long ticks) { return ticks * Profiler.TickMs; }

        // Текст закрытых секундных строк. Закрытая секунда больше не меняется, поэтому текст запоминаем:
        // при автосохранении в долгой сессии (до трёх часов, 10800 строк) пересчитывается только хвост, а не всё заново.
        sealed class RowCache
        {
            public StringBuilder Text = new StringBuilder();
            public int Count;          // сколько строк уже в Text
            public int FirstT = -1;    // номер секунды первой строки: если сдвинулся, старые строки удалили - кэш устарел
            public int Mods = -1;      // сколько модов было, когда строили: поменялось число столбцов - кэш устарел
        }
        static readonly RowCache jsonCache = new RowCache();
        static readonly RowCache csvCache = new RowCache();

        // Дописывает в sb строки по секундам (json=true: объекты через запятую, иначе строки CSV) + срез текущей незакрытой секунды.
        static void AppendTimeline(StringBuilder sb, RowCache c, bool json)
        {
            List<SecRow> closed = Profiler.Timeline;
            int n = closed.Count;
            if (c.Mods != Profiler.ModCount || c.Count > n || (n > 0 && c.Count > 0 && c.FirstT != closed[0].T))
            {
                c.Text.Length = 0;
                c.Count = 0;
                c.Mods = Profiler.ModCount;
            }
            if (n > 0 && c.Count == 0) c.FirstT = closed[0].T;
            for (int i = c.Count; i < n; i++) AppendRow(c.Text, closed[i], json, i == 0);
            c.Count = n;
            sb.Append(c.Text);
            SecRow cur = Profiler.PeekCurrentSecond();
            if (cur != null) AppendRow(sb, cur, json, n == 0);
        }

        static void AppendRow(StringBuilder sb, SecRow r, bool json, bool first)
        {
            if (json)
            {
                if (!first) sb.Append(',');
                sb.Append('{');
                Key(sb, "t"); Num(sb, r.T); sb.Append(',');
                Key(sb, "n"); Num(sb, r.Frames); sb.Append(',');
                Key(sb, "avgMs"); Num(sb, r.Frames > 0 ? r.SumMs / r.Frames : 0); sb.Append(',');
                Key(sb, "maxMs"); Num(sb, r.MaxMs); sb.Append(',');
                Key(sb, "mod"); sb.Append('[');
                for (int m = 0; m < Profiler.ModCount; m++)
                {
                    if (m > 0) sb.Append(',');
                    Num(sb, m < r.ModMs.Length ? r.ModMs[m] : 0);
                }
                sb.Append("]}");
            }
            else
            {
                double avg = r.Frames > 0 ? r.SumMs / r.Frames : 0;
                sb.Append(r.T).Append(',').Append(r.Frames).Append(',').Append(D(avg)).Append(',').Append(D(r.MaxMs)).Append(',')
                  .Append(D(avg > 0 ? 1000.0 / avg : 0)).Append(',').Append(D(r.SelfMs));
                for (int m = 1; m < Profiler.ModCount; m++) sb.Append(',').Append(D(m < r.ModMs.Length ? r.ModMs[m] : 0));
                sb.AppendLine();
            }
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '"') sb.Append("\\\"");
                    else if (c == '\\') sb.Append("\\\\");
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') sb.Append("\\r");
                    else if (c == '\t') sb.Append("\\t");
                    else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    else sb.Append(c);
                }
            sb.Append('"');
        }

        static void Num(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            sb.Append(v.ToString("0.###", Inv));
        }

        static void Key(StringBuilder sb, string k)
        {
            Str(sb, k);
            sb.Append(':');
        }

        static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\n', '\r', ';' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static string D(double v) { return v.ToString("0.####", Inv); }

        // id хуков DLL по убыванию суммарного времени
        static int[] SortedHooks(int mod)
        {
            List<int> ids = new List<int>();
            List<long> keys = new List<long>();
            for (int i = 1; i < Profiler.HookCount; i++)
                if (Profiler.HookMod[i] == mod && Profiler.HookTotalCalls[i] > 0)
                {
                    ids.Add(i);
                    keys.Add(-Profiler.HookTotalTicks[i]);
                }
            int[] a = ids.ToArray();
            long[] k = keys.ToArray();
            Array.Sort(k, a);
            return a;
        }

        static long ModCalls(int mod)
        {
            long c = 0;
            for (int i = 1; i < Profiler.HookCount; i++)
                if (Profiler.HookMod[i] == mod) c += Profiler.HookTotalCalls[i];
            return c;
        }

        static int[] ModOrder()
        {
            List<int> ids = new List<int>();
            List<long> keys = new List<long>();
            for (int m = 1; m < Profiler.ModCount; m++) { ids.Add(m); keys.Add(-Profiler.ModTotalTicks[m]); }
            int[] a = ids.ToArray();
            long[] k = keys.ToArray();
            Array.Sort(k, a);
            return a;
        }

        // ---- вердикт словами ----
        static string BuildVerdict(double modPct, double modMs, double avgMs, double selfPct)
        {
            if (Profiler.FramesTotal <= 0) return L.T("Данных нет: ни одного кадра не записано.", "No data: not a single frame was recorded.");
            StringBuilder v = new StringBuilder();
            v.Append(L.T("Моды заняли ", "Mods took ")).Append(modPct.ToString("0.0", Inv)).Append(L.T("% времени кадра (", "% of frame time ("))
             .Append(modMs.ToString("0.00", Inv)).Append(L.T(" мс из ", " ms out of ")).Append(avgMs.ToString("0.0", Inv)).Append(L.T(" мс в среднем). ", " ms on average). "));
            int[] order = ModOrder();
            if (order.Length > 0 && Profiler.ModTotalTicks[order[0]] > 0)
            {
                int top = order[0];
                double share = Profiler.ModAllTotalTicks > 0 ? Profiler.ModTotalTicks[top] * 100.0 / Profiler.ModAllTotalTicks : 0;
                v.Append(L.T("Тяжелее всех — «", "Heaviest: \"")).Append(Profiler.ModName[top]).Append(L.T("» (", "\" ("))
                 .Append((Ms(Profiler.ModTotalTicks[top]) / Profiler.FramesTotal).ToString("0.00", Inv)).Append(L.T(" мс/кадр, ", " ms/frame, "))
                 .Append(share.ToString("0", Inv)).Append(L.T("% от всех модов). ", "% of all mods). "));
            }
            // Оценка идёт и по доле кадра, и по миллисекундам: при 500 FPS кадр 2 мс, и 15% от него - это всего 0.3 мс, что не «сильная нагрузка».
            if (modPct < 5.0 || modMs < VerdictBarelyMs) v.Append(L.T("Моды почти не влияют на производительность: основная нагрузка — игра и видеокарта. ", "Mods barely affect performance: most of the load is the game and the GPU. "));
            else if (modPct < 15.0 || modMs < VerdictHeavyMs) v.Append(L.T("Моды заметно, но не критично влияют на кадр. ", "Mods have a noticeable but not critical effect on the frame. "));
            else v.Append(L.T("Моды сильно нагружают игру. ", "Mods put a heavy load on the game. "));
            int[] c = Profiler.SpikeCategoryCount;
            if (Profiler.SpikesTotal > 0)
            {
                v.Append(L.T("Лагов: ", "Spikes: ")).Append(Profiler.SpikesTotal).Append(L.T(" (по вине одного мода — ", " (one mod at fault: ")).Append(c[1])
                 .Append(L.T(", загрузка сцен — ", ", scene loads: ")).Append(c[2]).Append(L.T(", сборщик мусора — ", ", garbage collector: ")).Append(c[3])
                 .Append(L.T(", несколько модов вместе — ", ", several mods together: ")).Append(c[4]).Append(L.T(", не моды — ", ", not mods: ")).Append(c[5]).Append("). ");
            }
            else v.Append(L.T("Лагов не было. ", "There were no spikes. "));
            if (Profiler.StallsTotal > 0)
                v.Append(L.T("Остановок дольше секунды (загрузки): ", "Stalls longer than a second (loading): ")).Append(Profiler.StallsTotal).Append(L.T(", всего ", ", total "))
                 .Append((Profiler.StallsSumMs / 1000.0).ToString("0.0", Inv)).Append(L.T(" с — в FPS и 1% low они не учтены. ", " s - not counted in FPS or 1% low. "));
            if (Profiler.OrigTotalTicks > 0 && Ms(Profiler.OrigTotalTicks) / Profiler.FramesTotal >= 0.02)
                v.Append(L.T("Ещё ", "Another ")).Append((Ms(Profiler.OrigTotalTicks) / Profiler.FramesTotal).ToString("0.00", Inv))
                 .Append(L.T(" мс/кадр — код самой игры внутри хуков модов (вызовы оригинала из On.*): к нагрузке модов не отнесено. ",
                             " ms/frame is the game's own code running inside mod hooks (calls of the original from On.* hooks): it is not counted as mod load. "));
            v.Append(L.T("Сам Gaslight занимал ", "Gaslight itself took ")).Append(selfPct.ToString("0.0", Inv)).Append(L.T("% кадра.", "% of the frame."));
            return v.ToString();
        }

        // ---- JSON ----
        static string BuildJson(string game, string version)
        {
            long frames = Profiler.FramesTotal;
            double avgMs = frames > 0 ? Profiler.SumFrameMs / frames : 0;
            double sumTicks = Profiler.SumFrameTicks;
            double modPct = sumTicks > 0 ? Profiler.ModAllTotalTicks * 100.0 / sumTicks : 0;
            double selfPct = sumTicks > 0 ? Profiler.SelfTotalTicks * 100.0 / sumTicks : 0;
            double modMs = frames > 0 ? Ms(Profiler.ModAllTotalTicks) / frames : 0;
            double selfMs = frames > 0 ? Ms(Profiler.SelfTotalTicks) / frames : 0;

            StringBuilder sb = new StringBuilder(1 << 16);
            sb.Append('{');
            Key(sb, "game"); Str(sb, game); sb.Append(',');
            Key(sb, "version"); Str(sb, version); sb.Append(',');
            Key(sb, "lang"); Str(sb, L.Code); sb.Append(',');
            Key(sb, "generated"); Str(sb, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)); sb.Append(',');
            Key(sb, "durationSec"); Num(sb, Profiler.DurationSec()); sb.Append(',');
            Key(sb, "frames"); Num(sb, frames); sb.Append(',');
            Key(sb, "pausedFrames"); Num(sb, Profiler.PausedFrames); sb.Append(',');
            Key(sb, "avgFps"); Num(sb, Profiler.AvgFps()); sb.Append(',');
            Key(sb, "low1Fps"); Num(sb, Profiler.Low1Fps()); sb.Append(',');
            Key(sb, "avgMs"); Num(sb, avgMs); sb.Append(',');
            Key(sb, "worstMs"); Num(sb, Profiler.WorstFrameMs); sb.Append(',');
            Key(sb, "modMsPerFrame"); Num(sb, modMs); sb.Append(',');
            Key(sb, "modPct"); Num(sb, modPct); sb.Append(',');
            Key(sb, "selfMsPerFrame"); Num(sb, selfMs); sb.Append(',');
            Key(sb, "selfPct"); Num(sb, selfPct); sb.Append(',');
            Key(sb, "origMsPerFrame"); Num(sb, frames > 0 ? Ms(Profiler.OrigTotalTicks) / frames : 0); sb.Append(',');
            Key(sb, "restPct"); Num(sb, Math.Max(0, 100.0 - modPct - selfPct)); sb.Append(',');
            Key(sb, "pairCostNs"); Num(sb, Profiler.PairCostNs); sb.Append(',');
            Key(sb, "hookCount"); Num(sb, Profiler.HookCount - 1); sb.Append(',');
            Key(sb, "disabledHooks"); Num(sb, Profiler.DisabledHooks); sb.Append(',');
            Key(sb, "spikeMinMs"); Num(sb, Settings.SpikeMinMs); sb.Append(',');
            Key(sb, "verdict"); Str(sb, BuildVerdict(modPct, modMs, avgMs, selfPct)); sb.Append(',');

            // settings
            Key(sb, "settings"); sb.Append('{');
            List<SettingRow> rows = Settings.Rows();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Key(sb, rows[i].Name); Str(sb, Settings.ValueText(rows[i]));
            }
            sb.Append("},");

            // modNames
            Key(sb, "modNames"); sb.Append('[');
            for (int m = 0; m < Profiler.ModCount; m++) { if (m > 0) sb.Append(','); Str(sb, Profiler.ModName[m]); }
            sb.Append("],");

            // mods
            Key(sb, "mods"); sb.Append('[');
            int[] order = ModOrder();
            for (int i = 0; i < order.Length; i++)
            {
                int m = order[i];
                if (i > 0) sb.Append(',');
                double totalMs = Ms(Profiler.ModTotalTicks[m]);
                sb.Append('{');
                Key(sb, "idx"); Num(sb, m); sb.Append(',');
                Key(sb, "name"); Str(sb, Profiler.ModName[m]); sb.Append(',');
                Key(sb, "totalMs"); Num(sb, totalMs); sb.Append(',');
                Key(sb, "msPerFrame"); Num(sb, frames > 0 ? totalMs / frames : 0); sb.Append(',');
                Key(sb, "pct"); Num(sb, sumTicks > 0 ? Profiler.ModTotalTicks[m] * 100.0 / sumTicks : 0); sb.Append(',');
                Key(sb, "maxFrameMs"); Num(sb, Ms(Profiler.ModMaxTicks[m])); sb.Append(',');
                Key(sb, "calls"); Num(sb, ModCalls(m)); sb.Append(',');
                Key(sb, "hooks"); sb.Append('[');
                int[] hs = SortedHooks(m);
                int cnt = Math.Min(hs.Length, MaxHooksPerModInJson);
                for (int k = 0; k < cnt; k++)
                {
                    int h = hs[k];
                    if (k > 0) sb.Append(',');
                    double hm = Ms(Profiler.HookTotalTicks[h]);
                    sb.Append('{');
                    Key(sb, "name"); Str(sb, Profiler.HookName[h]); sb.Append(',');
                    Key(sb, "calls"); Num(sb, Profiler.HookTotalCalls[h]); sb.Append(',');
                    Key(sb, "totalMs"); Num(sb, hm); sb.Append(',');
                    Key(sb, "msPerFrame"); Num(sb, frames > 0 ? hm / frames : 0); sb.Append(',');
                    Key(sb, "maxMs"); Num(sb, Ms(Profiler.HookMaxTicks[h])); sb.Append(',');
                    Key(sb, "off"); sb.Append(Profiler.HookDisabled[h] ? "true" : "false");
                    sb.Append('}');
                }
                sb.Append("],");
                Key(sb, "hooksTotal"); Num(sb, hs.Length);
                sb.Append('}');
            }
            sb.Append("],");

            // timeline
            Key(sb, "stalls"); Num(sb, Profiler.StallsTotal); sb.Append(',');
            Key(sb, "stallsSumMs"); Num(sb, Profiler.StallsSumMs); sb.Append(',');
            Key(sb, "timeline"); sb.Append('[');
            AppendTimeline(sb, jsonCache, true);
            sb.Append("],");

            // spikes
            Key(sb, "spikeTotal"); Num(sb, Profiler.SpikesTotal); sb.Append(',');
            Key(sb, "spikeCategories"); sb.Append('[');
            for (int i = 1; i <= 5; i++) { if (i > 1) sb.Append(','); Num(sb, Profiler.SpikeCategoryCount[i]); }
            sb.Append("],");
            Key(sb, "spikes"); sb.Append('[');
            List<SpikeInfo> sp = PickSpikes();
            for (int i = 0; i < sp.Count; i++)
            {
                SpikeInfo s = sp[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Key(sb, "t"); Num(sb, s.TimeSec); sb.Append(',');
                Key(sb, "ms"); Num(sb, s.FrameMs); sb.Append(',');
                Key(sb, "scene"); Str(sb, s.Scene); sb.Append(',');
                Key(sb, "gc"); Num(sb, s.GcDelta); sb.Append(',');
                Key(sb, "cat"); Num(sb, s.Category); sb.Append(',');
                Key(sb, "verdict"); Str(sb, s.Verdict); sb.Append(',');
                Key(sb, "culprit"); Str(sb, s.Culprit); sb.Append(',');
                Key(sb, "methods"); Str(sb, s.TopMethods); sb.Append(',');
                Key(sb, "mods"); Str(sb, s.TopMods);
                sb.Append('}');
            }
            sb.Append("],");

            // scenes
            Key(sb, "scenes"); sb.Append('[');
            for (int i = 0; i < Profiler.Scenes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Key(sb, "t"); Num(sb, Profiler.Scenes[i].TimeSec); sb.Append(',');
                Key(sb, "name"); Str(sb, Profiler.Scenes[i].Name);
                sb.Append('}');
            }
            sb.Append("],");

            // notes
            Key(sb, "notes"); sb.Append('[');
            for (int i = 0; i < Profiler.Notes.Count; i++) { if (i > 0) sb.Append(','); Str(sb, Profiler.Notes[i]); }
            sb.Append("]}");
            return sb.ToString();
        }

        // Если лагов больше лимита - берём самые длинные, потом по времени.
        static List<SpikeInfo> PickSpikes()
        {
            List<SpikeInfo> l = new List<SpikeInfo>(Profiler.Spikes);
            int max = Settings.MaxSpikesInReport;
            if (l.Count > max)
            {
                l.Sort(delegate(SpikeInfo a, SpikeInfo b) { return b.FrameMs.CompareTo(a.FrameMs); });
                l.RemoveRange(max, l.Count - max);
                l.Sort(delegate(SpikeInfo a, SpikeInfo b) { return a.TimeSec.CompareTo(b.TimeSec); });
            }
            return l;
        }

        // ---- CSV ----
        static string BuildModsCsv()
        {
            long frames = Math.Max(1, Profiler.FramesTotal);
            double sumTicks = Math.Max(1, Profiler.SumFrameTicks);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("level,dll,name,calls,total_ms,ms_per_frame,pct_of_frame,max_frame_ms");
            sb.AppendLine("dll,Gaslight," + L.T("Gaslight (сам мод)", "Gaslight (the mod itself)") + ",," + D(Ms(Profiler.SelfTotalTicks)) + "," + D(Ms(Profiler.SelfTotalTicks) / frames)
                + "," + D(Profiler.SelfTotalTicks * 100.0 / sumTicks) + ",");
            int[] order = ModOrder();
            for (int i = 0; i < order.Length; i++)
            {
                int m = order[i];
                double tm = Ms(Profiler.ModTotalTicks[m]);
                sb.AppendLine("dll," + Csv(Profiler.ModName[m]) + "," + Csv(Profiler.ModName[m]) + "," + ModCalls(m) + "," + D(tm) + "," + D(tm / frames)
                    + "," + D(Profiler.ModTotalTicks[m] * 100.0 / sumTicks) + "," + D(Ms(Profiler.ModMaxTicks[m])));
                int[] hs = SortedHooks(m);
                for (int k = 0; k < hs.Length; k++)
                {
                    int h = hs[k];
                    double hm = Ms(Profiler.HookTotalTicks[h]);
                    sb.AppendLine("method," + Csv(Profiler.ModName[m]) + "," + Csv(Profiler.HookName[h]) + "," + Profiler.HookTotalCalls[h] + "," + D(hm)
                        + "," + D(hm / frames) + "," + D(Profiler.HookTotalTicks[h] * 100.0 / sumTicks) + "," + D(Ms(Profiler.HookMaxTicks[h])));
                }
            }
            return sb.ToString();
        }

        static string BuildTimelineCsv()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("t_sec,frames,avg_ms,max_ms,avg_fps,self_ms");
            for (int m = 1; m < Profiler.ModCount; m++) sb.Append(',').Append(Csv(Profiler.ModName[m]));
            sb.AppendLine();
            AppendTimeline(sb, csvCache, false);
            return sb.ToString();
        }

        static string BuildSpikesCsv()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("t_sec,frame_ms,scene,gc,verdict,culprit,top_methods,top_mods");
            List<SpikeInfo> sp = PickSpikes();
            for (int i = 0; i < sp.Count; i++)
            {
                SpikeInfo s = sp[i];
                sb.AppendLine(D(s.TimeSec) + "," + D(s.FrameMs) + "," + Csv(s.Scene) + "," + s.GcDelta + "," + Csv(s.Verdict) + ","
                    + Csv(s.Culprit) + "," + Csv(s.TopMethods) + "," + Csv(s.TopMods));
            }
            return sb.ToString();
        }
    }
}
