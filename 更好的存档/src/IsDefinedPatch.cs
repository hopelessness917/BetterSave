using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SaveOpt
{
    internal static class IsDefinedCache
    {
        private static readonly Dictionary<Type, Dictionary<Type, bool>> Cache = new Dictionary<Type, Dictionary<Type, bool>>();
        private static long hits;
        private static long misses;
        private static long passthrough;

        internal static string Summary()
        {
            return "[更好的存档] IsDefined 缓存：命中 " + hits + " 次，未命中 " + misses
                + " 次（合计 " + (hits + misses) + " 次调用），直通 " + passthrough
                + " 次，缓存类型 " + Cache.Count + " 个";
        }

        public static bool Check(MemberInfo self, Type attributeType, bool inherit)
        {
            Type selfType = self as Type;
            if (selfType == null || attributeType == null || inherit)
            {
                passthrough++;
                if (self == null || attributeType == null)
                {
                    return false;
                }
                return self.IsDefined(attributeType, inherit);
            }

            Dictionary<Type, bool> perType;
            if (!Cache.TryGetValue(selfType, out perType))
            {
                perType = new Dictionary<Type, bool>(2);
                Cache[selfType] = perType;
            }

            bool value;
            if (perType.TryGetValue(attributeType, out value))
            {
                hits++;
                return value;
            }

            misses++;
            value = selfType.IsDefined(attributeType, false);
            perType[attributeType] = value;
            return value;
        }
    }

    internal static class IsDefinedPatch
    {
        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(SaveLoadRoot), "SaveWithoutTransform");
            if (target == null)
            {
                Debug.LogError("[更好的存档] 找不到 SaveLoadRoot.SaveWithoutTransform，第三刀未挂载");
                return false;
            }
            harmony.Patch(target, transpiler: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(IsDefinedPatch), "Transpile")));
            Diag.Trace("[更好的存档] SaveLoadRoot.SaveWithoutTransform 已挂载（IsDefined 类型缓存）");
            return true;
        }

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(typeof(IsDefinedCache), "Check");
            if (replacement == null)
            {
                Debug.LogError("[更好的存档] IsDefinedCache.Check 解析失败，放弃第三刀");
                return instructions;
            }

            var list = new List<CodeInstruction>(instructions);
            int swapped = 0, skipped = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ins = list[i];
                if (ins.opcode != OpCodes.Callvirt && ins.opcode != OpCodes.Call)
                {
                    continue;
                }
                MethodInfo m = ins.operand as MethodInfo;
                if (m == null || m.Name != "IsDefined")
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 2 || ps[0].ParameterType != typeof(Type) || ps[1].ParameterType != typeof(bool))
                {
                    skipped++;
                    continue;
                }
                var rep = new CodeInstruction(OpCodes.Call, replacement);
                foreach (Label lb in ins.labels) rep.labels.Add(lb);
                foreach (ExceptionBlock eb in ins.blocks) rep.blocks.Add(eb);
                list[i] = rep;
                swapped++;
                Diag.Trace("[更好的存档] IsDefined 替换点 @" + i + "：" + m.DeclaringType.Name + "::IsDefined(Type, bool)");
            }

            if (swapped == 0)
            {
                Debug.LogError("[更好的存档] 未找到 IsDefined 调用点，放弃第三刀（游戏可运行）");
                return instructions;
            }
            Diag.Trace("[更好的存档] IsDefined 替换 " + swapped + " 处，跳过 " + skipped + " 处（同栈效果：3 弹 1 压）");
            return list;
        }
    }
}
