//  Hooker.cs - вставляет секундомеры в точки входа модов через MonoMod ILHook.
//
//  Вставка IL (одинаковая в обеих играх):
//    в начало:            ldc.i4 <id>; call Profiler.Enter
//    каждый ret на месте: превращается в ldc.i4 <id>, после него call Profiler.Leave и новый ret
//    (так все переходы на старый ret автоматически попадают на Leave).
//  Исключение пропускает Leave - Leave умеет разматывать стек, а на границе кадра стек обнуляется.
//  Мод ничего не меняет в поведении метода: секундомер только смотрит.
//
//  Как идёт работа (ничего не отбрасывается, меняется только скорость):
//    после задержки HookDelaySec мы по чуть-чуть, в рамках бюджета на кадр (StepBudgetPercent % кадра, не больше StepBudgetMs),
//    читаем DLL каждого мода (IlScanner), находим точки входа и ставим секундомеры.
//    Сначала берём маленькие DLL, потом большие - чтобы большинство модов
//    начало замеряться как можно раньше.
//
//  Защита от падения: перед каждым опасным шагом пишем в ScanGuard, что делаем.
//  Если игра всё-таки упала, в следующий раз это место пропускается (quarantine.txt).
//
//  Что менять:  игнор-список сборок - IgnorePrefixes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Mono.Cecil.Cil;

namespace Gaslight
{
    public sealed class ModAssembly
    {
        public Assembly Asm;
        public string Name;     // имя для отчёта (имя файла DLL)
    }

    public static class Hooker
    {
        // Сборки с такими началами имени не трогаем (библиотеки, сама игра, наш мод).
        public static readonly string[] IgnorePrefixes = new string[] {
            "0Harmony", "MonoMod", "Mono.Cecil", "Newtonsoft", "System", "Unity", "Microsoft", "MMHOOK", "Gaslight",
            "Modding", "BepInEx", "Assembly-CSharp", "mscorlib", "netstandard", "PlayMaker", "TeamCherry", "HarmonyX", "Mono.",
            "GalaxyCSharp", "com.rlabrecque", "Silksong.", "Facepunch", "Steamworks", "I18N", "ICSharpCode", "InControl", "Rewired", "Ionic." };

        // Предохранитель от бесконечного списка у одной DLL (обычный мод - десятки хуков).
        const int HardCapPerMod = 5000;

        static readonly List<ILHook> hooks = new List<ILHook>();     // держим ссылки, иначе GC снимет хуки
        static MethodInfo enterMi, leaveMi;

        // --- очередь работы
        static List<ModAssembly> queue = new List<ModAssembly>();
        static int asmIndex;
        static Type[] types;
        static int typeIndex;
        static int curMod = -1;
        static string curName = "";
        static int curModHooks;
        static bool curFull;
        static Mono.Cecil.ModuleDefinition curModule;       // DLL, прочитанная с диска (Cecil)
        static Module curRt;                                // та же DLL в Mono (нужна, чтобы получить метод по токену)
        static List<KeyValuePair<MethodBase, string>> pending = new List<KeyValuePair<MethodBase, string>>();
        static int pendIndex;
        static Dictionary<MethodBase, string> found = new Dictionary<MethodBase, string>();

        // --- контроль нагрузки самого мода при установке
        static double allowanceMs;      // сколько мс можно потратить в этом кадре (уходит в минус после тяжёлого хука)
        static double limitMs;          // лимит текущего шага
        static double workMs;           // всего потрачено мс на поиск и установку
        static long beginTicks;         // когда начали

        public static int Hooked;
        public static int Failed;
        public static int SkippedLimit;
        public static int Scanned;
        public static int Quarantined;
        public static bool Done;
        public static string LastError = "";
        public static Action<string> Log;

        public static void Begin(List<ModAssembly> mods) { Begin(mods, null); }

        public static void Begin(List<ModAssembly> mods, string dataDir)
        {
            enterMi = typeof(Profiler).GetMethod("Enter");
            leaveMi = typeof(Profiler).GetMethod("Leave");
            ScanGuard.Init(dataDir, Log);
            beginTicks = Stopwatch.GetTimestamp();
            allowanceMs = 0; workMs = 0;
            SortBySize(mods);
            queue = mods;
            asmIndex = -1;
            types = null;
            Done = mods.Count == 0;
            if (Done) ScanGuard.ClearMark();
        }

