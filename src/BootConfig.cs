using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SaveOpt
{
	internal static class BootConfig
	{
		private const string Key = "gc-max-time-slice";

		// 尝试读取 boot.config 里的 gc-max-time-slice。
		// 返回 true 且 value ∈ [1,6]：找到了合法值。
		// 返回 false：没找到、值非法、或读文件出错（此时 value = -1）。
		internal static bool TryRead(out int value)
		{
			value = -1;
			try
			{
				string dataPath = Application.dataPath;
				if (string.IsNullOrEmpty(dataPath))
				{
					return false;
				}

				string path = Path.Combine(dataPath, "boot.config");
				if (!File.Exists(path))
				{
					return false;
				}

				string[] lines = File.ReadAllLines(path);
				string prefix = Key + "=";

				for (int i = 0; i < lines.Length; i++)
				{
					string trimmed = lines[i].TrimStart();
					if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
					{
						string raw = trimmed.Substring(prefix.Length).Trim();
						int parsed;
						if (int.TryParse(raw, out parsed) && parsed >= 1 && parsed <= 6)
						{
							value = parsed;
							return true;
						}
						return false;
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[更好的存档] 读取 boot.config 失败: " + ex.Message);
			}
			return false;
		}

		internal static void Apply(int value)
		{
			try
			{
				// Application.dataPath 指向 .../OxygenNotIncluded_Data
				string dataPath = Application.dataPath;
				if (string.IsNullOrEmpty(dataPath))
				{
					Debug.LogWarning("[更好的存档] 无法定位 OxygenNotIncluded_Data，boot.config 未写入");
					return;
				}

				string path = Path.Combine(dataPath, "boot.config");
				if (!File.Exists(path))
				{
					Debug.LogWarning("[更好的存档] 找不到 boot.config: " + path);
					return;
				}

				string[] lines = File.ReadAllLines(path);
				bool found = false;
				bool same = false;
				string target = Key + "=" + value.ToString();

				for (int i = 0; i < lines.Length; i++)
				{
					string trimmed = lines[i].TrimStart();
					if (trimmed.StartsWith(Key + "=", StringComparison.Ordinal))
					{
						found = true;
						if (lines[i] == target)
						{
							same = true;
						}
						else
						{
							lines[i] = target;
						}
						break;
					}
				}

				if (same)
				{
					Diag.Trace("[更好的存档] boot.config 已是 " + target + "，无需写入");
					return;
				}

				if (!found)
				{
					List<string> list = new List<string>(lines);
					list.Add(target);
					lines = list.ToArray();
				}

				File.WriteAllLines(path, lines);

				Debug.Log(string.Concat(new string[]
				{
					"[更好的存档] boot.config 已写入 ",
					target,
					"，重启游戏后生效"
				}));
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[更好的存档] 写入 boot.config 失败: " + ex.Message);
			}
		}
	}
}