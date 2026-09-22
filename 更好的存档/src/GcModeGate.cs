using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace SaveOpt
{
    internal static class GcModeGate
    {
        private const int ProbeMb = 64;
        private const int CheckEveryFrames = 5;
        private static readonly double StuckSeconds = 1800.0;

        private static bool supported;
        private static bool engaged;
        private static bool holding;
        private static bool releasedThisCycle;
        private static GarbageCollector.Mode restoreTo = GarbageCollector.Mode.Enabled;
        private static double engagedAt;
        private static long engagedHeap;
        private static string lastTag = "未使用";
        private static string detect = "未探测";
        private static string behaviour = "未探测";
        private static string lastError = "";
        private static long enters;
        private static long byPause;
        private static long byCycle;
        private static long byWatchdog;
        private static long rescued;
        private static bool warned;
        private static int frameSkip;
        private static long heapNow;
        private static long heapPeak;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Detect();
            if (!supported) return;

            try
            {
                Type appType = AccessTools.TypeByName("App");
                MethodInfo late = appType == null ? null : AccessTools.Method(appType, "LateUpdate");
                if (late == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 App.LateUpdate，GC 模式门控看门狗未挂载（仍有 finalizer 兜底）");
                    return;
                }
                harmony.Patch(late, postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(GcModeGate), "LateUpdate_Postfix")));
                Debug.Log("[更好的存档] GC 释放策略：每个存档周期回收一次；"
                    + "若该周期内你按过暂停，则提前在暂停的静止画面上回收，"
                    + "存档结束后就不再卡那一下。暂停检测="
                    + (GcTuner.PauseDetectable ? "可用" : "不可用，退化为每周期在存档后回收"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] GC 模式门控看门狗挂载失败: " + e.Message);
            }
        }

        private static void Detect()
        {
            try
            {
                GarbageCollector.Mode before = GarbageCollector.GCMode;
                GarbageCollector.GCMode = GarbageCollector.Mode.Manual;
                GarbageCollector.Mode mid = GarbageCollector.GCMode;
                GarbageCollector.GCMode = before;
                GarbageCollector.Mode after = GarbageCollector.GCMode;
                detect = "初始=" + before + " 置Manual后=" + mid + " 还原后=" + after;
            }
            catch (Exception e)
            {
                detect = "读写失败 " + e.GetType().Name + " " + e.Message;
            }

            bool honoured = false;
            try
            {
                honoured = ProbeDisabled();
            }
            catch (Exception e)
            {
                behaviour = "行为探测失败 " + e.GetType().Name + " " + e.Message;
            }

            supported = honoured;

            if (supported)
            {
                Debug.Log("[更好的存档] GC 模式门控可用：" + detect + " ｜ " + behaviour
                    + "。存档窗口内自动回收已被挡住，窗口结束后按策略放行");
            }
            else
            {
                Debug.LogWarning("[更好的存档] GC 模式门控不可用：" + detect + " ｜ " + behaviour
                    + "。本刀自动停用，存档窗口内仍会有一次自动回收，其余优化不受影响");
            }
        }

        private static int Churn()
        {
            byte[][] junk = new byte[ProbeMb][];
            for (int i = 0; i < ProbeMb; i++) junk[i] = new byte[1048576];
            junk = null;
            return GC.CollectionCount(2);
        }

        private static bool ProbeDisabled()
        {
            int base0 = Churn();
            GC.Collect();
            int base1 = GC.CollectionCount(2);
            bool explicitWorks = base1 > base0;

            GarbageCollector.Mode before = GarbageCollector.GCMode;
            int off0, off1;
            try
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                off0 = Churn();
                GC.Collect();
                off1 = GC.CollectionCount(2);
            }
            finally
            {
                GarbageCollector.GCMode = before;
                GC.Collect();
            }

            behaviour = "显式回收基线 " + base0 + "->" + base1 + (explicitWorks ? "（有效）" : "（未观测到）")
                + "；Disabled 下分配 " + ProbeMb + " MB 并显式回收 " + off0 + "->" + off1
                + (off1 == off0 ? "（被忽略）" : "（仍执行）");

            return explicitWorks && off1 == off0;
        }

        internal static void Enter()
        {
            if (!supported) return;

            if (engaged)
            {
                holding = true;
                releasedThisCycle = false;
                lastTag = "门控 保持Disabled";
                return;
            }

            try
            {
                restoreTo = GarbageCollector.GCMode;
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                engaged = true;
                holding = true;
                releasedThisCycle = false;
                engagedAt = Time.realtimeSinceStartup;
                engagedHeap = SafeHeap();
                enters++;
                lastTag = "门控 " + restoreTo + "->Disabled";
            }
            catch (Exception e)
            {
                engaged = false;
                holding = false;
                supported = false;
                lastError = e.GetType().Name + " " + e.Message;
                lastTag = "门控失效";
                Debug.LogError("[更好的存档] 置为 Disabled 失败，本刀永久停用: " + lastError);
            }
        }

        internal static void AfterSave()
        {
            if (!engaged) return;

            if (releasedThisCycle)
            {
                lastTag = "门控 本周期已在暂停回收";
                return;
            }

            byCycle++;
            Release("每周期回收 堆 " + (heapNow / 1048576) + " MB");
        }

        internal static void ExitForced()
        {
            if (!engaged) return;
            rescued++;
            Release("异常兜底 堆 " + (heapNow / 1048576) + " MB");
        }

        private static void ReEngage()
        {
            if (!supported) return;
            try
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Disabled)
                {
                    lastTag = "门控 暂停回收后未能重新按住";
                    return;
                }
                engaged = true;
                holding = true;
                engagedAt = Time.realtimeSinceStartup;
                engagedHeap = SafeHeap();
                lastTag = "门控 暂停回收后继续按住";
            }
            catch (Exception e)
            {
                lastError = e.GetType().Name + " " + e.Message;
                Debug.LogWarning("[更好的存档] 暂停回收后重新按住失败，本周期余下走自然回收: " + lastError);
            }
        }

        public static void LateUpdate_Postfix()
        {
            if (!supported) return;

            heapNow = SafeHeap();
            if (heapNow > heapPeak) heapPeak = heapNow;

            if (!engaged) return;

            if (++frameSkip < CheckEveryFrames) return;
            frameSkip = 0;

            if (holding && !releasedThisCycle && GcTuner.IsPaused())
            {
                byPause++;
                Release("暂停 堆 " + (heapNow / 1048576) + " MB");
                ReEngage();
                return;
            }

            if (Time.realtimeSinceStartup - engagedAt > StuckSeconds)
            {
                byWatchdog++;
                Release("看门狗超时 " + (int)StuckSeconds + " s 堆 " + (heapNow / 1048576) + " MB");
            }
        }

        private static void Release(string reason)
        {
            engaged = false;
            holding = false;
            releasedThisCycle = true;

            try
            {
                GarbageCollector.GCMode = restoreTo;
                if (GarbageCollector.GCMode != restoreTo)
                {
                    supported = false;
                    lastTag = "门控 还原未生效";
                    Debug.LogError("[更好的存档] 恢复 GC 模式未生效（期望 " + restoreTo + "，实际 "
                        + GarbageCollector.GCMode + "），本刀永久停用以防堆失控");
                    return;
                }
            }
            catch (Exception e)
            {
                lastError = e.GetType().Name + " " + e.Message;
                Debug.LogError("[更好的存档] 恢复 GC 模式失败，强制置 Enabled: " + lastError);
                try { GarbageCollector.GCMode = GarbageCollector.Mode.Enabled; }
                catch (Exception e2) { lastError = e2.GetType().Name + " " + e2.Message; }
                return;
            }

            if (!warned)
            {
                warned = true;
                Debug.Log("[更好的存档] 首次放行回收（" + reason + "）：保持 Disabled 期间堆由 "
                    + (engagedHeap / 1048576) + " MB 涨到 " + (heapNow / 1048576) + " MB");
            }

            double t0 = Now();
            try { GC.Collect(); }
            catch (Exception e) { lastError = e.GetType().Name + " " + e.Message; }
            long ms = (long)(Now() - t0);

            lastTag = "门控 放行（" + reason + "）";
            Debug.Log("[更好的存档] 放行回收：" + reason + "，回收耗时 " + ms + " ms");
        }

        private static long SafeHeap()
        {
            try { return GC.GetTotalMemory(false); }
            catch (Exception) { return 0; }
        }

        private static double Now()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        internal static string Tag()
        {
            return supported ? lastTag : "门控停用";
        }

        internal static string Summary()
        {
            return "[更好的存档] GC 释放策略：" + detect + " ｜ " + behaviour + " ｜ 进入 " + enters
                + " 个周期，放行 " + (byPause + byCycle + byWatchdog + rescued)
                + " 次（暂停 " + byPause + "，周期末 " + byCycle + "，看门狗 " + byWatchdog
                + "，异常 " + rescued + "）｜ 峰值堆 " + (heapPeak / 1048576) + " MB"
                + (lastError.Length > 0 ? "，错误=" + lastError : "");
        }
    }
}
