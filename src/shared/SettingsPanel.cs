//  SettingsPanel.cs - экранная панель настроек (клавиша PageUp; работает и там, где нет меню модов).
//
//  Что это:  логика панели: список строк, выбор, смена значения, захват клавиши.
//            Рисует панель Driver.OnGUI (App.cs), а клавиши сюда передаёт Driver.
//  Управление:  стрелки вверх/вниз - выбрать строку, влево/вправо - изменить
//            значение, Enter на строке с клавишей - «нажми новую клавишу»,
//            Esc - отмена / закрыть панель.
//  Предупреждение:  игра не блокирует свои клавиши, пока панель открыта, поэтому
//            открывать её лучше на паузе или в меню.
//  Файл не зависит от Unity - проверяется без игры.
using System;
using System.Collections.Generic;
using System.Text;

namespace Gaslight
{
    public sealed class SettingsPanel
    {
        const int VisibleRows = 12;     // сколько строк видно одновременно

        public bool Open;
        public bool Capturing;          // ждём нажатия новой клавиши
        public int Selected;
        int first;                      // первая видимая строка
        List<SettingRow> rows;
        string[] text = new string[VisibleRows + 4];
        int textCount;
        bool dirty = true;

        public void Toggle()
        {
            Open = !Open;
            Capturing = false;
            if (Open) { rows = Settings.Rows(); dirty = true; }
        }

        // Вызывается каждый кадр, пока панель открыта. keyDown(имя) - нажата ли клавиша.
        public void Update(Func<string, bool> keyDown)
        {
            if (!Open) return;
            if (Capturing)
            {
                if (keyDown("Escape")) { Capturing = false; dirty = true; return; }
                for (int i = 0; i < KeyNames.Allowed.Length; i++)
                {
                    string n = KeyNames.Allowed[i];
                    if (n == "None") continue;
                    if (keyDown(n)) { Capture(n); return; }
                }
                return;
            }
            if (keyDown("Escape")) { Open = false; return; }
            if (keyDown("UpArrow")) Move(-1);
            else if (keyDown("DownArrow")) Move(1);
            else if (keyDown("LeftArrow")) Change(-1);
            else if (keyDown("RightArrow")) Change(1);
            else if (keyDown("Return") || keyDown("KeypadEnter")) Activate();
        }

        public void Move(int d)
        {
            Selected += d;
            if (Selected < 0) Selected = rows.Count - 1;
            if (Selected >= rows.Count) Selected = 0;
            if (Selected < first) first = Selected;
            if (Selected >= first + VisibleRows) first = Selected - VisibleRows + 1;
            dirty = true;
        }

        public void Change(int d)
        {
            SettingRow r = rows[Selected];
            Settings.SetIndex(r, Settings.GetIndex(r) + d);
            if (r.Name == "Language") rows = Settings.Rows();   // подписи значений зависят от языка
            dirty = true;
        }

        public void Activate()
        {
            SettingRow r = rows[Selected];
            if (r.Info.IsKey) { Capturing = true; dirty = true; }
            else Change(1);
        }

        public void Capture(string keyName)
        {
            SettingRow r = rows[Selected];
            int idx = KeyNames.IndexOf(keyName);
            if (idx >= 0) Settings.SetIndex(r, idx);
            Capturing = false;
            dirty = true;
        }

        // Строки для рисования (пересобираются только при изменении).
        public string[] Lines(out int count)
        {
            if (dirty) Rebuild();
            count = textCount;
            return text;
        }

        void Rebuild()
        {
            dirty = false;
            int n = 0;
            text[n++] = L.T("Gaslight — настройки      ↑↓ выбрать   ←→ изменить   Enter — назначить клавишу   Esc — закрыть",
                            "Gaslight settings      Up/Down select   Left/Right change   Enter = pick key   Esc = close");
            for (int i = first; i < rows.Count && i < first + VisibleRows; i++)
            {
                SettingRow r = rows[i];
                string val = Settings.ValueText(r);
                if (i == Selected && Capturing) val = L.T("... нажми новую клавишу (Esc — отмена)", "... press the new key (Esc to cancel)");
                text[n++] = (i == Selected ? "> " : "   ") + r.Name + ":  " + val;
            }
            text[n++] = rows.Count > 0 ? rows[Selected].Text : "";
            textCount = n;
        }
    }
}
