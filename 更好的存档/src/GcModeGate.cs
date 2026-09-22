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
        private const float EmergencyFraction = 0.90f;
        private static readonly long HeapThresholdBytes = 8L * 1024 * 1024 * 1024;
        private static readonly double StuckSeconds = 1800.0;

        private static bool supported;
        private static bool engaged;
        private static bool holding;
        private static GarbageCollector.Mode restoreTo = GarbageCollector.Mode.Enabled;
        private static double engagedAt;
        private static long engagedHeap;
        private static string lastTag = "未使用";
        private static string detect = "未探测";
        private static string behaviour = "未探测";
        private static string lastError = "";
        private static long enters;
        private static long exits;
        private static long rescued;
        private static long byPause;
        private static long byThreshold;
        private static long byEmergency;
        private static long byWatchdog;
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
                Debug.Log("[更好的存档] GC 模式门控释放策略：暂停时回收 ｜ 堆达 "
                    + (HeapThresholdBytes / 1048576) + " MB 时在下次存档后回收 ｜ 达本机物理内存 "
                    + (int)(EmergencyFraction * 100) + "% 时立即回收并弹窗 ｜ 看门狗兜底 "
                    + (int)StuckSeconds + " s");
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
                    + "。存档窗口内自动回收已被挡住，窗口结束后按策略决定何时放行");
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
                lastTag = "门控 保持Disabled";
                return;
            }

            try
            {
                restoreTo = GarbageCollector.GCMode;
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                engaged = true;
                holding = true;
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

            long heap = SafeHeap();
            if (heap >= HeapThresholdBytes)
            {
                byThreshold++;
                Release("阈值 堆 " + (heap / 1048576) + " MB 已达 " + (HeapThresholdBytes / 1048576) + " MB");
                return;
            }

            lastTag = "门控 保持Disabled（堆 " + (heap / 1048576) + " MB）";
        }

        internal static void ExitForced()
        {
            if (!engaged) return;
            Release("异常兜底");
            rescued++;
        }

        public static void LateUpdate_Postfix()
        {
            if (!supported) return;

            heapNow = SafeHeap();
            if (heapNow > heapPeak) heapPeak = heapNow;

            if (!engaged) return;

            if (++frameSkip < CheckEveryFrames) return;
            frameSkip = 0;

            if (holding && GcTuner.IsPaused())
            {
                byPause++;
                Release("暂停 堆 " + (heapNow / 1048576) + " MB");
                return;
            }

            long total = (long)SystemInfo.systemMemorySize * 1048576L;
            if (total > 0 && heapNow >= (long)(total * EmergencyFraction))
            {
                byEmergency++;
                Release("紧急 托管堆 " + (heapNow / 1048576) + " MB / 物理内存 "
                    + (total / 1048576) + " MB");
                Popup(heapNow / 1048576, total / 1048576);
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
            exits++;

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

            if (!warned && (byPause + byThreshold + byEmergency + byWatchdog) == 1)
            {
                warned = true;
                Debug.Log("[更好的存档] 首次放行回收（" + reason + "）：保持 Disabled 期间堆由 "
                    + (engagedHeap / 1048576) + " MB 涨到 " + (heapNow / 1048576) + " MB");
            }

            double t0 = Now();
            try { GC.Collect(); }
            catch (Exception e) { lastError = e.GetType().Name + " " + e.Message; }
            long ms = (long)(Now() - t0);

            lastTag = "门控 放行回收（" + reason + "，耗时 " + ms + " ms）";
            Debug.Log("[更好的存档] 放行回收：" + reason + "，回收耗时 " + ms + " ms");
        }

        private static void Popup(long heapMb, long totalMb)
        {
            try
            {
                ConfirmDialogScreen dlg = Util.KInstantiateUI<ConfirmDialogScreen>(
                    ScreenPrefabs.Instance.ConfirmDialogScreen.gameObject,
                    Global.Instance.globalCanvas, true);
                if (dlg == null)
                {
                    Debug.LogError("[更好的存档] 内存告警弹窗实例化失败");
                    return;
                }
                dlg.PopupConfirmDialog(
                    "托管内存已达本机物理内存的 " + (int)(EmergencyFraction * 100) + "%（"
                    + heapMb + " MB / " + totalMb + " MB）。\n\n"
                    + "已立即强制执行一次回收以避免游戏崩溃。建议立刻手动存档并重启游戏。",
                    delegate { dlg.Deactivate(); },
                    null, "更好的存档 · 内存告警", null, null, null, "知道了", null);
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] 内存告警弹窗失败: " + e.GetType().Name + " " + e.Message);
            }
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
            return "[更好的存档] GC 模式门控：" + detect + " ｜ " + behaviour + " ｜ 进入 " + enters
                + " 次，放行 " + exits + " 次（暂停 " + byPause + "，阈值 " + byThreshold
                + "，紧急 " + byEmergency + "，看门狗 " + byWatchdog + "，异常 " + rescued
                + "）｜ 峰值堆 " + (heapPeak / 1048576) + " MB"
                + (lastError.Length > 0 ? "，错误=" + lastError : "");
        }
    }
}
