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
        private static readonly double ForceSeconds = 600.0;
        private static readonly double StuckSeconds = 1800.0;
        private static readonly double MinReleaseGapSeconds = 5.0;

        private static bool supported;
        private static bool engaged;
        private static GarbageCollector.Mode restoreTo = GarbageCollector.Mode.Enabled;
        private static double engagedAt;
        private static long engagedHeap;
        private static string lastTag = "未使用";
        private static string detect = "未探测";
        private static string behaviour = "未探测";
        private static string gen = "未探测";
        private static string genVerdict = "未判定";
        private static string lastError = "";
        private static long enters;
        private static long byForce;
        private static long byWatchdog;
        private static long rescued;
        private static double lastCollectAt;
        private static long suppressed;
        private static bool warned;
        private static int frameSkip;
        private static long heapNow;
        private static long heapPeak;

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            Detect();
            if (!supported) return false;

            try
            {
                MethodInfo late = AccessTools.Method(typeof(App), "LateUpdate");
                if (late == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 App.LateUpdate，GC 模式门控看门狗未挂载（仍有 finalizer 兜底）");
                    return false;
                }
                harmony.Patch(late, postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(GcModeGate), "LateUpdate_Postfix")));
                Debug.Log("[更好的存档] GC 释放策略：整周期按住 Disabled，存档窗口因此恒定不受回收影响；"
                    + "唯一放行条件是距上次回收满 " + (int)(ForceSeconds / 60) + " 分钟（暂停不再触发回收），"
                    + "看门狗 " + (int)StuckSeconds + " s 仅作机制兜底");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] GC 模式门控看门狗挂载失败: " + e.Message);
                return false;
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

            try
            {
                ProbeGenerations();
            }
            catch (Exception e)
            {
                gen = "分代探测失败 " + e.GetType().Name + " " + e.Message;
            }

            supported = honoured;

            Debug.Log("[更好的存档] 分代探测：" + gen + " -> " + genVerdict);

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

        private static void ProbeGenerations()
        {
            Churn();
            int a0 = GC.CollectionCount(0), a1 = GC.CollectionCount(1), a2 = GC.CollectionCount(2);
            long ha0 = SafeHeap();
            double t1 = Now();
            GC.Collect(1);
            double ms1 = Now() - t1;
            int b0 = GC.CollectionCount(0), b1 = GC.CollectionCount(1), b2 = GC.CollectionCount(2);
            long ha1 = SafeHeap();

            Churn();
            int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
            long hb0 = SafeHeap();
            double t2 = Now();
            GC.Collect();
            double ms2 = Now() - t2;
            int d0 = GC.CollectionCount(0), d1 = GC.CollectionCount(1), d2 = GC.CollectionCount(2);
            long hb1 = SafeHeap();

            gen = "Collect(1) 增量 " + (b0 - a0) + "/" + (b1 - a1) + "/" + (b2 - a2)
                + "，堆降 " + ((ha0 - ha1) / 1048576) + " MB，耗时 " + ms1.ToString("F0") + " ms"
                + " ｜ Collect() 增量 " + (d0 - c0) + "/" + (d1 - c1) + "/" + (d2 - c2)
                + "，堆降 " + ((hb0 - hb1) / 1048576) + " MB，耗时 " + ms2.ToString("F0") + " ms";

            genVerdict = (b2 - a2) < (d2 - c2)
                ? "★ 分代存在：Collect(1) 未推进第 2 代，可用它做廉价回收"
                : "分代不存在：Collect(1) 与 Collect() 同样推进三代，后者不可替代";
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
                lastTag = "门控 保持Disabled";
                return;
            }

            try
            {
                restoreTo = GarbageCollector.GCMode;
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                engaged = true;
                engagedAt = Time.realtimeSinceStartup;
                if (lastCollectAt <= 0) lastCollectAt = engagedAt;
                engagedHeap = SafeHeap();
                enters++;
                lastTag = "门控 " + restoreTo + "->Disabled";
            }
            catch (Exception e)
            {
                engaged = false;
                supported = false;
                lastError = e.GetType().Name + " " + e.Message;
                lastTag = "门控失效";
                Debug.LogError("[更好的存档] 置为 Disabled 失败，本刀永久停用: " + lastError);
                try
                {
                    if (GarbageCollector.GCMode == GarbageCollector.Mode.Disabled)
                    {
                        GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                        Debug.LogWarning("[更好的存档] 置位异常后检测到 Disabled，已还原 Enabled");
                    }
                }
                catch (Exception) { }
            }
        }

        internal static void AfterSave()
        {
            if (!engaged) return;
            lastTag = "门控 保持Disabled（堆 " + (heapNow / 1048576) + " MB，距上次回收 "
                + ((Time.realtimeSinceStartup - lastCollectAt)).ToString("F0") + " s）";
        }

        internal static void ExitForced()
        {
            if (!engaged) return;
            if (Release("异常兜底 堆 " + (heapNow / 1048576) + " MB", true)) rescued++;
        }

        private static void ReEngage()
        {
            if (!supported) return;
            try
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Disabled)
                {
                    lastTag = "门控 回收后未能重新按住";
                    return;
                }
                engaged = true;
                engagedAt = Time.realtimeSinceStartup;
                engagedHeap = SafeHeap();
                lastTag = "门控 回收后继续按住";
            }
            catch (Exception e)
            {
                lastError = e.GetType().Name + " " + e.Message;
                Debug.LogWarning("[更好的存档] 回收后重新按住失败，本周期余下走自然回收: " + lastError);
            }
        }

        public static void LateUpdate_Postfix()
        {
            if (++frameSkip < CheckEveryFrames) return;
            frameSkip = 0;

            VerifyReleased();

            if (!supported) return;

            heapNow = SafeHeap();
            if (heapNow > heapPeak) heapPeak = heapNow;

            if (!engaged) return;

            if (Time.realtimeSinceStartup - lastCollectAt >= ForceSeconds)
            {
                if (Release("连续 " + (int)(ForceSeconds / 60) + " 分钟未回收，强制回收 堆 "
                    + (heapNow / 1048576) + " MB", false))
                {
                    byForce++;
                    ReEngage();
                }
                return;
            }

            if (Time.realtimeSinceStartup - engagedAt > StuckSeconds)
            {
                if (Release("看门狗超时 " + (int)StuckSeconds + " s 堆 " + (heapNow / 1048576) + " MB", true))
                {
                    byWatchdog++;
                }
            }
        }

        private static void VerifyReleased()
        {
            if (enters <= 0 || engaged) return;
            try
            {
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Disabled) return;
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                if (GarbageCollector.GCMode == GarbageCollector.Mode.Enabled)
                {
                    rescued++;
                    Debug.LogError("[更好的存档] 检测到门控已判定释放但 GC 模式仍为 Disabled，已强制恢复 Enabled");
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool Release(string reason, bool forced)
        {
            double since = Time.realtimeSinceStartup - lastCollectAt;
            if (!forced && lastCollectAt > 0 && since < MinReleaseGapSeconds)
            {
                suppressed++;
                if (suppressed == 1)
                {
                    Debug.LogWarning("[更好的存档] 放行被节流：距上次回收仅 " + since.ToString("F1")
                        + " s（下限 " + MinReleaseGapSeconds + " s），已忽略本次请求：" + reason);
                }
                return false;
            }

            lastCollectAt = Time.realtimeSinceStartup;

            try
            {
                GarbageCollector.GCMode = restoreTo;
                if (GarbageCollector.GCMode != restoreTo)
                {
                    Debug.LogError("[更好的存档] 恢复 GC 模式未生效（期望 " + restoreTo + "，实际 "
                        + GarbageCollector.GCMode + "），保持 engaged 以便看门狗继续重试");
                    lastTag = "门控 还原未生效，仍在重试";
                    return false;
                }
            }
            catch (Exception e)
            {
                lastError = e.GetType().Name + " " + e.Message;
                Debug.LogError("[更好的存档] 恢复 GC 模式失败，保持 engaged 以便看门狗继续重试: " + lastError);
                try { GarbageCollector.GCMode = GarbageCollector.Mode.Enabled; }
                catch (Exception e2) { lastError = e2.GetType().Name + " " + e2.Message; }
                if (GarbageCollector.GCMode == GarbageCollector.Mode.Disabled) return false;
            }

            engaged = false;

            if (!warned)
            {
                warned = true;
                Diag.Trace("[更好的存档] 首次放行回收（" + reason + "）：保持 Disabled 期间堆由 "
                    + (engagedHeap / 1048576) + " MB 涨到 " + (heapNow / 1048576) + " MB");
            }

            double t0 = Now();
            try { GC.Collect(); }
            catch (Exception e) { lastError = e.GetType().Name + " " + e.Message; }
            long ms = (long)(Now() - t0);

            lastTag = "门控 放行（" + reason + "）";
            Debug.Log("[更好的存档] 放行回收：" + reason + "，回收耗时 " + ms + " ms");
            return true;
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
                + " 个周期，放行 " + (byForce + byWatchdog + rescued)
                + " 次（强制 " + byForce + "，看门狗 " + byWatchdog
                + "，异常 " + rescued + "，节流 " + suppressed + "）｜ 峰值堆 " + (heapPeak / 1048576) + " MB"
                + " ｜ 分代：" + genVerdict
                + (lastError.Length > 0 ? "，错误=" + lastError : "");
        }
    }
}
