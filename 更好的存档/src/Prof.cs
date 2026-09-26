using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SaveOpt
{
    internal static class Prof
    {
        internal const int Slots = 12;
        internal const int Cold = 0;
        internal const int Hot = 1;

        private static readonly string[] labels =
        {
            "PrepSaveFile", "根对象", "SaveSettings", "Sim", "对象区",
            "分组写", "对象序列化", "类型分派", "Game.Save", "目录",
            "组件自序列化", "取组件数组"
        };

        private static readonly int[] kind =
        {
            Cold, Cold, Cold, Cold, Cold, Hot, Hot, Hot, Cold, Cold, Hot, Hot
        };

        private static readonly int[] strideMask =
        {
            0, 0, 0, 0, 0, 7, 31, 31, 0, 0, 31, 15
        };

        private static readonly string[] mount = new string[Slots];
        private static readonly long[] ticks = new long[Slots];
        private static readonly long[] maxTicks = new long[Slots];
        private static readonly long[] calls = new long[Slots];
        private static readonly long[] sampled = new long[Slots];
        private static readonly int[] depth = new int[Slots];
        private static readonly long[] sampleStart = new long[Slots];
        private static readonly bool[] sampling = new bool[Slots];

        private static FieldInfo sceneField;
        private static FieldInfo keysField;
        private static FieldInfo managersField;
        private static int groupCount;
        private static int keyCount;
        private static int managerCount;
        private static uint rng = 0x9E3779B9u;
        private static bool mounted;

        internal static bool Mounted { get { return mounted; } }

        internal static void Begin()
        {
            for (int i = 0; i < Slots; i++)
            {
                ticks[i] = 0;
                maxTicks[i] = 0;
                calls[i] = 0;
                sampled[i] = 0;
                depth[i] = 0;
                sampling[i] = false;
            }
            groupCount = 0;
            keyCount = 0;
            managerCount = 0;
        }

        internal static void Enter(int slot)
        {
            calls[slot]++;
            int d = depth[slot]++;
            if (d != 0) return;

            bool take;
            if (kind[slot] == Cold) take = true;
            else
            {
                take = false;
                if (calls[slot] > 64)
                {
                    rng ^= rng << 13;
                    rng ^= rng >> 17;
                    rng ^= rng << 5;
                    take = (rng & (uint)strideMask[slot]) == 0;
                }
            }
            if (take)
            {
                sampleStart[slot] = System.Diagnostics.Stopwatch.GetTimestamp();
                sampling[slot] = true;
            }
        }

        internal static void Leave(int slot)
        {
            int d = depth[slot] - 1;
            if (d < 0) d = 0;
            depth[slot] = d;
            if (d != 0 || !sampling[slot]) return;

            long span = System.Diagnostics.Stopwatch.GetTimestamp() - sampleStart[slot];
            ticks[slot] += span;
            if (span > maxTicks[slot]) maxTicks[slot] = span;
            sampled[slot]++;
            sampling[slot] = false;
        }

        public static void EnterS() { Enter(7); }
        public static void LeaveS() { Leave(7); }
        public static void EnterD() { Enter(10); }
        public static void LeaveD() { Leave(10); }

        public static void P0() { Enter(0); }
        public static void P1() { Enter(1); }
        public static void P2() { Enter(2); }
        public static void P3() { Enter(3); }
        public static void P4() { Enter(4); }
        public static void P5() { Enter(5); }
        public static void P6() { Enter(6); }
        public static void P8() { Enter(8); }
        public static void P9() { Enter(9); }
        public static void P11() { Enter(11); }

        public static void Q0() { Leave(0); }
        public static void Q1() { Leave(1); }
        public static void Q2() { Leave(2); }
        public static void Q3() { Leave(3); }
        public static void Q4() { Leave(4); }
        public static void Q5() { Leave(5); }
        public static void Q6() { Leave(6); }
        public static void Q8() { Leave(8); }
        public static void Q9() { Leave(9); }
        public static void Q11() { Leave(11); }

        private static MethodInfo Find(Type owner, string name, Type[] args)
        {
            try
            {
                return AccessTools.Method(owner, name, args);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 定位 " + owner.Name + "." + name + " 失败: " + e.GetType().Name);
                return null;
            }
        }

        private static bool Hook(HarmonyLib.Harmony harmony, int slot, MethodInfo target)
        {
            if (target == null)
            {
                mount[slot] = "未找到";
                return false;
            }
            MethodInfo pre = AccessTools.Method(typeof(Prof), "P" + slot);
            MethodInfo post = AccessTools.Method(typeof(Prof), "Q" + slot);
            if (pre == null || post == null)
            {
                mount[slot] = "桩缺失";
                return false;
            }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyLib.HarmonyMethod(pre),
                    postfix: new HarmonyLib.HarmonyMethod(post));
                mount[slot] = "已挂载";
                return true;
            }
            catch (Exception e)
            {
                mount[slot] = "失败:" + e.GetType().Name;
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> InjectHotCalls(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo typeless = AccessTools.Method(typeof(KSerialization.Serializer), "SerializeTypeless",
                new[] { typeof(object), typeof(BinaryWriter) });
            MethodInfo enterS = AccessTools.Method(typeof(Prof), "EnterS");
            MethodInfo leaveS = AccessTools.Method(typeof(Prof), "LeaveS");
            MethodInfo enterD = AccessTools.Method(typeof(Prof), "EnterD");
            MethodInfo leaveD = AccessTools.Method(typeof(Prof), "LeaveD");

            var list = new List<CodeInstruction>(instructions);
            int typelessHits = 0, detailsHits = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Call && list[i].opcode != OpCodes.Callvirt) continue;
                MethodInfo m = list[i].operand as MethodInfo;
                if (m == null) continue;

                MethodInfo enter = null, leave = null;
                if (typeless != null && m == typeless)
                {
                    enter = enterS; leave = leaveS; typelessHits++;
                }
                else if (m.Name == "Serialize" && m.DeclaringType != null
                    && m.DeclaringType.Name == "ISaveLoadableDetails")
                {
                    enter = enterD; leave = leaveD; detailsHits++;
                }
                else continue;

                list.Insert(i, new CodeInstruction(OpCodes.Call, enter));
                i++;
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, leave));
                i++;
            }

            if (typelessHits != 2 || detailsHits != 1)
            {
                Debug.LogError("[更好的存档] 热调用注入点不符（SerializeTypeless=" + typelessHits
                    + "，ISaveLoadableDetails.Serialize=" + detailsHits + "），放弃注入");
                mount[7] = "注入失败";
                mount[10] = "注入失败";
                return instructions;
            }
            mount[7] = "已注入";
            mount[10] = "已注入";
            Diag.Trace("[更好的存档] 热调用注入：SerializeTypeless=" + typelessHits
                + "，ISaveLoadableDetails.Serialize=" + detailsHits);
            return list;
        }

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            sceneField = AccessTools.Field(typeof(SaveManager), "sceneObjects");
            keysField = AccessTools.Field(typeof(SaveManager), "orderedKeys");
            managersField = AccessTools.Field(typeof(SaveLoadRoot), "serializableComponentManagers");

            int ok = 0;
            Type[] bw = { typeof(BinaryWriter) };
            if (Hook(harmony, 0, Find(typeof(SaveLoader), "PrepSaveFile", Type.EmptyTypes))) ok++;
            if (Hook(harmony, 1, Find(typeof(KSerialization.Serializer), "Serialize", new[] { typeof(object), typeof(BinaryWriter) }))) ok++;
            if (Hook(harmony, 2, Find(typeof(Game), "SaveSettings", bw))) ok++;
            if (Hook(harmony, 3, Find(typeof(Sim), "Save", new[] { typeof(BinaryWriter), typeof(int), typeof(int) }))) ok++;
            if (Hook(harmony, 4, Find(typeof(SaveManager), "Save", bw))) ok++;
            if (Hook(harmony, 5, Find(typeof(SaveManager), "Write", new[] { typeof(Tag), typeof(List<SaveLoadRoot>), typeof(BinaryWriter) }))) ok++;
            if (Hook(harmony, 6, Find(typeof(SaveLoadRoot), "SaveWithoutTransform", bw))) ok++;
            if (Hook(harmony, 8, Find(typeof(Game), "Save", bw))) ok++;
            if (Hook(harmony, 9, Find(typeof(KSerialization.Manager), "SerializeDirectory", bw))) ok++;
            if (Hook(harmony, 11, Find(typeof(UnityEngine.Component), "GetComponents", Type.EmptyTypes))) ok++;

            MethodInfo inject = AccessTools.Method(typeof(SaveLoadRoot), "SaveWithoutTransform", bw);
            if (inject != null)
            {
                try
                {
                    harmony.Patch(inject,
                        transpiler: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(Prof), "InjectHotCalls")));
                    ok += 2;
                }
                catch (Exception e)
                {
                    mount[7] = "失败:" + e.GetType().Name;
                    mount[10] = "失败:" + e.GetType().Name;
                }
            }
            else
            {
                mount[7] = "未找到";
                mount[10] = "未找到";
            }

            mounted = ok > 0;
            string detail = "";
            for (int i = 0; i < Slots; i++)
            {
                if (mount[i] != "已挂载" && mount[i] != "已注入") detail += " " + labels[i] + "=" + mount[i];
            }
            Debug.Log("[更好的存档] 窗口分段探针 " + ok + "/" + Slots + " 已挂载"
                + (detail.Length == 0 ? "" : "，异常项:" + detail));
            return mounted;
        }

        private static int Count(object dict)
        {
            System.Collections.ICollection c = dict as System.Collections.ICollection;
            return c == null ? -1 : c.Count;
        }

        internal static void Snapshot()
        {
            try
            {
                SaveLoader loader = SaveLoader.Instance;
                object manager = loader == null ? null : (object)loader.saveManager;
                if (manager != null)
                {
                    if (sceneField != null) groupCount = Count(sceneField.GetValue(manager));
                    if (keysField != null) keyCount = Count(keysField.GetValue(manager));
                }
                if (managersField != null) managerCount = Count(managersField.GetValue(null));
            }
            catch (Exception e)
            {
                Diag.Trace("[更好的存档] 探针规模读取失败: " + e.GetType().Name);
            }
        }

        private static double Ms(int slot)
        {
            if (sampled[slot] <= 0) return 0;
            return ticks[slot] * (1000.0 / System.Diagnostics.Stopwatch.Frequency)
                * calls[slot] / sampled[slot];
        }

        private static string Cell(int slot)
        {
            if (mount[slot] != "已挂载" && mount[slot] != "已注入") return labels[slot] + "=?";
            if (calls[slot] <= 0) return labels[slot] + " 0/0";
            return labels[slot] + " " + Ms(slot).ToString("F0") + "/" + calls[slot]
                + "(" + sampled[slot] + ")";
        }

        internal static string Report(double windowMs)
        {
            double top = Ms(0) + Ms(1) + Ms(2) + Ms(3) + Ms(4) + Ms(8) + Ms(9);
            double loop = Ms(6) - Ms(7) - Ms(10) - Ms(11);
            string warn = "";
            for (int i = 0; i < Slots; i++)
            {
                if (mount[i] != "已挂载" && mount[i] != "已注入") continue;
                if (depth[i] != 0) warn += " " + labels[i] + "深漏" + depth[i];
                if (kind[i] == Hot && maxTicks[i] * (1000.0 / System.Diagnostics.Stopwatch.Frequency) > 50)
                    warn += " " + labels[i] + "最大样本>50ms";
            }
            return "[更好的存档] 窗口分段(ms/次) 窗口 " + windowMs.ToString("F0")
                + " ｜ " + Cell(0) + " ｜ " + Cell(1) + " ｜ " + Cell(2) + " ｜ " + Cell(3)
                + " ｜ " + Cell(4) + " ｜ " + Cell(8) + " ｜ " + Cell(9)
                + " ｜ 未归类 " + (windowMs - top).ToString("F0")
                + "\n[更好的存档] 对象区内部(ms/次) " + Cell(5) + " ｜ " + Cell(6)
                + " ｜ " + Cell(7) + " ｜ " + Cell(10) + " ｜ " + Cell(11)
                + " ｜ 标签组 " + groupCount + " ｜ 排序键 " + keyCount + " ｜ 管理器 " + managerCount
                + "\n[更好的存档] 循环净开销(ms) " + loop.ToString("F0")
                + " = 对象序列化 " + Ms(6).ToString("F0") + " − 类型分派 " + Ms(7).ToString("F0")
                + " − 组件自序列化 " + Ms(10).ToString("F0") + " − 取组件数组 " + Ms(11).ToString("F0")
                + " ｜ 占窗口 " + (loop / windowMs * 100.0).ToString("F0") + "%"
                + (warn.Length == 0 ? "" : "\n[更好的存档] 探针告警:" + warn);
        }
    }
}
