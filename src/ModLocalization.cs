using System;
using System.Collections.Generic;
using System.IO;
using KMod;
using UnityEngine;

namespace SaveOpt
{
	internal static class ModLocalization
	{
		internal static void Load(Mod mod)
		{
			if (mod == null)
			{
				Debug.LogWarning("[更好的存档] Mod 实例为空，跳过本地化加载");
				return;
			}
			try
			{
				string dir = Path.Combine(mod.ContentPath, "translations");
				// 先加载英文做兜底，再用当前语言覆盖
				LoadCatalog(Path.Combine(dir, "en.po"));
				string lang = NormalizeLanguage(Localization.GetCurrentLanguageCode());
				if (lang != "en")
				{
					LoadCatalog(Path.Combine(dir, lang + ".po"));
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[更好的存档] 本地化加载失败: " + ex.Message);
			}
		}

		private static void LoadCatalog(string path)
		{
			if (!File.Exists(path))
			{
				return;
			}
			try
			{
				foreach (KeyValuePair<string, string> kv in Localization.LoadStringsFile(path, false))
				{
					if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
					{
						Strings.Add(kv.Key, kv.Value);
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[更好的存档] 加载 " + path + " 失败: " + ex.Message);
			}
		}

		private static string NormalizeLanguage(string code)
		{
			if (string.IsNullOrWhiteSpace(code))
			{
				return "en";
			}
			string c = code.Trim().ToLowerInvariant().Replace('_', '-');
			if (c.StartsWith("zh"))
			{
				return "zh";
			}
			return "en";
		}
	}
}