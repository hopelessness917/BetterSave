using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using KSerialization;

namespace SaveOpt
{
    internal static class TypeNameCache
    {
        private const int MaxCached = 4096;
        private const int LongName = 1024;

        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>(1024);
        private static readonly MemoryStream ScratchStream = new MemoryStream(8192);

        private static BinaryWriter scratchWriter;
        private static bool failed;
        private static bool bypass;
        private static string failReason = "";
        private static long hits;
        private static long misses;
        private static long verified;
        private static long tooLong;
        private static long notCached;
        private static int swapped;

        internal static bool Bypass
        {
            get { return bypass; }
        }

        internal static void BeginSave()
        {
            bypass = !bypass;
        }

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(SaveLoadRoot), "SaveWithoutTransform");
            if (target == null)
            {
                Debug.LogError("[更好的存档] 找不到 SaveLoadRoot.SaveWithoutTransform，类型名缓存未挂载");
                return false;
            }
            harmony.Patch(target, transpiler: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(TypeNameCache), "Transpile")));
            if (swapped != 1)
            {
                Debug.LogError("[更好的存档] 类型名缓存替换点数量异常（" + swapped + "），已退回原路径");
                return false;
            }
            Debug.Log("[更好的存档] 类型名缓存已挂载：按 string 记忆化 UTF-8 编码，"
                + "每个新名字首次使用时与游戏原实现逐字节比对，不一致则永久回退。"
                + "本次为 A/B 诊断版：每隔一次存档走原路径，便于同局对照");
            return true;
        }

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo klei = AccessTools.Method(typeof(IOHelper), "WriteKleiString",
                new[] { typeof(BinaryWriter), typeof(string) });
            MethodInfo mine = AccessTools.Method(typeof(TypeNameCache), "Write");
            swapped = 0;
            if (klei == null || mine == null)
            {
                Debug.LogError("[更好的存档] 类型名缓存解析失败，放弃（游戏可运行）");
                return instructions;
            }

            var list = new List<CodeInstruction>(instructions);
            for (int i = 2; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Call) continue;
                MethodInfo m = list[i].operand as MethodInfo;
                if (m == null || m != klei) continue;

                MethodInfo ts = list[i - 1].operand as MethodInfo;
                if (list[i - 1].opcode != OpCodes.Callvirt || ts == null || ts.Name != "ToString") continue;

                MethodInfo gt = list[i - 2].operand as MethodInfo;
                if (list[i - 2].opcode != OpCodes.Callvirt || gt == null || gt.Name != "GetType") continue;

                var rep = new CodeInstruction(OpCodes.Call, mine);
                foreach (Label lb in list[i].labels) rep.labels.Add(lb);
                foreach (ExceptionBlock eb in list[i].blocks) rep.blocks.Add(eb);
                list[i] = rep;
                swapped++;
                break;
            }

            if (swapped != 1)
            {
                Debug.LogError("[更好的存档] 未找到类型名写入点（GetType->ToString->WriteKleiString），"
                    + "放弃类型名缓存（游戏可运行）");
                return instructions;
            }
            return list;
        }

        public static void Write(BinaryWriter w, string s)
        {
            if (s == null || failed || bypass)
            {
                IOHelper.WriteKleiString(w, s);
                return;
            }

            byte[] bytes;
            if (Cache.TryGetValue(s, out bytes))
            {
                hits++;
                w.Write(bytes.Length);
                w.Write(bytes, 0, bytes.Length);
                return;
            }

            misses++;
            bytes = Build(s);
            if (bytes == null)
            {
                IOHelper.WriteKleiString(w, s);
                return;
            }

            if (Cache.Count < MaxCached) Cache[s] = bytes;
            else notCached++;

            w.Write(bytes.Length);
            w.Write(bytes, 0, bytes.Length);
        }

        private static byte[] Build(string s)
        {
            byte[] mine;
            try
            {
                mine = Encoding.UTF8.GetBytes(s);
            }
            catch (Exception e)
            {
                Fail("UTF-8 编码异常: " + e.Message);
                return null;
            }

            if (mine.Length >= LongName)
            {
                tooLong++;
                return null;
            }

            try
            {
                if (scratchWriter == null) scratchWriter = new BinaryWriter(ScratchStream);
                ScratchStream.SetLength(0);
                ScratchStream.Position = 0;
                IOHelper.WriteKleiString(scratchWriter, s);

                int n = (int)ScratchStream.Length;
                byte[] buf = ScratchStream.GetBuffer();
                bool ok = n == mine.Length + 4;
                if (ok)
                {
                    int len = buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24);
                    ok = len == mine.Length;
                    for (int i = 0; ok && i < mine.Length; i++)
                    {
                        if (buf[4 + i] != mine[i]) ok = false;
                    }
                }
                if (!ok)
                {
                    Fail("首个新名字的字节级比对与原实现不一致（" + s + "，原实现 " + n + " 字节 / 本实现 "
                        + (mine.Length + 4) + " 字节）");
                    return null;
                }
                verified++;
            }
            catch (Exception e)
            {
                Fail("与原实现比对时异常: " + e.Message);
                return null;
            }

            return mine;
        }

        private static void Fail(string reason)
        {
            failed = true;
            failReason = reason;
            Debug.LogError("[更好的存档] 类型名缓存永久停用，全部退回原实现：" + reason);
        }

        internal static string Summary()
        {
            return "[更好的存档] 类型名缓存" + (failed ? "（已停用：" + failReason + "）" : "") + "：命中 " + hits
                + " 次，新名字 " + misses + " 次（其中校验通过 " + verified + " 次），"
                + "过长未缓存 " + tooLong + " 次，超出容量未缓存 " + notCached + " 次 ｜ 缓存 " + Cache.Count + " 个";
        }
    }
}
