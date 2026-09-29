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
                LoadCatalog(Path.Combine(dir, "en.po"));
                string lang = NormalizeLanguage(Localization.GetCurrentLanguageCode());
                if (lang != "en")
                {
                    LoadCatalog(Path.Combine(dir, lang + ".po"));
                }
                Debug.Log("[更好的存档] 本地化已加载：语言 " + lang + "，目录 " + dir
                    + "（en.po 兜底 + " + lang + ".po 覆盖；缺文件时面板会显示 STRINGS.* 原始键名）");
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
                Debug.LogWarning("[更好的存档] 缺少翻译文件: " + path + "（该语言下的选项文案将显示为原始键名）");
                return;
            }
            try
            {
                int added = 0;
                foreach (KeyValuePair<string, string> kv in Localization.LoadStringsFile(path, false))
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    {
                        Strings.Add(kv.Key, kv.Value);
                        added++;
                    }
                }
                Debug.Log("[更好的存档] 翻译文件 " + Path.GetFileName(path) + " 已注入 " + added + " 条");
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
