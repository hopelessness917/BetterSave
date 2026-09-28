using System;
using UnityEngine;
using PeterHan.PLib.Options;

namespace SaveOpt
{
	internal static class BetterSaveSettings
	{
		public static bool ManualGcMode { get; private set; } = false;
		public static int GcMaxTimeSlice { get; private set; } = 3;
		public static bool AutoSaveAllowGC { get; private set; } = false;
		public static bool AutoSaveThumbnail { get; private set; } = false;
		public static bool ManualSaveAllowGC { get; private set; } = true;

		// 启动时调用一次。boot.config 是 gc-max-time-slice 的唯一权威源：
		//   1. 从 config.json 读其它选项（ManualGcMode / AutoSaveThumbnail）
		//   2. 检查 boot.config：
		//      - 有合法值（1-6）：用它覆盖 config.json，让 UI 显示实际值
		//      - 没有 / 值非法：补一行 gc-max-time-slice=3，同步 config.json
		public static void Initialize()
		{
			try
			{
				BetterSaveOptions opts = POptions.ReadSettings<BetterSaveOptions>();
				if (opts == null)
				{
					// 首次启动，config.json 还没生成。用默认值创建一份。
					opts = new BetterSaveOptions();
					POptions.WriteSettings(opts);
				}
				ApplyFromOptions(opts);

				int bootValue;
				if (BootConfig.TryRead(out bootValue))
				{
					ApplyGcMaxTimeSlice(opts, bootValue);
				}
				else
				{
					// boot.config 没有 gc-max-time-slice，或值非法：补默认 3
					BootConfig.Apply(3);
					ApplyGcMaxTimeSlice(opts, 3);
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[更好的存档] 初始化选项失败，使用默认值: " + ex.Message);
			}
		}

		// 玩家在选项界面点“好的”后由 OnOptionsChanged 调用。
		public static void ApplyFromOptions(BetterSaveOptions opts)
		{
			if (opts == null)
			{
				return;
			}
			ManualGcMode = opts.ManualGcMode;
			ManualSaveAllowGC = opts.ManualSaveAllowGC;
			GcMaxTimeSlice = Mathf.Clamp((int)opts.GcMaxTimeSlice, 1, 6);

			// 四档翻译成两个运行时 bool，供 GC_Prefix 和 SaveColonyPreview_Prefix 读取
			switch (opts.AutoSaveExtras)
			{
				case AutoSaveExtrasType.GcOnly:
					AutoSaveAllowGC = true;
					AutoSaveThumbnail = false;
					break;
				case AutoSaveExtrasType.ThumbnailOnly:
					AutoSaveAllowGC = false;
					AutoSaveThumbnail = true;
					break;
				case AutoSaveExtrasType.Both:
					AutoSaveAllowGC = true;
					AutoSaveThumbnail = true;
					break;
				default: // AutoSaveExtrasType.None
					AutoSaveAllowGC = false;
					AutoSaveThumbnail = false;
					break;
			}
		}

		// 把 boot.config 的值同步进内存和 config.json。
		// 只在启动时调用一次，config.json 里已经是目标值就不重复写。
		private static void ApplyGcMaxTimeSlice(BetterSaveOptions opts, int value)
		{
			GcMaxTimeSlice = Mathf.Clamp(value, 1, 6);
			GcMaxTimeSliceType desired = (GcMaxTimeSliceType)GcMaxTimeSlice;
			if (opts.GcMaxTimeSlice == desired)
			{
				return;
			}
			try
			{
				opts.GcMaxTimeSlice = desired;
				POptions.WriteSettings(opts);
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[更好的存档] 同步 gc-max-time-slice 到 config.json 失败: " + ex.Message);
			}
		}
	}
}