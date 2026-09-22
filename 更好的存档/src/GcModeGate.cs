using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace SaveOpt
{
    internal static class GcModeGate
    {
        private const double StuckSeconds = 30.0;

        private static bool supported;
        private static bool engaged;
        private static GarbageCollector.Mode restoreTo = GarbageCollector.Mode.Enabled;
        private static double engagedAt;
        private static string lastTag = "未使用";
        private static string detect = "未探测";
        private static string lastError = "";
        private static long enters;
        private static long exits;
        private static long rescued;

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
                Debug.Log("[更好的存档] GC 模式门控看门狗已挂载（超时 " + (int)StuckSeconds + " s）");
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
                supported = mid == GarbageCollector.Mode.Manual && after == before;
                detect = "初始=" + before + " 置Manual后=" + mid + " 还原后=" + after;
            }
            catch (Exception e)
            {
                supported = false;
                detect = "不可用 " + e.GetType().Name + " " + e.Message;
            }

            if (supported)
            {
                Debug.Log("[更好的存档] GC 模式门控探测通过：" + detect
                    + "。存档窗口内自动回收已被挡住，游戏显式 GC.Collect 不受影响");
            }
            else
            {
                Debug.LogWarning("[更好的存档] GC 模式门控探测失败：" + detect + "。本刀自动停用，其余优化不受影响");
            }
        }

        internal static void Enter()
        {
            if (!supported) return;

            if (engaged)
            {
                rescued++;
                Debug.LogWarning("[更好的存档] 上一次存档未正常恢复 GC 模式，本次进入前强制恢复");
                Restore();
            }

            try
            {
                restoreTo = GarbageCollector.GCMode;
                GarbageCollector.GCMode = GarbageCollector.Mode.Manual;
                engaged = true;
                engagedAt = Time.realtimeSinceStartup;
                enters++;
                lastTag = "门控 " + restoreTo + "->Manual";
            }
            catch (Exception e)
            {
                engaged = false;
                supported = false;
                lastError = e.GetType().Name + " " + e.Message;
                lastTag = "门控失效";
                Debug.LogError("[更好的存档] 置为 Manual 失败，本刀永久停用: " + lastError);
            }
        }

        internal static void Exit()
        {
            if (!engaged) return;
            string was = restoreTo.ToString();
            Restore();
            if (supported) lastTag = "门控 Manual->" + was;
        }

        private static void Restore()
        {
            engaged = false;
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
                }
            }
            catch (Exception e)
            {
                lastError = e.GetType().Name + " " + e.Message;
                Debug.LogError("[更好的存档] 恢复 GC 模式失败，强制置 Enabled: " + lastError);
                try
                {
                    GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                }
                catch (Exception e2)
                {
                    lastError = e2.GetType().Name + " " + e2.Message;
                }
            }
        }

        public static void LateUpdate_Postfix()
        {
            if (!engaged) return;
            if (Time.realtimeSinceStartup - engagedAt < StuckSeconds) return;
            rescued++;
            Debug.LogWarning("[更好的存档] GC 模式门控超时 " + (int)StuckSeconds + " s 未恢复，看门狗强制恢复");
            Restore();
            lastTag = "门控 看门狗恢复";
        }

        internal static string Tag()
        {
            return supported ? lastTag : "门控停用";
        }

        internal static string Summary()
        {
            return "[更好的存档] GC 模式门控：" + detect + " ｜ 进入 " + enters + " 次，恢复 " + exits
                + " 次，兜底救回 " + rescued + " 次" + (lastError.Length > 0 ? "，错误=" + lastError : "");
        }
    }
}
