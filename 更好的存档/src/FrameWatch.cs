using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace SaveOpt
{
    internal static class FrameWatch
    {
        private const double Window = 10.0;
        private const double ThresholdMs = 100.0;
        private const double HitchMs = 250.0;
        private const long HitchLogCap = 200;
        private const int MaxRecords = 24;
        private const double SaveWindowSec = 3.0;

        private static double lastFrame;
        private static bool tracking;
        private static double startAt;
        private static long windowFrames;
        private static long totalFrames;
        private static double saveMs;
        private static int count;
        private static double gapTotal;
        private static double gapMax;
        private static long hitchCount;
        private static double hitchTotal;
        private static double hitchMax;
        private static long hitchSaveCount;
        private static double hitchSaveTotal;
        private static long hitchFreeCount;
        private static double hitchFreeTotal;
        private static long hitchStartCount;
        private static double hitchStartTotal;
        private static readonly double[] topMs = new double[3];
        private static readonly double[] topSince = new double[3];
        private static readonly long[] topHeap = new long[3];
        private static readonly bool[] topGc = new bool[3];
        private static int lastC0;
        private static double lastSaveAt;
        private static readonly double[] gapMs = new double[MaxRecords];
        private static readonly double[] gapAt = new double[MaxRecords];
        private static double previewStart;
        private static int lastC2;
        private static int gcInWindow;
        private static readonly double[] gcOffsets = new double[8];
        private static bool previewCopy;
        private static double markAt;
        private static bool markPending;
        private static double postMs;
        private static double allowStart;
        private static double allowMs;
        private static long allowCalls;
        private static double allowTotal;
        private static double deactStart;
        private static double deactMs;
        private static long deactCalls;
        private static double deactTotal;
        private static double lastHeapLog;
        private static long heapSamples;
        private static long heapFirst;
        private static long heapMax;
        private static bool captureWatch;
        private static bool capturing;
        private static double captureAt;
        private static long captureCount;
        private static double captureTotal;
        private static double captureMax;

        internal static bool Apply(HarmonyLib.Harmony harmony)
        {
            bool frameHook = false;
            try
            {
                MethodInfo late = AccessTools.Method(typeof(App), "LateUpdate");
                if (late == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 App.LateUpdate，体感窗口无法测量");
                }
                else
                {
                    harmony.Patch(late, postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(FrameWatch), "Tick")));
                    lastFrame = Time.realtimeSinceStartup;
                    frameHook = true;
                    Diag.Trace("[更好的存档] 体感窗口测量已挂载（逐帧记录存档开始后 " + (int)Window + " s 内 ≥"
                        + (int)ThresholdMs + " ms 的卡顿）");
                }

                MethodInfo rap = AccessTools.Method(typeof(Timelapser), "RenderAndPrint");
                if (rap != null)
                {
                    harmony.Patch(rap,
                        prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(FrameWatch), "Preview_Prefix")),
                        postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(FrameWatch), "Preview_Postfix")));
                    Diag.Trace("[更好的存档] 预览图生成计时已挂载（Timelapser.RenderAndPrint）");
                }
                else
                {
                    Debug.LogWarning("[更好的存档] 找不到 Timelapser.RenderAndPrint，预览图计时段缺失");
                }

                PatchTimer(harmony, typeof(PlayerController), "AllowDragging",
                    "AllowDragging_Prefix", "AllowDragging_Postfix", "PlayerController.AllowDragging");
                PatchTimer(harmony, typeof(SaveActive), "DeactivateSaveIndicator",
                    "Deactivate_Prefix", "Deactivate_Postfix", "SaveActive.DeactivateSaveIndicator");

                captureWatch = AccessTools.Method(typeof(GameUtil), "IsCapturingTimeLapse") != null;
                if (captureWatch)
                {
                    Diag.Trace("[更好的存档] 预览捕获状态监测已挂载（经 GameUtil.IsCapturingTimeLapse，该状态为真时 CameraController 不处理输入）");
                }
                else
                {
                    Debug.LogWarning("[更好的存档] 找不到 GameUtil.IsCapturingTimeLapse，预览捕获窗口无法计时");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 体感窗口测量挂载失败: " + e.Message);
            }
            return frameHook;
        }

        private static void PatchTimer(HarmonyLib.Harmony harmony, Type owner, string name,
            string prefix, string postfix, string label)
        {
            if (owner == null) return;
            try
            {
                MethodInfo m = AccessTools.Method(owner, name);
                if (m == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 " + label + "，该分项计时缺失");
                    return;
                }
                harmony.Patch(m,
                    prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(FrameWatch), prefix)),
                    postfix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(FrameWatch), postfix)));
                Diag.Trace("[更好的存档] 存档帧分项计时已挂载：" + label);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] " + label + " 计时挂载失败: " + e.Message);
            }
        }

        public static void Tick()
        {
            double now = Time.realtimeSinceStartup;
            double gap = now - lastFrame;
            lastFrame = now;
            totalFrames++;
            int c0 = GC.CollectionCount(0);
            bool collected = c0 != lastC0;
            lastC0 = c0;

            if (lastHeapLog <= 0) lastHeapLog = now;
            if (now - lastHeapLog >= 30.0)
            {
                lastHeapLog = now;
                heapSamples++;
                long mb = GC.GetTotalMemory(false) / 1048576;
                if (heapFirst == 0) heapFirst = mb;
                if (mb > heapMax) heapMax = mb;
                Diag.Trace("[更好的存档] 堆采样 #" + heapSamples + "：" + mb + " MB ｜ 回收计数 0/1/2 = "
                    + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2)
                    + " ｜ GCMode=" + GarbageCollector.GCMode);
            }

            if (markPending)
            {
                postMs = (now - markAt) * 1000.0;
                markPending = false;
            }

            if (gap * 1000.0 >= HitchMs)
            {
                double ms = gap * 1000.0;
                double since = lastSaveAt > 0 ? now - lastSaveAt : -1;
                long heapMb = GC.GetTotalMemory(false) / 1048576;
                hitchCount++;
                hitchTotal += ms;
                if (ms > hitchMax) hitchMax = ms;
                if (since < 0)
                {
                    hitchStartCount++;
                    hitchStartTotal += ms;
                }
                else if (since <= SaveWindowSec)
                {
                    hitchSaveCount++;
                    hitchSaveTotal += ms;
                }
                else
                {
                    hitchFreeCount++;
                    hitchFreeTotal += ms;
                }
                if (ms > topMs[0])
                {
                    topMs[2] = topMs[1]; topSince[2] = topSince[1]; topHeap[2] = topHeap[1]; topGc[2] = topGc[1];
                    topMs[1] = topMs[0]; topSince[1] = topSince[0]; topHeap[1] = topHeap[0]; topGc[1] = topGc[0];
                    topMs[0] = ms; topSince[0] = since; topHeap[0] = heapMb; topGc[0] = collected;
                }
                else if (ms > topMs[1])
                {
                    topMs[2] = topMs[1]; topSince[2] = topSince[1]; topHeap[2] = topHeap[1]; topGc[2] = topGc[1];
                    topMs[1] = ms; topSince[1] = since; topHeap[1] = heapMb; topGc[1] = collected;
                }
                else if (ms > topMs[2])
                {
                    topMs[2] = ms; topSince[2] = since; topHeap[2] = heapMb; topGc[2] = collected;
                }
                if (hitchCount <= HitchLogCap)
                {
                    string line = "[更好的存档] 全程卡顿 " + ms.ToString("F0") + " ms ｜ 距上次存档结束 "
                        + (since < 0 ? "无" : since.ToString("F0") + " s") + " ｜ 堆 " + heapMb
                        + " MB ｜ " + (collected ? "有回收" : "无回收") + " ｜ 累计 " + hitchCount + " 次";
                    if (ms >= 400.0) Debug.Log(line);
                    else Diag.Trace(line);
                }
                else if (hitchCount == HitchLogCap + 1)
                {
                    Debug.LogWarning("[更好的存档] 卡顿日志已达上限 " + HitchLogCap + " 条，后续只计数不再逐条打印");
                }
            }

            int c2 = GC.CollectionCount(2);
            if (c2 != lastC2)
            {
                if (tracking)
                {
                    if (gcInWindow < gcOffsets.Length) gcOffsets[gcInWindow] = now - startAt;
                    gcInWindow += c2 - lastC2;
                }
                lastC2 = c2;
            }

            if (captureWatch)
            {
                bool nowCapturing;
                try { nowCapturing = GameUtil.IsCapturingTimeLapse(); }
                catch (Exception) { captureWatch = false; nowCapturing = false; }

                if (nowCapturing && !capturing)
                {
                    capturing = true;
                    captureAt = now;
                }
                else if (!nowCapturing && capturing)
                {
                    capturing = false;
                    double d = (now - captureAt) * 1000.0;
                    captureCount++;
                    captureTotal += d;
                    if (d > captureMax) captureMax = d;
                    Diag.Trace("[更好的存档] 预览捕获窗口结束：持续 " + d.ToString("F0")
                        + " ms（该期间 CameraController 不处理输入）");
                }
            }

            if (!tracking) return;

            windowFrames++;
            if (gap * 1000.0 >= ThresholdMs)
            {
                if (count < MaxRecords)
                {
                    gapMs[count] = gap * 1000.0;
                    gapAt[count] = now - startAt;
                    count++;
                }
                gapTotal += gap * 1000.0;
                if (gap * 1000.0 > gapMax) gapMax = gap * 1000.0;
            }

            if (now - startAt >= Window)
            {
                tracking = false;
                LogSummary();
            }
        }

        internal static void Begin()
        {
            tracking = true;
            startAt = Time.realtimeSinceStartup;
            windowFrames = 0;
            count = 0;
            gapTotal = 0;
            gapMax = 0;
            saveMs = 0;
            lastC2 = GC.CollectionCount(2);
            gcInWindow = 0;
            previewCopy = false;
            markPending = false;
            postMs = 0;
            allowMs = 0;
            deactMs = 0;
            allowStart = 0;
            deactStart = 0;
        }

        internal static void NotePreviewCopy()
        {
            previewCopy = true;
        }

        internal static void Mark(double mainMs)
        {
            saveMs = mainMs;
            markAt = Time.realtimeSinceStartup;
            lastSaveAt = markAt;
            markPending = true;
        }

        public static void AllowDragging_Prefix()
        {
            if (!tracking) return;
            allowStart = Time.realtimeSinceStartup;
        }

        public static void AllowDragging_Postfix()
        {
            if (!tracking || allowStart <= 0) return;
            allowCalls++;
            allowMs = (Time.realtimeSinceStartup - allowStart) * 1000.0;
            allowTotal += allowMs;
            allowStart = 0;
        }

        public static void Deactivate_Prefix()
        {
            if (!tracking) return;
            deactStart = Time.realtimeSinceStartup;
        }

        public static void Deactivate_Postfix()
        {
            if (!tracking || deactStart <= 0) return;
            deactCalls++;
            deactMs = (Time.realtimeSinceStartup - deactStart) * 1000.0;
            deactTotal += deactMs;
            deactStart = 0;
        }

        public static void Preview_Prefix()
        {
            previewStart = Time.realtimeSinceStartup;
        }

        public static void Preview_Postfix()
        {
            Diag.Trace("[更好的存档] 预览图生成（主线程，落在存档计时窗口之外）："
                + ((Time.realtimeSinceStartup - previewStart) * 1000.0).ToString("F0") + " ms");
        }

        private static void LogSummary()
        {
            string detail = "";
            for (int i = 0; i < count; i++)
            {
                detail += (i > 0 ? "、" : "") + gapMs[i].ToString("F0") + " ms@" + gapAt[i].ToString("F1") + " s";
            }
            if (detail.Length == 0) detail = "无";

            string gcs = gcInWindow == 0 ? "无" : (gcInWindow + " 次");
            for (int i = 0; i < gcInWindow && i < gcOffsets.Length; i++)
            {
                gcs += (i > 0 ? "、" : " ") + gcOffsets[i].ToString("F1") + " s";
            }

            Diag.Trace("[更好的存档] 体感窗口：自存档开始 " + (int)Window + " s 内逐帧 " + windowFrames
                + " 帧（累计 " + totalFrames + "）｜ 主线程存档窗口 " + saveMs.ToString("F0")
                + " ms ｜ ≥" + (int)ThresholdMs + " ms 的卡顿 " + count + " 段，合计 "
                + gapTotal.ToString("F0") + " ms，最长 " + gapMax.ToString("F0") + " ms ｜ " + detail
                + " ｜ 窗口内 Gen2 " + gcs + " ｜ 预览图" + (previewCopy ? "复制复用" : "真实捕获")
                + " ｜ 窗口结束到下一帧 " + postMs.ToString("F0") + " ms（AllowDragging "
                + allowCalls + " 次/" + allowTotal.ToString("F0") + " ms，停用提示 "
                + deactCalls + " 次/" + deactTotal.ToString("F0") + " ms）");
        }

        private static string TopText()
        {
            string s = "";
            for (int i = 0; i < topMs.Length; i++)
            {
                if (topMs[i] <= 0) break;
                s += (i > 0 ? "、" : "") + topMs[i].ToString("F0") + " ms(距存档 "
                    + (topSince[i] < 0 ? "无" : topSince[i].ToString("F0") + " s")
                    + "，堆 " + topHeap[i] + " MB，" + (topGc[i] ? "有回收" : "无回收") + ")";
            }
            return s.Length == 0 ? "无" : s;
        }

        internal static string Summary()
        {
            return "[更好的存档] 体感窗口测量：全程逐帧 " + totalFrames + " 帧 ｜ 预览捕获 "
                + captureCount + " 次，合计 " + captureTotal.ToString("F0") + " ms，最长 "
                + captureMax.ToString("F0") + " ms ｜ 堆采样 " + heapSamples + " 次，首 "
                + heapFirst + " MB，峰值 " + heapMax + " MB ｜ 全程卡顿 " + hitchCount
                + " 次，合计 " + hitchTotal.ToString("F0") + " ms，最长 " + hitchMax.ToString("F0") + " ms"
                + "（启动期 " + hitchStartCount + " 次/合计 "
                + hitchStartTotal.ToString("F0") + " ms，存档后 " + (int)SaveWindowSec + " s 内 "
                + hitchSaveCount + " 次/合计 " + hitchSaveTotal.ToString("F0")
                + " ms，游玩中 " + hitchFreeCount + " 次/合计 "
                + hitchFreeTotal.ToString("F0") + " ms）｜ 最长三次 " + TopText();
        }
    }
}
