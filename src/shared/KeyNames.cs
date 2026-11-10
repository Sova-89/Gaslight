//  KeyNames.cs - список клавиш, которые можно назначить в меню/панели.
//
//  Что это:  имена клавиш в том виде, как их понимает Unity (enum KeyCode).
//  Что менять:  чтобы разрешить ещё клавишу - допиши её имя в BuildAllowed().
//            "None" = клавиша выключена.
//  Файл не зависит от Unity, поэтому проверяется без игры.
using System;
using System.Collections.Generic;

namespace Gaslight
{
    public static class KeyNames
    {
        public static readonly string[] Allowed = BuildAllowed();

        static string[] BuildAllowed()
        {
            List<string> l = new List<string>();
            l.Add("None");
            for (int i = 1; i <= 12; i++) l.Add("F" + i);
            l.Add("Insert"); l.Add("Home"); l.Add("End");
            l.Add("PageUp"); l.Add("PageDown"); l.Add("Delete");
            l.Add("Pause"); l.Add("ScrollLock");
            for (int i = 0; i <= 9; i++) l.Add("Keypad" + i);
            l.Add("KeypadPlus"); l.Add("KeypadMinus"); l.Add("KeypadMultiply");
            l.Add("KeypadDivide"); l.Add("KeypadPeriod"); l.Add("KeypadEnter");
            // буквы и цифры (имена Unity: A..Z, Alpha0..Alpha9)
            for (char c = 'A'; c <= 'Z'; c++) l.Add(c.ToString());
            for (int i = 0; i <= 9; i++) l.Add("Alpha" + i);
            l.Add("BackQuote"); l.Add("Minus"); l.Add("Equals"); l.Add("LeftBracket"); l.Add("RightBracket");
            l.Add("Backslash"); l.Add("Semicolon"); l.Add("Quote"); l.Add("Comma"); l.Add("Period"); l.Add("Slash");
            return l.ToArray();
        }

        public static bool IsAllowed(string name)
        {
            return IndexOf(name) >= 0;
        }

        public static int IndexOf(string name)
        {
            if (name == null) return -1;
            for (int i = 0; i < Allowed.Length; i++)
                if (string.Equals(Allowed[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
