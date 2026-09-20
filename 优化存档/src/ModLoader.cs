using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using KMod;

namespace SaveOpt
{
    public class ModLoader : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            try
            {
                Debug.Log("[优化存档] OnLoad 开始");
                Sink.Start();
                SavePatch.Apply(harmony);
                SerializerPatch.Apply(harmony);
                IsDefinedPatch.Apply(harmony);
                GcTuner.Apply(harmony);
                ThumbnailAsync.Apply(harmony);

                MethodInfo quit = AccessTools.Method(typeof(Game), "OnApplicationQuit");
                if (quit != null)
                {
                    harmony.Patch(quit, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ModLoader), "OnApplicationQuit_Prefix")));
                    Debug.Log("[优化存档] Game.OnApplicationQuit 已挂载（退出前 flush）");
                }
                else
                {
                    Debug.LogWarning("[优化存档] 找不到 Game.OnApplicationQuit，退出前可能丢失未落盘的存档");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[优化存档] OnLoad 失败: " + e);
            }
        }

        public static void OnApplicationQuit_Prefix()
        {
            Sink.Flush(15000);
            Sink.Stop();
            ThumbnailAsync.Flush(5000);
            ThumbnailAsync.Stop();
            Debug.Log(SavePatch.Summary());
            Debug.Log(SerializerPatch.Summary());
            Debug.Log(IsDefinedCache.Summary());
            Debug.Log(SaveBuffer.Summary());
            Debug.Log(FieldPlanner.Summary());
            Debug.Log(GcTuner.Summary());
            Debug.Log(ThumbnailAsync.Summary());
        }
    }
}
