//  IlScanner.cs - ищет в DLL мода «точки входа»: места, куда игра заходит в код мода.
//
//  Почему так:  время мода = время его точек входа (всё, что мод делает, начинается
//  в одной из них). Замеряем точки входа - видим нагрузку всего мода.
//
//  КАК ЧИТАЕМ DLL:  не через Mono-рефлексию, а читателем Cecil прямо с диска.
//  У рефлексии есть опасная особенность: если у метода в сигнатуре тип, которого в
//  игре уже нет (мод собран под старую версию), Mono может упасть целиком. Cecil
//  просто читает файл и до игры не дотрагивается, так что упасть не может.
//  Mono мы спрашиваем только про те методы, у которых заранее проверили, что все
//  типы на месте. Остальные в игре запуститься всё равно не могут.
//
//  Что считаем точкой входа:
//    * Unity-методы в наследниках MonoBehaviour (Update, LateUpdate, OnGUI, ...)
//    * корутины: MoveNext у сгенерированных классов <...>d__N
//    * действия PlayMaker (OnEnter, OnUpdate, ...)
//    * обработчики: методы, на которые в IL есть ldftn (подписки на события,
//      On.* хуки, ModHooks.*, лямбды, кнопки интерфейса)               [режим full]
//    * патчи Harmony: по имени атрибута и по именам Prefix*/Postfix*/Finalizer* [full]
//    * хуки Hollow Knight: первый параметр типа orig_*                  [full]
//  Что менять:  списки имён ниже.
using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Gaslight
{
    public static class IlScanner
    {
        // Все Unity-сообщения, которые реально вызываются во время игры.
        public static readonly string[] UnityMessages = new string[] {
            "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy",
            "Update", "LateUpdate", "FixedUpdate", "OnGUI",
            "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnWillRenderObject", "OnRenderImage",
            "OnAnimatorMove", "OnAnimatorIK", "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
            "OnBecameVisible", "OnBecameInvisible", "OnLevelWasLoaded",
            "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
            "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
            "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit",
            "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
            "OnControllerColliderHit", "OnParticleCollision", "OnParticleTrigger", "OnJointBreak",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseDrag", "OnMouseUpAsButton",
            "OnTransformChildrenChanged", "OnTransformParentChanged", "OnRectTransformDimensionsChange" };

        // Методы действий PlayMaker (FsmStateAction), которые вызываются в игре.
        static readonly string[] FsmActionMethods = new string[] {
            "OnEnter", "OnUpdate", "OnFixedUpdate", "OnLateUpdate", "OnGUI", "OnExit", "OnPreprocess" };

        static readonly string[] PatchNamePrefixes = new string[] { "Prefix", "Postfix", "Finalizer" };
        static readonly string[] PatchAttributePrefixes = new string[] { "HarmonyPrefix", "HarmonyPostfix", "HarmonyFinalizer" };

        // Сборки, типы из которых не проверяем (системные и Unity - их типы не пропадают).
        static readonly string[] SystemScopes = new string[] { "mscorlib", "System", "netstandard", "UnityEngine", "Unity.", "Mono." };

        // ---- сколько методов пропущено, потому что в них нет типов игры (запуститься они не могут)
        public static int SkippedBadSignature;

        static readonly Dictionary<System.Type, bool> mbCache = new Dictionary<System.Type, bool>();
        static readonly Dictionary<System.Type, bool> fsmCache = new Dictionary<System.Type, bool>();
        static readonly Dictionary<string, bool> typeOkCache = new Dictionary<string, bool>();
        static readonly Dictionary<string, Assembly> asmCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<int> seen = new HashSet<int>();

        //  Чтение DLL

        // Открывает DLL мода читателем Cecil. null - если файл прочитать нельзя.
        public static ModuleDefinition OpenModule(Assembly asm, out string error)
        {
            error = null;
            seen.Clear();
            string path = "";
            try { path = asm.Location; } catch (Exception) { }
            if (string.IsNullOrEmpty(path)) { error = L.T("у DLL нет файла на диске", "the DLL has no file on disk"); return null; }
            try
            {
                // FileShare.ReadWrite|Delete - чтобы не мешать игре, которая держит этот файл открытым
                System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                ReaderParameters rp = new ReaderParameters(ReadingMode.Deferred);
                rp.ReadSymbols = false;
                ModuleDefinition md = ModuleDefinition.ReadModule(fs, rp);
                OpenRaw(path);                      // отдельное «сырое» чтение IL (см. AddDelegateTargets)
                return md;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        // Может ли в этом типе вообще быть то, что мы ищем?
        public static bool IsCandidateType(System.Type t)
        {
            if (t == null || t.IsInterface) return false;
            return !(t.IsGenericTypeDefinition || t.ContainsGenericParameters);   // generic-типы не хукаются
        }

        //  Поиск точек входа в одном типе
        public static void ScanType(System.Type t, ModuleDefinition cecil, Module rt, bool full, Dictionary<MethodBase, string> found)
        {
            if (cecil == null || !IsCandidateType(t)) return;
            TypeDefinition td = null;
            try { td = cecil.LookupToken(t.MetadataToken) as TypeDefinition; } catch (Exception) { }
            if (td == null || !td.HasMethods) return;

            bool isBehaviour = DerivesFrom(t, "UnityEngine", "MonoBehaviour", mbCache);
            bool isFsmAction = DerivesFrom(t, "HutongGames.PlayMaker", "FsmStateAction", fsmCache);
            bool isCoroutine = IsCoroutineType(td);

            for (int i = 0; i < td.Methods.Count; i++)
            {
                MethodDefinition md = td.Methods[i];
                if (!CanHook(md)) continue;
                if (IsEntryPoint(md, isBehaviour, isFsmAction, isCoroutine, full)) Add(md, rt, found);
                if (full) AddDelegateTargets(md, cecil, rt, found);
            }
        }

        static bool IsEntryPoint(MethodDefinition md, bool isBehaviour, bool isFsmAction, bool isCoroutine, bool full)
        {
            string n = md.Name;
            if (isBehaviour && !md.IsStatic && Array.IndexOf(UnityMessages, n) >= 0) return true;
            if (isCoroutine && n == "MoveNext") return true;
            if (isFsmAction && !md.IsStatic && Array.IndexOf(FsmActionMethods, n) >= 0) return true;
            if (!full) return false;
            if (md.IsStatic && (NameLooksLikePatch(n) || HasPatchAttribute(md))) return true;
            return false;     // хуки HK (On.*, ModHooks) и лямбды найдёт AddDelegateTargets по ldftn
        }

        //  Обработчики: всё, на что в IL метода есть ldftn (только методы этого же файла).
        //
        //  IL читаем «сырыми» байтами прямо из файла, а не через Cecil: Cecil на каждый метод
        //  создаёт кучу объектов, а у больших модов методов десятки тысяч - это дало бы
        //  сотни мегабайт мусора и подвисания от сборщика мусора. Сырое чтение ничего не выделяет.
        static System.IO.FileStream rawFs;
        static int[] secVa, secSize, secRaw;                 // таблица секций PE-файла: RVA -> смещение в файле
        static byte[] ilBuf = new byte[4096];                // буфер для кода одного метода (переиспользуется)
        static readonly byte[] hdrBuf = new byte[64];
        static sbyte[] opSize1, opSize2;                     // размер операнда по опкоду: -1 неизвестен, -2 switch

        static void AddDelegateTargets(MethodDefinition md, ModuleDefinition cecil, Module rt, Dictionary<MethodBase, string> found)
        {
            if (rawFs == null || md.RVA == 0) return;
            try
            {
                int len = ReadIl(md.RVA);
                if (len <= 0) return;
                byte[] il = ilBuf;
                int p = 0;
                while (p < len)
                {
                    int b = il[p++];
                    int size;
                    if (b == 0xFE)
                    {
                        if (p >= len) return;
                        int b2 = il[p++];
                        size = opSize2[b2];
                        if ((b2 == 0x06 || b2 == 0x07) && p + 4 <= len)         // ldftn / ldvirtftn
                            HandleFunctionToken(BitConverter.ToInt32(il, p), cecil, rt, found);
                    }
                    else size = opSize1[b];
                    if (size == -2) { if (p + 4 > len) return; size = 4 + 4 * BitConverter.ToInt32(il, p); }   // switch
                    if (size < 0) return;                                        // неизвестный опкод - дальше читать нельзя
                    p += size;
                }
            }
            catch (Exception) { }     // кривое IL одного метода не должно останавливать поиск
        }

        static void HandleFunctionToken(int token, ModuleDefinition cecil, Module rt, Dictionary<MethodBase, string> found)
        {
            if ((token >> 24) != 0x06) return;           // 0x06 = метод из этого же файла (MethodDef)
            if (seen.Contains(token)) return;
            MethodDefinition target = cecil.LookupToken(token) as MethodDefinition;
            if (target != null && CanHook(target)) Add(target, rt, found);
        }

        // Читает код метода (по RVA) в ilBuf. Возвращает длину кода или -1.
        static int ReadIl(int rva)
        {
            long off = RvaToOffset(rva);
            if (off < 0) return -1;
            rawFs.Position = off;
            int b0 = rawFs.ReadByte();
            int codeSize;
            long codeOff;
            if ((b0 & 3) == 2) { codeSize = b0 >> 2; codeOff = off + 1; }                  // «крошечный» заголовок
            else if ((b0 & 3) == 3)                                                         // «толстый» заголовок
            {
                hdrBuf[0] = (byte)b0;
                if (rawFs.Read(hdrBuf, 1, 11) != 11) return -1;
                codeSize = BitConverter.ToInt32(hdrBuf, 4);
                codeOff = off + (hdrBuf[1] >> 4) * 4;
            }
            else return -1;
            if (codeSize <= 0 || codeSize > 1000000) return -1;
            if (ilBuf.Length < codeSize) ilBuf = new byte[Math.Max(codeSize, ilBuf.Length * 2)];
            rawFs.Position = codeOff;
            int got = 0;
            while (got < codeSize)
            {
                int n = rawFs.Read(ilBuf, got, codeSize - got);
                if (n <= 0) return -1;
                got += n;
            }
            return codeSize;
        }

        static long RvaToOffset(int rva)
        {
            for (int i = 0; i < secVa.Length; i++)
                if (rva >= secVa[i] && rva < secVa[i] + secSize[i]) return (long)rva - secVa[i] + secRaw[i];
            return -1;
        }

        // Открывает файл для «сырого» чтения и читает таблицу секций PE.
        static void OpenRaw(string path)
        {
            CloseRaw();
            try
            {
                BuildOpTables();
                rawFs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 4096);
                byte[] h = new byte[64];
                if (rawFs.Read(h, 0, 64) != 64) { CloseRaw(); return; }
                int pe = BitConverter.ToInt32(h, 0x3C);
                rawFs.Position = pe;
                byte[] coff = new byte[24];
                if (rawFs.Read(coff, 0, 24) != 24 || coff[0] != 'P' || coff[1] != 'E') { CloseRaw(); return; }
                int nsec = BitConverter.ToUInt16(coff, 6);
                int optSize = BitConverter.ToUInt16(coff, 20);
                rawFs.Position = pe + 24 + optSize;
                byte[] sec = new byte[nsec * 40];
                if (rawFs.Read(sec, 0, sec.Length) != sec.Length) { CloseRaw(); return; }
                secVa = new int[nsec]; secSize = new int[nsec]; secRaw = new int[nsec];
                for (int i = 0; i < nsec; i++)
                {
                    int virtSize = BitConverter.ToInt32(sec, i * 40 + 8);
                    int rawSize = BitConverter.ToInt32(sec, i * 40 + 16);
                    secVa[i] = BitConverter.ToInt32(sec, i * 40 + 12);
                    secSize[i] = Math.Max(virtSize, rawSize);
                    secRaw[i] = BitConverter.ToInt32(sec, i * 40 + 20);
                }
            }
            catch (Exception) { CloseRaw(); }
        }

        public static void CloseRaw()
        {
            if (rawFs != null)
            {
                try { rawFs.Close(); } catch (Exception) { }
            }
            rawFs = null;
        }

        // Размеры операндов IL-команд (берём из таблицы .NET, чтобы не писать её руками).
        static void BuildOpTables()
        {
            if (opSize1 != null) return;
            opSize1 = new sbyte[256];
            opSize2 = new sbyte[256];
            for (int i = 0; i < 256; i++) { opSize1[i] = -1; opSize2[i] = -1; }
            FieldInfo[] fs = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fs.Length; i++)
            {
                System.Reflection.Emit.OpCode oc = (System.Reflection.Emit.OpCode)fs[i].GetValue(null);
                sbyte sz = OperandBytes(oc.OperandType);
                int v = (ushort)oc.Value & 0xFF;
                if (oc.Size == 1) opSize1[v] = sz; else opSize2[v] = sz;
            }
        }

        static sbyte OperandBytes(System.Reflection.Emit.OperandType t)
        {
            switch (t)
            {
                case System.Reflection.Emit.OperandType.InlineNone: return 0;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                case System.Reflection.Emit.OperandType.ShortInlineI:
                case System.Reflection.Emit.OperandType.ShortInlineVar: return 1;
                case System.Reflection.Emit.OperandType.InlineVar: return 2;
                case System.Reflection.Emit.OperandType.InlineI8:
                case System.Reflection.Emit.OperandType.InlineR: return 8;
                case System.Reflection.Emit.OperandType.InlineSwitch: return -2;
                default: return 4;
            }
        }

        // Метод вообще можно оборачивать? (есть тело, не generic, не конструктор)
        static bool CanHook(MethodDefinition md)
        {
            if (md.IsConstructor) return false;
            if (!md.HasBody || md.IsAbstract || md.IsPInvokeImpl) return false;
            if (md.HasGenericParameters || md.DeclaringType.HasGenericParameters) return false;
            return true;
        }

        static bool NameLooksLikePatch(string n)
        {
            for (int i = 0; i < PatchNamePrefixes.Length; i++)
                if (n.StartsWith(PatchNamePrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        static bool HasPatchAttribute(MethodDefinition md)
        {
            if (!md.HasCustomAttributes) return false;
            for (int i = 0; i < md.CustomAttributes.Count; i++)
            {
                string an = md.CustomAttributes[i].AttributeType.Name;
                for (int k = 0; k < PatchAttributePrefixes.Length; k++)
                    if (an.StartsWith(PatchAttributePrefixes[k], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Добавляет метод в список, если его сигнатура целиком состоит из существующих типов.
        static void Add(MethodDefinition md, Module rt, Dictionary<MethodBase, string> found)
        {
            int token = md.MetadataToken.ToInt32();
            if (!seen.Add(token)) return;                 // этот метод уже разбирали
            if (!SignatureOk(md)) { SkippedBadSignature++; return; }
            MethodBase mbase = null;
            try { mbase = rt.ResolveMethod(token); } catch (Exception) { }
            if (mbase != null) found[mbase] = Pretty(md);
        }

        //  Проверка «все типы из сигнатуры есть в игре?» - без Mono-рефлексии по методу
        static bool SignatureOk(MethodDefinition md)
        {
            if (!TypeOk(md.ReturnType)) return false;
            if (md.HasParameters)
                for (int i = 0; i < md.Parameters.Count; i++)
                    if (!TypeOk(md.Parameters[i].ParameterType)) return false;
            return true;
        }

        static bool TypeOk(TypeReference tr)
        {
            if (tr == null) return true;
            GenericInstanceType gi = tr as GenericInstanceType;
            if (gi != null)
            {
                for (int i = 0; i < gi.GenericArguments.Count; i++)
                    if (!TypeOk(gi.GenericArguments[i])) return false;
                return TypeOk(gi.ElementType);
            }
            TypeSpecification ts = tr as TypeSpecification;          // массив, ref, указатель, модификаторы
            if (ts != null) return TypeOk(ts.ElementType);
            if (tr is GenericParameter || tr.IsDefinition) return true;

            AssemblyNameReference scope = tr.Scope as AssemblyNameReference;
            if (scope == null || IsSystemScope(scope.Name)) return true;

            string runtimeName = tr.FullName.Replace('/', '+');      // у вложенных типов Cecil пишет «/», Mono - «+»
            string key = runtimeName + ", " + scope.Name;
            bool ok;
            if (typeOkCache.TryGetValue(key, out ok)) return ok;
            ok = TypeExists(runtimeName, scope.Name);
            typeOkCache[key] = ok;
            return ok;
        }

        static bool IsSystemScope(string name)
        {
            for (int i = 0; i < SystemScopes.Length; i++)
                if (name.StartsWith(SystemScopes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // Есть ли такой тип среди загруженных сборок? Ищем осторожно, чтобы не ошибиться
        // в сторону «нет» (type forwarding и т.п.): считаем «нет», только если не нашли нигде.
        static bool TypeExists(string typeName, string assemblyName)
        {
            Assembly a = FindAssembly(assemblyName);
            if (a != null)
            {
                try { if (a.GetType(typeName, false) != null) return true; } catch (Exception) { }
            }
            try { if (System.Type.GetType(typeName + ", " + assemblyName, false) != null) return true; } catch (Exception) { }
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                try { if (all[i].GetType(typeName, false) != null) return true; } catch (Exception) { }
            }
            return false;
        }

        static Assembly FindAssembly(string name)
        {
            Assembly a;
            if (asmCache.TryGetValue(name, out a)) return a;
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length && a == null; i++)
            {
                try { if (string.Equals(all[i].GetName().Name, name, StringComparison.OrdinalIgnoreCase)) a = all[i]; } catch (Exception) { }
            }
            if (a == null)
            {
                try { a = Assembly.Load(name); } catch (Exception) { a = null; }
            }
            asmCache[name] = a;
            return a;
        }

        //  Типы и красивые имена

        // Наследуется ли тип от класса ns.name? Результат запоминаем (имя типа спрашиваем один раз).
        static bool DerivesFrom(System.Type t, string ns, string name, Dictionary<System.Type, bool> cache)
        {
            if (t == null) return false;
            bool r;
            if (cache.TryGetValue(t, out r)) return r;
            r = (t.Name == name && t.Namespace == ns) || DerivesFrom(t.BaseType, ns, name, cache);
            cache[t] = r;
            return r;
        }

        // Roslyn: <Load>d__5, старый mcs: <Load>c__Iterator0
        static bool IsCoroutineType(TypeDefinition t)
        {
            if (!t.IsNested) return false;
            string n = t.Name;
            return n.IndexOf(">d__", StringComparison.Ordinal) >= 0 || n.IndexOf(">c__Iterator", StringComparison.Ordinal) >= 0;
        }

        // Имя для отчёта:  Класс.Метод  /  Класс.Метод (coroutine)  /  Класс.Метод (lambda)
        static string Pretty(MethodDefinition md)
        {
            TypeDefinition t = md.DeclaringType;
            if (IsCoroutineType(t))
                return OwnerName(t) + "." + InsideBrackets(t.Name) + " (coroutine)";
            if (md.Name.IndexOf(">b__", StringComparison.Ordinal) > 0)
                return OwnerName(t) + "." + InsideBrackets(md.Name) + " (lambda)";
            return t.FullName.Replace('/', '+') + "." + md.Name;
        }

        // Для сгенерированных компилятором классов (<>c, <Load>d__5 ...) берём класс-хозяин.
        static string OwnerName(TypeDefinition t)
        {
            while (t.DeclaringType != null && t.Name.StartsWith("<", StringComparison.Ordinal)) t = t.DeclaringType;
            return t.FullName.Replace('/', '+');
        }

        // "<Load>d__5" -> "Load"
        static string InsideBrackets(string s)
        {
            int a = s.IndexOf('<'), b = s.IndexOf('>');
            return (a >= 0 && b > a) ? s.Substring(a + 1, b - a - 1) : s;
        }
    }
}
