using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace SaveOpt
{
    internal static class GcTuner
    {
        private static string env = "未探测";

        private static Type speedType;
        private static PropertyInfo instanceProp;
        private static PropertyInfo pausedProp;

        private static int c0;
        private static int c1;
        private static int c2;
        private static long heapBefore;
        private static long firstSaveHeap;
        private static double wallStart;
        private static TimeSpan cpuStart;
        private static long savesSeen;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                env = "isIncremental=" + GarbageCollector.isIncremental
                    + " GCMode=" + GarbageCollector.GCMode
                    + " vSync=" + QualitySettings.vSyncCount
                    + " targetFrameRate=" + Application.targetFrameRate;
            }
            catch (Exception e)
            {
                env = "探测失败: " + e.GetType().Name + " " + e.Message;
            }
            Diag.Trace("[更好的存档] GC 环境: " + env);

            ResolveApis();
        }

        private static void ResolveApis()
        {
            try
            {
                speedType = AccessTools.TypeByName("SpeedControlScreen");
                if (speedType != null)
                {
                    instanceProp = AccessTools.Property(speedType, "Instance");
                    pausedProp = AccessTools.Property(speedType, "IsPaused");
                }
            }
            catch (Exception) { }

            Diag.Trace("[更好的存档] 暂停检测探测: 可用=" + (instanceProp != null && pausedProp != null));
        }

        internal static long SafeHeap()
        {
            try { return GC.GetTotalMemory(false); }
            catch (Exception) { return 0; }
        }

        internal static long HeapMb()
        {
            return SafeHeap() / 1048576;
        }

        internal static bool PauseDetectable
        {
            get { return instanceProp != null && pausedProp != null; }
        }

        internal static bool IsPaused()
        {
            if (instanceProp == null || pausedProp == null) return false;
            object screen = instanceProp.GetValue(null, null);
            if (screen == null) return false;
            object v = pausedProp.GetValue(screen, null);
            return v is bool && (bool)v;
        }

        internal static void BeginSave()
        {
            c0 = GC.CollectionCount(0);
            c1 = GC.CollectionCount(1);
            c2 = GC.CollectionCount(2);
            heapBefore = SafeHeap();
            if (savesSeen == 0) firstSaveHeap = heapBefore;
            wallStart = Time.realtimeSinceStartup;
            try { cpuStart = Process.GetCurrentProcess().TotalProcessorTime; }
            catch (Exception) { cpuStart = TimeSpan.MinValue; }
            savesSeen++;
        }

        internal static string SaveSummary()
        {
            long after = SafeHeap();
            long debt = after - firstSaveHeap;
            string cores = "?";
            double wall = (Time.realtimeSinceStartup - wallStart) * 1000.0;
            if (cpuStart != TimeSpan.MinValue)
            {
                try
                {
                    double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds;
                    cores = cpuMs.ToString("F0") + " ms / 平均 "
                        + (cpuMs / (wall > 1.0 ? wall : 1.0)).ToString("F2") + " 核";
                }
                catch (Exception) { }
            }
            return "[更好的存档] 本轮存档期间 GC: 第0代 +" + (GC.CollectionCount(0) - c0)
                + "，第1代 +" + (GC.CollectionCount(1) - c1)
                + "，第2代 +" + (GC.CollectionCount(2) - c2)
                + " ｜ 堆 " + (heapBefore / 1048576) + " -> " + (after / 1048576)
                + " MB（相对本局首次存档 +" + (debt / 1048576) + " MB）"
                + " ｜ CPU " + cores + "（墙钟 " + wall.ToString("F0") + " ms）";
        }

        internal static string Summary()
        {
            return "[更好的存档] GC 环境: " + env + " ｜ 存档 " + savesSeen + " 次 ｜ 暂停检测="
                + (PauseDetectable ? "可用" : "不可用");
        }
    }
}
