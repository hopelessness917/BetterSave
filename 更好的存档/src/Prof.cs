using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace SaveOpt
{
    internal static class Prof
    {
        internal const int Slots = 10;

        private static readonly string[] labels =
        {
            "PrepSaveFile", "根对象", "SaveSettings", "Sim", "对象区",
            "分组写", "对象序列化", "类型分派", "Game.Save", "目录"
        };

        private static readonly string[] mount = new string[Slots];
        private static readonly long[] ticks = new long[Slots];
        private static readonly long[] calls = new long[Slots];
        private static readonly long[] sampled = new long[Slots];
        private static readonly int[] depth = new int[Slots];
        private static readonly long[] sampleStart = new long[Slots];
        private static readonly bool[] sampling = new bool[Slots];

        private static FieldInfo sceneField;
        private static FieldInfo keysField;
        private static int groupCount;
        private static int keyCount;
        private static bool mounted;

        internal static bool Mounted { get { return mounted; } }

        internal static void Begin()
        {
            for (int i = 0; i < Slots; i++)
            {
                ticks[i] = 0;
                calls[i] = 0;
                sampled[i] = 0;
                depth[i] = 0;
                sampling[i] = false;
            }
            groupCount = 0;
            keyCount = 0;
        }

        internal static void Enter(int slot)
        {
            calls[slot]++;
            int d = depth[slot]++;
            if (d == 0 && (calls[slot] == 1 || (calls[slot] & 63) == 0))
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
            if (d == 0 && sampling[slot])
            {
                ticks[slot] += System.Diagnostics.Stopwatch.GetTimestamp() - sampleStart[slot];
                sampled[slot]++;
                sampling[slot] = false;
            }
        }

        public static void P0() { Enter(0); }
        public static void P1() { Enter(1); }
        public static void P2() { Enter(2); }
        public static void P3() { Enter(3); }
        public static void P4() { Enter(4); }
        public static void P5() { Enter(5); }
        public static void P6() { Enter(6); }
        public static void P7() { Enter(7); }
        public static void P8() { Enter(8); }
        public static void P9() { Enter(9); }

        public static void Q0() { Leave(0); }
        public static void Q1() { Leave(1); }
        public static void Q2() { Leave(2); }
        public static void Q3() { Leave(3); }
        public static void Q4() { Leave(4); }
        public static void Q5() { Leave(5); }
        public static void Q6() { Leave(6); }
        public static void Q7() { Leave(7); }
        public static void Q8() { Leave(8); }
        public static void Q9() { Leave(9); }

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

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            sceneField = AccessTools.Field(typeof(SaveManager), "sceneObjects");
            keysField = AccessTools.Field(typeof(SaveManager), "orderedKeys");

            int ok = 0;
            Type[] bw = { typeof(BinaryWriter) };
            if (Hook(harmony, 0, Find(typeof(SaveLoader), "PrepSaveFile", Type.EmptyTypes))) ok++;
            if (Hook(harmony, 1, Find(typeof(KSerialization.Serializer), "Serialize", new[] { typeof(object), typeof(BinaryWriter) }))) ok++;
            if (Hook(harmony, 2, Find(typeof(Game), "SaveSettings", bw))) ok++;
            if (Hook(harmony, 3, Find(typeof(Sim), "Save", new[] { typeof(BinaryWriter), typeof(int), typeof(int) }))) ok++;
            if (Hook(harmony, 4, Find(typeof(SaveManager), "Save", bw))) ok++;
            if (Hook(harmony, 5, Find(typeof(SaveManager), "Write", new[] { typeof(Tag), typeof(List<SaveLoadRoot>), typeof(BinaryWriter) }))) ok++;
            if (Hook(harmony, 6, Find(typeof(SaveLoadRoot), "SaveWithoutTransform", bw))) ok++;
            if (Hook(harmony, 7, Find(typeof(KSerialization.Serializer), "SerializeTypeless", new[] { typeof(object), typeof(BinaryWriter) }))) ok++;
            if (Hook(harmony, 8, Find(typeof(Game), "Save", bw))) ok++;
            if (Hook(harmony, 9, Find(typeof(KSerialization.Manager), "SerializeDirectory", bw))) ok++;

            mounted = ok > 0;
            string detail = "";
            for (int i = 0; i < Slots; i++)
            {
                if (mount[i] != "已挂载") detail += " " + labels[i] + "=" + mount[i];
            }
            Debug.Log("[更好的存档] 窗口分段探针 " + ok + "/" + Slots + " 已挂载"
                + (detail.Length == 0 ? "" : "，异常项:" + detail));
            return mounted;
        }

        internal static void Snapshot()
        {
            try
            {
                SaveLoader loader = SaveLoader.Instance;
                object manager = loader == null ? null : (object)loader.saveManager;
                if (manager == null) return;
                if (sceneField != null)
                {
                    System.Collections.ICollection c = sceneField.GetValue(manager) as System.Collections.ICollection;
                    if (c != null) groupCount = c.Count;
                }
                if (keysField != null)
                {
                    System.Collections.ICollection c = keysField.GetValue(manager) as System.Collections.ICollection;
                    if (c != null) keyCount = c.Count;
                }
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
            if (mount[slot] != "已挂载") return labels[slot] + "=?";
            if (calls[slot] <= 0) return labels[slot] + " 0/0";
            return labels[slot] + " " + Ms(slot).ToString("F0") + "/" + calls[slot];
        }

        internal static string Report(double windowMs)
        {
            double top = Ms(0) + Ms(1) + Ms(2) + Ms(3) + Ms(4) + Ms(8) + Ms(9);
            return "[更好的存档] 窗口分段(ms/次) 窗口 " + windowMs.ToString("F0")
                + " ｜ " + Cell(0) + " ｜ " + Cell(1) + " ｜ " + Cell(2) + " ｜ " + Cell(3)
                + " ｜ " + Cell(4) + " ｜ " + Cell(8) + " ｜ " + Cell(9)
                + " ｜ 未归类 " + (windowMs - top).ToString("F0")
                + "\n[更好的存档] 对象区内部(ms/次) " + Cell(5) + " ｜ " + Cell(6) + " ｜ " + Cell(7)
                + " ｜ 标签组 " + groupCount + " ｜ 排序键 " + keyCount;
        }
    }
}
