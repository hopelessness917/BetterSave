using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KMod;

namespace SaveOpt
{
    internal static class Diag
    {
        internal static readonly bool Verbose = false;

        internal static void Trace(string message)
        {
            if (Verbose) Debug.Log(message);
        }

        internal static void Trace(string message, bool important)
        {
            if (Verbose || important) Debug.Log(message);
        }
    }

    public class ModLoader : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            try
            {
                Sink.Start();
                InstallQuitHook(harmony);

                List<string> mounted = new List<string>();
                Mount(mounted, "存档管线", SavePatch.Apply, harmony);
                Mount(mounted, "序列化委托", SerializerPatch.Apply, harmony);
                Mount(mounted, "IsDefined缓存", IsDefinedPatch.Apply, harmony);
                Mount(mounted, "GC门控", GcModeGate.Apply, harmony);
                Mount(mounted, "体感监控", FrameWatch.Apply, harmony);
                Mount(mounted, "缩略图后台", ThumbnailAsync.Apply, harmony);
                Mount(mounted, "序列化替换", SaveTransform.Apply, harmony);
                GcTuner.Apply(harmony);

                Debug.Log("[更好的存档] 已加载（" + string.Join(" ", mounted.ToArray()) + "）"
                    + (Diag.Verbose ? " ｜ 诊断日志=开" : ""));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] OnLoad 失败: " + e);
            }
        }

        private static void Mount(List<string> list, string name, Func<HarmonyLib.Harmony, bool> apply, HarmonyLib.Harmony harmony)
        {
            try
            {
                if (apply(harmony)) list.Add(name);
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] " + name + " 挂载失败，其余组件继续运行: " + e);
            }
        }

        private static void InstallQuitHook(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo quit = AccessTools.Method(typeof(Game), "OnApplicationQuit");
                if (quit == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 Game.OnApplicationQuit，退出前可能丢失未落盘的存档");
                    return;
                }
                harmony.Patch(quit, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ModLoader), "OnApplicationQuit_Prefix")));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] 退出钩子挂载失败: " + e);
            }
        }

        public static void OnApplicationQuit_Prefix()
        {
            Sink.Flush(15000);
            Sink.Stop();
            ThumbnailAsync.Flush(5000);
            ThumbnailAsync.Stop();
            Debug.Log(SavePatch.Summary());
            Debug.Log(GcModeGate.Summary());
            Debug.Log(SaveBuffer.Summary());
            Debug.Log(ThumbnailAsync.Summary());
            Debug.Log(SaveTransform.Summary());
            Debug.Log(FrameWatch.Summary());
            Debug.Log(GcTuner.Summary());
            if (Diag.Verbose)
            {
                Debug.Log(SerializerPatch.Summary());
                Debug.Log(IsDefinedCache.Summary());
                Debug.Log(FieldPlanner.Summary());
            }
        }
    }
}
