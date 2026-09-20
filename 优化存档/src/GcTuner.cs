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
        private const long SliceNanoseconds = 500000L;
        private const long DebtThresholdBytes = 160L * 1024 * 1024;
        private const int CheckEveryFrames = 15;
        private const double MinIntervalSeconds = 90.0;

        private static readonly bool InterventionEnabled = false;

        private static bool incremental;
        private static bool driving;
        private static bool opportunistic = true;
        private static string env = "未探测";

        private static long slices;
        private static long completed;
        private static long skipped;
        private static long frames;
        private static double frameMs;

        private static int frameCounter;
        private static long baseline;
        private static double lastCollectAt;
        private static long opportunisticCount;
        private static double opportunisticMs;
        private static long peakDebt;

        private static Type speedType;
        private static PropertyInfo instanceProp;
        private static PropertyInfo pausedProp;
        private static FieldInfo focusField;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                incremental = GarbageCollector.isIncremental;
                env = "isIncremental=" + incremental
                    + " GCMode=" + GarbageCollector.GCMode
                    + " vSync=" + QualitySettings.vSyncCount
                    + " targetFrameRate=" + Application.targetFrameRate;
            }
            catch (Exception e)
            {
                env = "探测失败: " + e.GetType().Name + " " + e.Message;
            }
            Debug.Log("[优化存档] GC 环境: " + env);

            ResolveApis();

            if (!InterventionEnabled)
            {
                opportunistic = false;
                Debug.Log("[优化存档] 自动干预已停用（第七刀实测无收益：23 分钟内执行 2 次，"
                    + "阻止存档 GC 0 次、却让总回收次数从 9 增到 11）。"
                    + "仍保留每轮存档的 GC / 堆 / CPU 监控");
                return;
            }

            try
            {
                Type appType = AccessTools.TypeByName("App");
                MethodInfo late = appType == null ? null : AccessTools.Method(appType, "LateUpdate");
                if (late == null)
                {
                    Debug.LogWarning("[优化存档] 找不到 App.LateUpdate，第六/七刀无法驱动");
                    opportunistic = false;
                    return;
                }
                harmony.Patch(late, postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(GcTuner), "LateUpdate_Postfix")));
                driving = true;
            }
            catch (Exception e)
            {
                Debug.LogError("[优化存档] 挂载 App.LateUpdate 失败: " + e.Message);
                opportunistic = false;
                return;
            }

            baseline = SafeHeap();
            Debug.Log("[优化存档] App.LateUpdate 已挂载（" + (incremental ? "每帧驱动增量 GC" : "机会性回收：暂停或失焦时还债") + "）");
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

            try
            {
                Type appType = AccessTools.TypeByName("App");
                focusField = appType == null ? null : AccessTools.Field(appType, "hasFocus");
            }
            catch (Exception) { }

            Debug.Log("[优化存档] 机会性回收探测: 暂停=" + (instanceProp != null && pausedProp != null)
                + "，失焦=" + (focusField != null)
                + "，阈值=" + (DebtThresholdBytes / 1048576) + " MB，最短间隔=" + (int)MinIntervalSeconds + " s");
        }

        private static long SafeHeap()
        {
            try { return GC.GetTotalMemory(false); }
            catch (Exception) { return 0; }
        }

        private static bool IsPaused()
        {
            if (instanceProp == null || pausedProp == null) return false;
            object screen = instanceProp.GetValue(null, null);
            if (screen == null) return false;
            object v = pausedProp.GetValue(screen, null);
            return v is bool && (bool)v;
        }

        private static bool HasFocus()
        {
            if (focusField == null) return true;
            object v = focusField.GetValue(null);
            return !(v is bool) || (bool)v;
        }

        public static void LateUpdate_Postfix()
        {
            if (!driving) return;

            float dt = Time.unscaledDeltaTime;
            if (dt > 0f && dt < 1f)
            {
                frames++;
                frameMs += dt * 1000.0;
            }

            if (SavePatch.InSave)
            {
                skipped++;
                return;
            }

            if (incremental)
            {
                try
                {
                    slices++;
                    if (GarbageCollector.CollectIncremental(SliceNanoseconds)) completed++;
                }
                catch (Exception)
                {
                    driving = false;
                }
                return;
            }

            if (!opportunistic) return;
            if (++frameCounter < CheckEveryFrames) return;
            frameCounter = 0;
            TryOpportunistic();
        }

        private static void TryOpportunistic()
        {
            try
            {
                if (savesSeen == 0) return;

                bool paused = IsPaused();
                if (!paused && HasFocus()) return;
                opportunities++;

                double now = Time.realtimeSinceStartup;
                if (now - lastCollectAt < MinIntervalSeconds)
                {
                    blockedShort++;
                    return;
                }

                long heap = SafeHeap();
                long debt = heap - baseline;
                if (debt > peakDebt) peakDebt = debt;
                if (debt < DebtThresholdBytes)
                {
                    blockedSmall++;
                    return;
                }

                var sw = Stopwatch.StartNew();
                GC.Collect();
                sw.Stop();
                long after = SafeHeap();
                lastCollectAt = now;
                baseline = after;
                opportunisticCount++;
                opportunisticMs += sw.Elapsed.TotalMilliseconds;
                Debug.Log("[优化存档] 机会性回收" + (paused ? "（游戏已暂停）" : "（窗口失焦）")
                    + "：还债 " + (debt / 1048576) + " MB，耗时 " + ((long)sw.Elapsed.TotalMilliseconds)
                    + " ms，堆 " + (heap / 1048576) + " -> " + (after / 1048576) + " MB");
            }
            catch (Exception)
            {
                opportunistic = false;
            }
        }

        internal static void BeginSave()
        {
            c0 = GC.CollectionCount(0);
            c1 = GC.CollectionCount(1);
            c2 = GC.CollectionCount(2);
            heapBefore = SafeHeap();
            wallStart = Time.realtimeSinceStartup;
            try { cpuStart = Process.GetCurrentProcess().TotalProcessorTime; }
            catch (Exception) { cpuStart = TimeSpan.MinValue; }
            savesSeen++;
            if (savesSeen == 1)
            {
                baseline = heapBefore;
                lastCollectAt = Time.realtimeSinceStartup;
            }
        }

        private static double wallStart;
        private static TimeSpan cpuStart;
        private static long savesSeen;
        private static long opportunities;
        private static long blockedShort;
        private static long blockedSmall;
        private static int c0;
        private static int c1;
        private static int c2;
        private static long heapBefore;

        internal static string SaveSummary()
        {
            long after = SafeHeap();
            long debt = after - baseline;
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
            return "[优化存档] 本轮存档期间 GC: 第0代 +" + (GC.CollectionCount(0) - c0)
                + "，第1代 +" + (GC.CollectionCount(1) - c1)
                + "，第2代 +" + (GC.CollectionCount(2) - c2)
                + " ｜ 堆 " + (heapBefore / 1048576) + " -> " + (after / 1048576)
                + " MB（相对上次回收 +" + (debt / 1048576) + " MB）"
                + " ｜ CPU " + cores + "（墙钟 " + wall.ToString("F0") + " ms）";
        }

        internal static string Summary()
        {
            if (!InterventionEnabled)
            {
                return "[优化存档] 监控已结束：自动干预停用 ｜ GC 环境: " + env;
            }

            var sb = new System.Text.StringBuilder("[优化存档] ");
            sb.Append(incremental
                ? ("第六刀（增量 GC 驱动）：时间片 " + slices + " 次，完成整轮 " + completed + " 次")
                : ("第七刀（机会性回收）：执行 " + opportunisticCount + " 次，累计 " + ((long)opportunisticMs)
                    + " ms ｜ 机会出现 " + opportunities + " 次，其中被时间挡住 " + blockedShort
                    + " 次、被债务阈值挡住 " + blockedSmall + " 次 ｜ 观测最大债务 " + (peakDebt / 1048576) + " MB"));
            sb.Append("，存档期间跳过 ").Append(skipped).Append(" 帧，平均帧 ")
              .Append(frames > 0 ? (frameMs / frames).ToString("F2") : "?").Append(" ms（").Append(frames).Append(" 帧）");
            sb.Append(" ｜ GC 环境: ").Append(env);
            return sb.ToString();
        }
    }
}