        public static bool IsIgnored(string name)
        {
            for (int i = 0; i < IgnorePrefixes.Length; i++)
                if (name.StartsWith(IgnorePrefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Один шаг работы: вызывается из Driver раз в кадр. Возвращает true, если ещё есть работа.
        //
        // Бюджет - это СРЕДНЕЕ время на кадр, а не потолок одного кадра: один хук
        // ставится целиком и может занять 2–3 мс (JIT), поэтому лишнее записывается в «долг»,
        // и следующие кадры мод отдыхает. В среднем за секунду мод не тратит больше бюджета.
        // Сам бюджет = StepBudgetPercent % от обычной длины кадра, но не больше StepBudgetMs:
        // так при 300 fps (кадр 3 мс) установка берёт ~0,5 мс, а не фиксированные 1,5 мс (половину кадра).
        public static bool Step()
        {
            if (Done) return false;
            double budget = Settings.StepBudgetMs;
            double frame = Profiler.TypicalFrameMs;
            if (frame > 0) budget = Math.Min(budget, frame * Settings.StepBudgetPercent / 100.0);
            budget = Math.Max(0.1, budget);
            allowanceMs += budget;
            if (allowanceMs > budget * 3) allowanceMs = budget * 3;      // после долгой загрузки запас не копим
            if (allowanceMs <= 0) return true;                           // прошлые кадры потратили больше - отдыхаем

            long t0 = Stopwatch.GetTimestamp();
            limitMs = allowanceMs;
            bool more = DoStep(t0);
            double spent = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            allowanceMs -= spent;
            workMs += spent;
            return more;
        }

        static bool DoStep(long t0)
        {
            // 1) сначала ставим накопленные хуки
            if (pendIndex < pending.Count)
            {
                int n = 0;
                while (pendIndex < pending.Count && n < Settings.HooksPerFrame)
                {
                    KeyValuePair<MethodBase, string> kv = pending[pendIndex++];
                    TryHook(kv.Key, kv.Value);
                    n++;
                    if (OverBudget(t0)) break;
                }
                return true;
            }

            // 2) следующая DLL
            if (types == null || typeIndex >= types.Length)
            {
                if (!NextAssembly()) { Finish(); return false; }
                return true;
            }

            // 3) сканируем типы текущей DLL
            found.Clear();
            int c = 0;
            while (typeIndex < types.Length && c < Settings.ScanTypesPerFrame)
            {
                ScanOneType(types[typeIndex]);
                typeIndex++; c++; Scanned++;
                if (OverBudget(t0)) break;
            }
            pending.Clear();
            pendIndex = 0;
            foreach (KeyValuePair<MethodBase, string> kv in found) pending.Add(kv);
            return true;
        }

        // Выделенное на кадр время вышло? (замеры при этом не отбрасываются - продолжим в следующем кадре)
        static bool OverBudget(long startTicks)
        {
            double ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            return ms >= limitMs;
        }

        static void Note(string s) { if (Log != null) Log(s); }

        // Маленькие DLL первыми: так больше модов начнёт замеряться раньше.
        static void SortBySize(List<ModAssembly> mods)
        {
            Dictionary<ModAssembly, long> size = new Dictionary<ModAssembly, long>();
            for (int i = 0; i < mods.Count; i++)
            {
                long len = 0;
                try { len = new System.IO.FileInfo(mods[i].Asm.Location).Length; } catch (Exception) { }
                size[mods[i]] = len;
            }
            mods.Sort(delegate (ModAssembly a, ModAssembly b) { return size[a].CompareTo(size[b]); });
        }

        // Переходит к следующей DLL: открывает её файл и читает список типов. false - DLL закончились.
        static bool NextAssembly()
        {
            if (curName.Length > 0) Note("  " + curName + L.T(": замеров ", ": measurements ") + curModHooks);
            CloseModule();
            asmIndex++;
            if (asmIndex >= queue.Count) return false;

            ModAssembly ma = queue[asmIndex];
            curName = ma.Name;
            curMod = Profiler.RegisterMod(ma.Name);       // DLL попадёт в отчёт даже если замеров в ней нет
            curModHooks = 0;
            typeIndex = 0;
            types = new Type[0];
            curFull = !string.Equals(Settings.HookScope, "light", StringComparison.OrdinalIgnoreCase);

            if (curMod < 0) { Note(L.T("Слишком много DLL: " + ma.Name + " не поместилась в таблицу", "Too many DLLs: " + ma.Name + " did not fit in the table")); curName = ""; return true; }
            if (ScanGuard.IsDllBad(ma.Name)) { Note(ma.Name + L.T(": пропущена (в прошлый раз игра падала на ней, см. quarantine.txt)", ": skipped (the game crashed on it last time, see quarantine.txt)")); return true; }

            string err;
            ScanGuard.Mark(ma.Name + "|<open>");
            curModule = IlScanner.OpenModule(ma.Asm, out err);
            if (curModule == null) { Note(ma.Name + L.T(": не удалось прочитать файл (", ": could not read the file (") + err + ")"); return true; }
            curRt = ma.Asm.ManifestModule;

            ScanGuard.Mark(ma.Name + "|<GetTypes>");
            try { types = ma.Asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }       // часть типов не загрузилась - работаем с остальными
            catch (Exception e) { Note(ma.Name + L.T(": не удалось получить список типов: ", ": could not get the type list: ") + e.Message); }
            if (types == null) types = new Type[0];
            return true;
        }

        static void CloseModule()
        {
            if (curModule != null)
            {
                try { curModule.Dispose(); } catch (Exception) { }
            }
            curModule = null;
            curRt = null;
            IlScanner.CloseRaw();
        }

        static void Finish()
        {
            Done = true;
            ScanGuard.ClearMark();
            double wall = (Stopwatch.GetTimestamp() - beginTicks) / (double)Stopwatch.Frequency;
            Note(L.T("Установка замеров: " + wall.ToString("0.0") + " с, из них работа Gaslight " + (workMs / 1000.0).ToString("0.0") + " с процессора (растянуто по кадрам, бюджет " + Settings.StepBudgetPercent.ToString("0") + "% кадра, не больше " + Settings.StepBudgetMs.ToString("0.0") + " мс).",
                "Installing measurements took " + wall.ToString("0.0") + " s, of which Gaslight used " + (workMs / 1000.0).ToString("0.0") + " s of CPU (spread over frames, budget " + Settings.StepBudgetPercent.ToString("0") + "% of the frame, at most " + Settings.StepBudgetMs.ToString("0.0") + " ms)."));
            if (IlScanner.SkippedBadSignature > 0)
                Note(L.T("Не замерено " + IlScanner.SkippedBadSignature + " методов: в их сигнатурах нет типов игры (мод собран под другую версию), запуститься они не могут.",
                    IlScanner.SkippedBadSignature + " methods were not measured: their signatures use types that do not exist in the game (the mod was built for another version), so they cannot run."));
        }

        static void ScanOneType(Type t)
        {
            if (!IlScanner.IsCandidateType(t)) return;
            try
            {
                string key = curName + "|" + t.FullName;
                if (ScanGuard.IsBad(key)) { Quarantined++; return; }
                ScanGuard.Mark(key);
                IlScanner.ScanType(t, curModule, curRt, curFull, found);
            }
            catch (Exception e) { LastError = e.GetType().Name + ": " + e.Message; }
        }

        static void TryHook(MethodBase m, string name)
        {
            if (Profiler.HookCount >= Profiler.MaxHooks || curModHooks >= HardCapPerMod) { SkippedLimit++; return; }
            string key = "hook:" + curName + "|" + name;
            if (ScanGuard.IsBad(key)) { Quarantined++; return; }
            int id = Profiler.RegisterHook(curMod, name);
            if (id < 0) { SkippedLimit++; return; }
            try
            {
                ScanGuard.Mark(key);
                ILHook h = new ILHook(m, delegate (ILContext il) { Inject(il, id); });
                hooks.Add(h);
                Hooked++;
                curModHooks++;
            }
            catch (Exception e)
            {
                Failed++;
                Profiler.HookDisabled[id] = true;
                LastError = name + ": " + e.GetType().Name + ": " + e.Message;
                if (Settings.VerboseLog) Note(L.T("Хук не поставлен ", "Hook not installed ") + LastError);
            }
        }

        // Собственно вставка IL.
        static void Inject(ILContext il, int id)
        {
            ILProcessor p = il.IL;
            Mono.Cecil.MethodReference enter = il.Import(enterMi);
            Mono.Cecil.MethodReference leave = il.Import(leaveMi);
            Mono.Cecil.Cil.MethodBody body = il.Body;
            Instruction first = body.Instructions[0];
            p.InsertBefore(first, p.Create(OpCodes.Ldc_I4, id));
            p.InsertBefore(first, p.Create(OpCodes.Call, enter));

            // каждый ret -> (ldc.i4 id; call Leave; ret)
            for (int i = 0; i < body.Instructions.Count; i++)
            {
                Instruction ins = body.Instructions[i];
                if (ins.OpCode != OpCodes.Ret) continue;
                ins.OpCode = OpCodes.Ldc_I4;
                ins.Operand = id;
                Instruction call = p.Create(OpCodes.Call, leave);
                Instruction ret = p.Create(OpCodes.Ret);
                p.InsertAfter(ins, call);
                p.InsertAfter(call, ret);
                i += 2;
            }

            Mono.Cecil.TypeReference origType = FindOrigType(il.Method);
            if (origType != null) WrapOrigCalls(il, origType, enter, leave);
        }

        // Хук в стиле On.* / MonoMod: первым параметром идёт оригинал - delegate orig_Имя (или параметр с именем orig
        // у Func/Action). Возвращает его тип, если метод такой, иначе null.
        static Mono.Cecil.TypeReference FindOrigType(Mono.Cecil.MethodDefinition m)
        {
            if (m == null || m.Parameters.Count == 0) return null;
            Mono.Cecil.ParameterDefinition p0 = m.Parameters[0];
            string tn = p0.ParameterType.Name;
            if (tn == null) return null;
            if (tn.StartsWith("orig_", StringComparison.Ordinal)) return p0.ParameterType;
            if (p0.Name == "orig" && (tn.StartsWith("Func`", StringComparison.Ordinal) || tn.StartsWith("Action", StringComparison.Ordinal)))
                return p0.ParameterType;
            return null;
        }

        // Каждый вызов оригинала orig.Invoke(...) в хуке мода оборачиваем в Enter/Leave(OrigId): это работа игры, а не
        // мода, Profiler вычтет её из времени метода мода и учтёт отдельно. Как и с ret, исходная инструкция вызова
        // превращается в «ldc.i4 OrigId» (на неё могли вести переходы), а сам вызов ставится следом.
        // Стек не меняется: ldc.i4 + call Enter забирают своё же число. Хвостовые вызовы (tail.) не трогаем.
        static void WrapOrigCalls(ILContext il, Mono.Cecil.TypeReference origType, Mono.Cecil.MethodReference enter, Mono.Cecil.MethodReference leave)
        {
            ILProcessor p = il.IL;
            Mono.Cecil.Cil.MethodBody body = il.Body;
            string origName = origType.FullName;
            for (int i = 0; i < body.Instructions.Count; i++)
            {
                Instruction ins = body.Instructions[i];
                if (ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Call) continue;
                Mono.Cecil.MethodReference mr = ins.Operand as Mono.Cecil.MethodReference;
                if (mr == null || mr.Name != "Invoke" || mr.DeclaringType == null || mr.DeclaringType.FullName != origName) continue;
                if (ins.Previous != null && ins.Previous.OpCode == OpCodes.Tail) continue;
                Instruction realCall = p.Create(ins.OpCode, mr);
                ins.OpCode = OpCodes.Ldc_I4;
                ins.Operand = Profiler.OrigId;
                Instruction e = p.Create(OpCodes.Call, enter);
                Instruction l1 = p.Create(OpCodes.Ldc_I4, Profiler.OrigId);
                Instruction l2 = p.Create(OpCodes.Call, leave);
                p.InsertAfter(ins, e);
                p.InsertAfter(e, realCall);
                p.InsertAfter(realCall, l1);
                p.InsertAfter(l1, l2);
                i += 4;
            }
        }

        public static void UndoAll()
        {
            for (int i = 0; i < hooks.Count; i++)
            {
                try { hooks[i].Dispose(); } catch (Exception) { }
            }
            hooks.Clear();
            CloseModule();
            ScanGuard.ClearMark();       // обычное закрытие игры - это не падение
        }

        public static string Status()
        {
            return L.T("хуки: ", "hooks: ") + Hooked + (Failed > 0 ? L.T(", ошибок ", ", errors ") + Failed : "") + (SkippedLimit > 0 ? L.T(", лимит ", ", limit ") + SkippedLimit : "") + (Done ? "" : L.T(" (ставлю…)", " (installing...)"));
        }
    }
}
