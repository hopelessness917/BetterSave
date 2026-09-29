using System;
using PeterHan.PLib.Options;
using UnityEngine;

namespace SaveOpt
{
    internal static class ModOptions
    {
        private const int MinMinutes = 1;
        private const int MaxMinutes = 20;

        private static BetterSaveOptions settings;
        private static bool gcAuto;
        private static int releaseMinutes = 10;
        private static bool realScreenshot;
        private static bool collectAfterSave;

        internal static bool GcAuto
        {
            get { return gcAuto; }
        }

        internal static int ReleaseMinutes
        {
            get { return releaseMinutes; }
        }

        internal static bool RealScreenshot
        {
            get { return realScreenshot; }
        }

        internal static bool CollectAfterSave
        {
            get { return collectAfterSave || gcAuto; }
        }

        internal static void Load()
        {
            try
            {
                settings = POptions.ReadSettings<BetterSaveOptions>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 读取选项失败，使用默认值: " + e.Message);
            }
            if (settings == null) settings = new BetterSaveOptions();
            Apply(settings.GcMode == GcModeType.Auto, settings.GcReleaseMinutes, settings.RealScreenshot,
                settings.CollectAfterSave, settings.Verbose, false);
            Debug.Log(Summary());
        }

        internal static void ApplyFromOptions(BetterSaveOptions options)
        {
            if (options == null) return;
            settings = options;
            Apply(options.GcMode == GcModeType.Auto, options.GcReleaseMinutes, options.RealScreenshot,
                options.CollectAfterSave, options.Verbose, false);
            Debug.Log("[更好的存档] 选项已应用 ｜ " + Summary());
        }

        private static void Apply(bool auto, int minutes, bool screenshot, bool afterSave,
            bool verbose, bool persist)
        {
            gcAuto = auto;
            releaseMinutes = Clamp(minutes);
            realScreenshot = screenshot;
            collectAfterSave = afterSave;
            Diag.Verbose = verbose;
            if (persist) Persist();
        }

        private static void Persist()
        {
            try
            {
                if (settings == null) return;
                settings.GcMode = gcAuto ? GcModeType.Auto : GcModeType.Manual;
                settings.GcReleaseMinutes = releaseMinutes;
                settings.RealScreenshot = realScreenshot;
                settings.CollectAfterSave = collectAfterSave;
                settings.Verbose = Diag.Verbose;
                POptions.WriteSettings(settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 保存选项失败，本次改动只在当前运行内有效: " + e.Message);
            }
        }

        private static string Summary()
        {
            return "配置 ｜ 1.GC方式 " + (gcAuto ? "自动" : "手动")
                + " ｜ 2.GC回收时间 " + releaseMinutes + " 分钟"
                + (gcAuto ? "（自动模式下此项无效）" : "")
                + " ｜ 3.存档后回收GC " + (CollectAfterSave ? "开" : "关")
                + (gcAuto ? "（自动模式下此项无效）" : "")
                + " ｜ 4.真实截图 " + (realScreenshot ? "开" : "关")
                + " ｜ 5.诊断日志 " + (Diag.Verbose ? "开" : "关");
        }

        private static int Clamp(int v)
        {
            if (v < MinMinutes) return MinMinutes;
            if (v > MaxMinutes) return MaxMinutes;
            return v;
        }
    }
}
