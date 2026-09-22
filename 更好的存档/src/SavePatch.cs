using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SaveOpt
{
    internal static class Capture
    {
        private static MemoryStream graphStream;
        private static MemoryStream outputStream;
        private static byte[] head;
        private static byte[] source;
        private static int sourceLen;

        internal static void Reset()
        {
            graphStream = null;
            outputStream = null;
            head = null;
            source = null;
            sourceLen = 0;
        }

        internal static void NoteGraphStream(object stream)
        {
            graphStream = stream as MemoryStream;
        }

        internal static void NoteOutput(object writer)
        {
            BinaryWriter w = writer as BinaryWriter;
            if (w == null) return;
            outputStream = w.BaseStream as MemoryStream;
        }

        internal static bool Ready
        {
            get { return graphStream != null && outputStream != null; }
        }

        internal static void Snapshot()
        {
            try
            {
                if (outputStream != null) head = outputStream.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 抓取头部失败: " + e.Message);
                head = null;
            }

            try
            {
                if (graphStream == null) return;
                sourceLen = (int)graphStream.Length;
                source = graphStream.GetBuffer();
                SaveBuffer.CheckUsage(sourceLen);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 抓取源缓冲失败: " + e.Message);
                source = null;
                sourceLen = 0;
            }
        }

        internal static byte[] TakeHead()
        {
            byte[] h = head;
            head = null;
            return h;
        }

        internal static byte[] TakeSource(out int length)
        {
            length = sourceLen;
            byte[] s = source;
            source = null;
            sourceLen = 0;
            return s;
        }
    }

    internal static class SavePatch
    {
        private static string pendingPath = "";
        private static volatile bool inSave;
        private static long saved;
        private static double lastMs;

        internal static bool InSave { get { return inSave; } }

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo save = AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(string), typeof(bool), typeof(bool) });
            MethodInfo compress = AccessTools.Method(typeof(SaveLoader), "CompressContents");
            if (save == null || compress == null)
            {
                Debug.LogError("[更好的存档] 找不到 SaveLoader.Save / CompressContents，补丁未挂载");
                return;
            }

            harmony.Patch(save,
                prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SavePatch), "Save_Prefix")),
                postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SavePatch), "Save_Postfix")),
                finalizer: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SavePatch), "Save_Finalizer")),
                transpiler: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SavePatch), "Transpile")));
            Debug.Log("[更好的存档] SaveLoader.Save 已挂载");

            harmony.Patch(compress,
                prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SavePatch), "Compress_Prefix")));
            Debug.Log("[更好的存档] CompressContents 已挂载");

            MethodInfo gc = AccessTools.Method(typeof(GC), "Collect", Type.EmptyTypes);
            if (gc != null)
            {
                harmony.Patch(gc, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(SavePatch), "GC_Prefix")));
                Debug.Log("[更好的存档] GC.Collect 已挂载");
            }
        }

        public static void Save_Prefix(string filename, bool isAutoSave)
        {
            pendingPath = filename;
            Capture.Reset();
            SaveBuffer.ReleaseStale();
            inSave = true;
            GcTuner.BeginSave();
            GcModeGate.Enter();
            FrameWatch.Begin();
            SaveWatch.Start = Now();
            Debug.Log("[更好的存档] 存档开始 " + (isAutoSave ? "自动" : "手动") + " -> " + Path.GetFileName(filename));
        }

        public static Exception Save_Finalizer(Exception __exception)
        {
            GcModeGate.Exit();
            return __exception;
        }

        public static void Save_Postfix()
        {
            GcModeGate.Exit();
            inSave = false;
            byte[] head = Capture.TakeHead();
            int srcLen;
            byte[] src = Capture.TakeSource(out srcLen);
            string path = pendingPath;
            pendingPath = "";

            double total = Now() - SaveWatch.Start;
            saved++;
            lastMs = total;
            FrameWatch.Mark(total);

            if (src == null || srcLen <= 0)
            {
                Debug.LogError("[更好的存档] 未捕获到序列化缓冲。本次存档【未能落盘】——该周期进度会丢失，请用手动存档补一次。"
                    + "头部=" + (head == null ? 0 : head.Length) + " B");
                return;
            }

            if (SerializerPatch.VerifyMode) SerializerPatch.EndVerify("首次存档完成");
            if (!Sink.Enqueue(head, src, srcLen, path)) SaveBuffer.Release(src);
            Debug.Log("[更好的存档] 主线程移交后台：头部 " + ((head == null ? 0 : head.Length) / 1024) + " KB + 未压缩 "
                + (srcLen / 1048576.0).ToString("F1") + " MB；主线程存档耗时 " + total.ToString("F0") + " ms");
            Debug.Log(IsDefinedCache.Summary());
            Debug.Log(GcTuner.SaveSummary());
        }

        public static bool Compress_Prefix()
        {
            if (inSave && Capture.Ready)
            {
                Capture.Snapshot();
                return false;
            }
            Debug.LogWarning("[更好的存档] 捕获点未就绪（图流/输出流缺失），本次退回原版同步压缩");
            return true;
        }

        public static bool GC_Prefix()
        {
            return !inSave;
        }

        internal static double Now()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static string Summary()
        {
            return "[更好的存档] 存档 " + saved + " 次，主线程最近一次 " + lastMs.ToString("F0") + " ms，后台写盘 "
                + Sink.Written + " 次，未压缩 " + (Sink.RawBytes / 1048576.0).ToString("F1") + " MB -> 落盘 "
                + (Sink.OutBytes / 1048576.0).ToString("F1") + " MB，后台最近 压缩 "
                + ((long)Sink.LastCompressMs) + " ms + 写盘 " + ((long)Sink.LastWriteMs) + " ms"
                + (Sink.LastError.Length > 0 ? "，错误=" + Sink.LastError : "");
        }

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo emptyMs = AccessTools.Constructor(typeof(MemoryStream), Type.EmptyTypes);
            ConstructorInfo capMs = AccessTools.Constructor(typeof(MemoryStream), new[] { typeof(int) });
            MethodInfo rent = AccessTools.Method(typeof(SaveBuffer), "Rent");
            if (rent == null)
            {
                Debug.LogError("[更好的存档] 找不到 SaveBuffer.Rent，放弃整批补丁以保证游戏可运行");
                return instructions;
            }
            ConstructorInfo bwCtor = AccessTools.Constructor(typeof(BinaryWriter), new[] { typeof(Stream) });
            MethodInfo noteGraph = AccessTools.Method(typeof(Capture), "NoteGraphStream");
            MethodInfo noteOutput = AccessTools.Method(typeof(Capture), "NoteOutput");
            MethodInfo open = AccessTools.Method(typeof(File), "Open", new[] { typeof(string), typeof(FileMode) });

            var list = new List<CodeInstruction>(instructions);
            int graphNoted = 0, swapped = 0, outputNoted = 0;
            int swapAt = -1;

            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ins = list[i];

                if (ins.opcode == OpCodes.Newobj && ins.operand is ConstructorInfo mc && mc == capMs && graphNoted == 0)
                {
                    if (i + 1 < list.Count && list[i + 1].opcode == OpCodes.Stloc_S)
                    {
                        var rentCall = new CodeInstruction(OpCodes.Call, rent);
                        foreach (Label lb in ins.labels) rentCall.labels.Add(lb);
                        foreach (ExceptionBlock eb in ins.blocks) rentCall.blocks.Add(eb);
                        list[i] = rentCall;
                        list.Insert(i + 2, new CodeInstruction(OpCodes.Call, noteGraph));
                        var ld = new CodeInstruction(OpCodes.Ldloc_S, (LocalBuilder)null);
                        ld.operand = list[i + 1].operand;
                        list.Insert(i + 2, ld);
                        graphNoted++;
                        i += 2;
                        continue;
                    }
                    Debug.LogError("[更好的存档] 对象图 MemoryStream 之后不是 stloc，放弃");
                    break;
                }

                if (ins.opcode == OpCodes.Call && ins.operand is MethodInfo m && m == open && swapped == 0)
                {
                    if (i < 2)
                    {
                        Debug.LogError("[更好的存档] File.Open 前参数不足，放弃");
                        break;
                    }
                    CodeInstruction arg1 = list[i - 2];
                    CodeInstruction arg2 = list[i - 1];
                    if (!arg1.opcode.Name.StartsWith("ldarg") || !arg2.opcode.Name.StartsWith("ldc.i4"))
                    {
                        Debug.LogError("[更好的存档] File.Open 参数形态不符，放弃");
                        break;
                    }
                    var rep = new CodeInstruction(OpCodes.Newobj, emptyMs);
                    foreach (Label lb in arg1.labels) rep.labels.Add(lb);
                    foreach (ExceptionBlock eb in arg1.blocks) rep.blocks.Add(eb);
                    list.RemoveRange(i - 2, 3);
                    list.Insert(i - 2, rep);
                    swapAt = i - 2;
                    swapped++;
                    i = i - 2;
                    continue;
                }

                if (ins.opcode == OpCodes.Newobj && ins.operand is ConstructorInfo bw && bw == bwCtor
                    && outputNoted == 0 && swapAt >= 0 && i > swapAt)
                {
                    if (i + 1 < list.Count && list[i + 1].opcode == OpCodes.Stloc_S)
                    {
                        list.Insert(i + 2, new CodeInstruction(OpCodes.Call, noteOutput));
                        var ld2 = new CodeInstruction(OpCodes.Ldloc_S, (LocalBuilder)null);
                        ld2.operand = list[i + 1].operand;
                        list.Insert(i + 2, ld2);
                        outputNoted++;
                        i += 2;
                        continue;
                    }
                    Debug.LogError("[更好的存档] 输出 BinaryWriter 之后不是 stloc，放弃");
                    break;
                }
            }

            if (graphNoted != 1 || swapped != 1 || outputNoted != 1)
            {
                Debug.LogError("[更好的存档] 注入不完整（图流=" + graphNoted + " 换流=" + swapped + " 输出=" + outputNoted
                    + "），放弃整批补丁以保证游戏可运行");
                return instructions;
            }

            if (!ShapeOk(list, rent, bwCtor, noteGraph, noteOutput))
            {
                Debug.LogError("[更好的存档] 结构性校验失败，放弃整批补丁");
                return instructions;
            }

            Debug.Log("[更好的存档] transpiler: 图流记录=1 换流=1 输出记录=1，结构性校验通过");
            return list;
        }

        private static bool StructOk(List<CodeInstruction> list, int callIdx)
        {
            if (callIdx < 2 || callIdx >= list.Count) return false;
            bool call = list[callIdx].opcode == OpCodes.Call;
            bool ld = list[callIdx - 1].opcode == OpCodes.Ldloc_S;
            bool st = list[callIdx - 2].opcode.Name.StartsWith("stloc");
            bool sameVar = ld && st && Equals(list[callIdx - 1].operand, list[callIdx - 2].operand);
            return call && ld && st && sameVar;
        }

        private static bool GraphAfterCap(List<CodeInstruction> list, int capIdx, int graphCall)
        {
            return capIdx >= 0 && graphCall == capIdx + 3;
        }

        private static bool ShapeOk(List<CodeInstruction> list, MethodInfo rent, ConstructorInfo bwCtor,
            MethodInfo noteGraph, MethodInfo noteOutput)
        {
            int graphCall = -1, outputCall = -1, capIdx = -1, bwAfterSwap = -1, swapIdx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction c = list[i];
                if (c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ci)
                {
                    if (ci.DeclaringType == typeof(MemoryStream) && ci.GetParameters().Length == 0 && swapIdx < 0) swapIdx = i;
                    if (ci == bwCtor && swapIdx >= 0 && i > swapIdx && bwAfterSwap < 0) bwAfterSwap = i;
                }
                if (c.opcode == OpCodes.Call && c.operand is MethodInfo m)
                {
                    if (m == rent && capIdx < 0) capIdx = i;
                    if (m == noteGraph && graphCall < 0) graphCall = i;
                    if (m == noteOutput && outputCall < 0) outputCall = i;
                }
            }

            bool a = StructOk(list, graphCall) && GraphAfterCap(list, capIdx, graphCall);
            bool b = swapIdx >= 0 && bwAfterSwap == swapIdx + 1;
            bool c2 = StructOk(list, outputCall) && outputCall > bwAfterSwap;
            bool d = capIdx >= 0 && graphCall > capIdx && swapIdx > graphCall && bwAfterSwap > swapIdx && outputCall > bwAfterSwap;
            if (!(a && b && c2 && d))
            {
                Debug.LogError("[更好的存档] ShapeOk 逐条: graphCall=" + graphCall + " capIdx=" + capIdx + " a=" + a
                    + " | swapIdx=" + swapIdx + " bwAfterSwap=" + bwAfterSwap + " b=" + b
                    + " | outputCall=" + outputCall + " c=" + c2 + " | 顺序 d=" + d);
            }
            return a && b && c2 && d;
        }
    }

    internal static class SaveWatch
    {
        internal static double Start;
    }
}