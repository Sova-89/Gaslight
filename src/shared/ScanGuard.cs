//  ScanGuard.cs - «чёрный ящик» для поиска методов и установки замеров.
//
//  Зачем:  чужие моды бывают собраны под старую версию игры. Если спросить у Mono
//          про такой метод, Mono иногда падает целиком (Access Violation) - поймать
//          это через try/catch нельзя. Поэтому перед каждым «опасным» шагом мы
//          записываем в маленький файл scan_guard.txt, что сейчас делаем.
//
//          Если игра упала, файл так и останется записанным. При следующем запуске
//          Gaslight прочитает его, поймёт, на чём упали, и запишет это место в
//          quarantine.txt. Дальше этот тип/метод просто пропускается (не замеряется).
//          Игре и модам это никак не мешает - они работают как обычно.
//
//  Если игру закрыли обычным способом - файл очищается, ложных срабатываний нет.
//
//  Что менять:  BadPerDllLimit - сколько разных «плохих мест» в одной DLL, после
//               которых DLL пропускается целиком.
//               Хочешь вернуть всё как было - удали quarantine.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gaslight
{
    public static class ScanGuard
    {
        const int RecordBytes = 512;       // размер записи в файле (фиксированный - так писать дёшево)
        const int MaxKeyChars = 110;       // длиннее - заменяем «хвост» на хэш
        const int BadPerDllLimit = 3;      // 3 плохих места в одной DLL -> пропускаем всю DLL

        static FileStream mark;            // файл-«черновик», открыт всё время
        static readonly byte[] buf = new byte[RecordBytes];
        static string quarantinePath;
        static Action<string> log;
        static readonly HashSet<string> bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> badPerDll = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static int QuarantinedCount { get { return bad.Count; } }

        // Вызвать один раз перед началом поиска. dir == null - защита выключена.
        public static void Init(string dir, Action<string> logger)
        {
            log = logger;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                quarantinePath = Path.Combine(dir, "quarantine.txt");
                string markPath = Path.Combine(dir, "scan_guard.txt");

                LoadQuarantine();

                // что было записано в прошлый раз и не было стёрто?
                string left = "";
                try { if (File.Exists(markPath)) left = File.ReadAllText(markPath, Encoding.UTF8).Replace("\0", "").Trim(); }
                catch (Exception) { }
                if (left.Length > 0)
                {
                    AddBad(left);
                    Note(L.T("Прошлый запуск оборвался на шаге «", "The previous run was cut short at step \"") + left + L.T("». Это место больше не замеряется (quarantine.txt).", "\". This spot is no longer measured (quarantine.txt)."));
                }

                mark = new FileStream(markPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                ClearMark();
            }
            catch (Exception e)
            {
                mark = null;
                Note(L.T("ScanGuard выключен: ", "ScanGuard is off: ") + e.Message);
            }
        }

        // Ключи:  "Мод.dll|Тип.Полное.Имя"   - тип
        //         "hook:Мод.dll|Тип.Метод"   - установка замера в метод
        //         "Мод.dll|<GetTypes>"       - чтение списка типов DLL
        //         "Мод.dll|*"                - вся DLL
        public static bool IsBad(string key)
        {
            if (bad.Count == 0) return false;
            if (bad.Contains(Norm(key))) return true;
            return IsDllBad(DllOf(key));
        }

        public static bool IsDllBad(string dll)
        {
            return bad.Count > 0 && bad.Contains(dll + "|*");
        }

        // Записать «сейчас делаю вот это». Дёшево: одна запись фиксированного размера.
        public static void Mark(string key)
        {
            if (mark == null) return;
            try
            {
                Array.Clear(buf, 0, buf.Length);
                string k = Norm(key);
                Encoding.UTF8.GetBytes(k, 0, k.Length, buf, 0);
                mark.Position = 0;
                mark.Write(buf, 0, buf.Length);
                mark.Flush();
            }
            catch (Exception) { mark = null; }       // не получилось писать - просто работаем без защиты
        }

        // «Всё прошло нормально, ничего не делаю».
        public static void ClearMark()
        {
            if (mark == null) return;
            try
            {
                Array.Clear(buf, 0, buf.Length);
                mark.Position = 0;
                mark.Write(buf, 0, buf.Length);
                mark.Flush();
            }
            catch (Exception) { mark = null; }
        }

        // ---- внутреннее ----

        static string Norm(string key)
        {
            if (key.Length <= MaxKeyChars) return key;
            return key.Substring(0, MaxKeyChars - 9) + "#" + key.GetHashCode().ToString("x8");
        }

        static string DllOf(string key)
        {
            string k = key.StartsWith("hook:", StringComparison.Ordinal) ? key.Substring(5) : key;
            int bar = k.IndexOf('|');
            return bar < 0 ? k : k.Substring(0, bar);
        }

        static void AddBad(string key)
        {
            key = Norm(key);
            if (!bad.Add(key)) return;
            string dll = DllOf(key);
            int n;
            badPerDll.TryGetValue(dll, out n);
            badPerDll[dll] = ++n;
            AppendLine(key + "    # " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + L.T(" игра оборвалась на этом шаге", " the game was cut short at this step"));
            if (n >= BadPerDllLimit && !key.EndsWith("|*", StringComparison.Ordinal))
            {
                string all = dll + "|*";
                if (bad.Add(all))
                {
                    AppendLine(all + L.T("    # слишком много плохих мест — DLL пропускается целиком", "    # too many bad spots - the whole DLL is skipped"));
                    Note(L.T("DLL " + dll + " пропускается целиком: на ней игра падала уже " + n + " раза.", "DLL " + dll + " is skipped entirely: the game has already crashed on it " + n + " time(s)."));
                }
            }
        }

        static void LoadQuarantine()
        {
            if (quarantinePath == null || !File.Exists(quarantinePath)) return;
            string[] lines = File.ReadAllLines(quarantinePath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i];
                int hash = s.IndexOf("    #", StringComparison.Ordinal);
                if (hash >= 0) s = s.Substring(0, hash);
                s = s.Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                bad.Add(s);
                string dll = DllOf(s);
                int n;
                badPerDll.TryGetValue(dll, out n);
                badPerDll[dll] = n + 1;
            }
        }

        static void AppendLine(string line)
        {
            try
            {
                if (quarantinePath == null) return;
                if (!File.Exists(quarantinePath))
                    File.WriteAllText(quarantinePath,
                        L.T("# Места, где игра упала при поиске методов. Gaslight их больше не замеряет.\r\n" +
                            "# Игре и модам это не мешает. Чтобы вернуть всё как было — удали этот файл.\r\n",
                            "# Spots where the game crashed while searching for methods. Gaslight no longer measures them.\r\n" +
                            "# This does not affect the game or other mods. To undo it, delete this file.\r\n"), new UTF8Encoding(true));
                File.AppendAllText(quarantinePath, line + "\r\n", Encoding.UTF8);
            }
            catch (Exception) { }
        }

        static void Note(string s) { if (log != null) log(s); }
    }
}
