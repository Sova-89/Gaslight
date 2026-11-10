//  GaslightMod.cs - адаптер для Hollow Knight (Modding API).
//  Mod + IMenuMod: настройки видны в Options -> Mods -> Gaslight (стрелками).
//  Моды для замера: все DLL под Managed\Mods\ (кроме игнор-списка Hooker).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Modding;
using UnityEngine;

namespace Gaslight
{
    public sealed class GaslightMod : Mod, IMenuMod, IHost
    {
        public GaslightMod() : base("Gaslight") { }
        public override string GetVersion() { return App.Version + ".0"; }   // Lumafly ждёт четыре числа: 1.1.0.0

        public string DataDir { get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } }
        public string GameName { get { return "Hollow Knight"; } }

        public override void Initialize()
        {
            try { App.Start(this); }
            catch (Exception e) { Log(L.T("Gaslight: ошибка запуска: ", "Gaslight: startup error: ") + e); }
        }

        void IHost.Log(string s) { Log(s); }
        public bool KeyDown(KeyCode k) { return Input.GetKeyDown(k); }

        public List<ModAssembly> FindModAssemblies()
        {
            List<ModAssembly> l = new List<ModAssembly>();
            string modsDir = Path.GetFullPath(Path.Combine(DataDir, ".."));   // Managed\Mods
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                string loc = "";
                try { loc = all[i].Location; } catch (Exception) { }
                if (loc == null || loc.Length == 0) continue;
                if (!Path.GetFullPath(loc).StartsWith(modsDir, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(loc);
                if (Hooker.IsIgnored(name)) continue;
                ModAssembly m = new ModAssembly();
                m.Asm = all[i];
                m.Name = name;
                l.Add(m);
            }
            return l;
        }

        // ---- меню Options -> Mods -> Gaslight
        public bool ToggleButtonInsideMenu { get { return false; } }

        public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggleButtonEntry)
        {
            List<IMenuMod.MenuEntry> l = new List<IMenuMod.MenuEntry>();
            List<SettingRow> rows = Settings.Rows();
            for (int i = 0; i < rows.Count; i++)
            {
                SettingRow r = rows[i];
                if (r.Info.Advanced) continue;
                if (r.Info.IsKey)
                    l.Add(new IMenuMod.MenuEntry(r.Name, KeyValues(r), r.Text + L.T(" (стрелка → «нажми клавишу…» → нажми нужную)", " (arrow -> \"press a key...\" -> press the key you want)"), MakeKeySaver(r), MakeKeyLoader(r)));
                else
                    l.Add(new IMenuMod.MenuEntry(r.Name, r.Values, r.Text, MakeSaver(r), MakeLoader(r)));
            }
            return l;
        }

        // Для клавиш списка нет: строка показывает текущую клавишу, любое нажатие стрелки
        // переводит её в режим «нажми клавишу…», и следующая нажатая кнопка записывается.

        static string[] KeyValues(SettingRow r)
        {
            return new string[] { Settings.ValueText(r), L.T("нажми клавишу…", "press a key...") };
        }

        static Action<int> MakeKeySaver(SettingRow r)
        {
            return delegate(int idx)
            {
                if (idx == 1 && App.Drv != null) App.Drv.BeginCapture(r);
            };
        }

        static Func<int> MakeKeyLoader(SettingRow r) { return delegate() { return 0; }; }

        static Action<int> MakeSaver(SettingRow r) { return delegate(int idx) { Settings.SetIndex(r, idx); }; }
        static Func<int> MakeLoader(SettingRow r) { return delegate() { return Settings.GetIndex(r); }; }
    }
}
