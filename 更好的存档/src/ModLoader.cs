using System;
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

                string mounted = "";
                mounted += SavePatch.Apply(harmony) ? "存档管线" : "";
                mounted += SerializerPatch.Apply(harmony) ? " 序列化委托" : "";
                mounted += IsDefinedPatch.Apply(harmony) ? " IsDefined缓存" : "";
                GcTuner.Apply(harmony);
                mounted += GcModeGate.Apply(harmony) ? " GC门控" : "";
                mounted += FrameWatch.Apply(harmony) ? " 体感监控" : "";
                mounted += ThumbnailAsync.Apply(harmony) ? " 缩略图后台" : "";
                Prof.Apply(harmony);

                MethodInfo quit = AccessTools.Method(typeof(Game), "OnApplicationQuit");
                if (quit != null)
                {
                    harmony.Patch(quit, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ModLoader), "OnApplicationQuit_Prefix")));
                }
                else
                {
                    Debug.LogWarning("[更好的存档] 找不到 Game.OnApplicationQuit，退出前可能丢失未落盘的存档");
                }

                Debug.Log("[更好的存档] 已加载（" + mounted.Trim() + "）"
                    + (Diag.Verbose ? " ｜ 诊断日志=开" : ""));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] OnLoad 失败: " + e);
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
            if (Diag.Verbose)
            {
                Debug.Log(SerializerPatch.Summary());
                Debug.Log(IsDefinedCache.Summary());
                Debug.Log(FieldPlanner.Summary());
                Debug.Log(GcTuner.Summary());
                Debug.Log(FrameWatch.Summary());
            }
        }
    }
}
