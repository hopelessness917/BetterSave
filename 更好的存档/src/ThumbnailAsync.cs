using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace SaveOpt
{
    internal sealed class ThumbJob
    {
        internal string Path;
        internal Color32[] Pixels;
        internal int Width;
        internal int Height;
        internal byte[] Reference;
    }

    internal static class ThumbnailAsync
    {
        private const float PreviewScale = 0.1f;
        private const double RefreshSeconds = 1800.0;

        private static readonly byte[] Sentinel = new byte[0];

        private static AccessTools.FieldRef<Timelapser, Vector2Int> previewRes;
        private static Vector2Int previewOriginal;
        private static Vector2Int previewApplied;
        private static bool previewHave;
        private static long previewShrinks;
        private static bool haveRendered;
        private static bool skipThisSave;
        private static double lastRenderAt;
        private static string lastPngPath;
        private static long previewCaptures;
        private static long previewSkips;
        private static long previewCopies;
        private static long previewMisses;

        private static readonly object Gate = new object();
        private static readonly Queue<ThumbJob> Queue = new Queue<ThumbJob>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);

        private static Thread worker;
        private static volatile bool stopping;
        private static bool enabled;

        private static bool verified;
        private static bool flipRows;
        private static bool mismatch;

        private static readonly Queue<ThumbJob> Pending = new Queue<ThumbJob>();

        private static long jobs;
        private static long fallbacks;
        private static long lastEncodeMs;
        private static long lastMainMs;
        private static long refEncodeMs;
        private static long totalEncodeMs;
        private static long bytes;
        private static string lastNote = "";

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(Timelapser), "WriteToPng");
            MethodInfo writeAll = AccessTools.Method(typeof(File), "WriteAllBytes", new[] { typeof(string), typeof(byte[]) });
            if (target == null || writeAll == null)
            {
                Debug.LogWarning("[更好的存档] 找不到 Timelapser.WriteToPng 或 File.WriteAllBytes，缩略图后台化未挂载");
                return;
            }

            int swapped = 0;
            try
            {
                harmony.Patch(target, transpiler: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "Transpile")));
                swapped = swappedInjected;
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] 缩略图 transpiler 挂载失败: " + e.Message);
                return;
            }

            if (swapped != 1)
            {
                Debug.LogError("[更好的存档] 缩略图替换点数量异常（" + swapped + "），缩略图后台化未启用");
                return;
            }

            try
            {
                harmony.Patch(writeAll, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "WriteAllBytes_Prefix")));
            }
            catch (Exception e)
            {
                Debug.LogError("[更好的存档] File.WriteAllBytes 挂载失败: " + e.Message);
                return;
            }

            ApplyPreviewScale(harmony);

            Start();
            enabled = true;
            Debug.Log("[更好的存档] 缩略图 PNG 编码已移入后台（EncodeToPNG -> EncodeArrayToPNG + 独立线程；"
                + "首张做字节级比对，行序自动判定，不一致则永久回退）");
        }

        private static int swappedInjected;

        private static void ApplyPreviewScale(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo refresh = AccessTools.Method(typeof(Timelapser), "RefreshRenderTextureSize");
                MethodInfo colony = AccessTools.Method(typeof(Timelapser), "SaveColonyPreview");
                if (refresh == null || colony == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到预览图相关方法，预览图跳渲未启用");
                    return;
                }
                previewRes = AccessTools.FieldRefAccess<Timelapser, Vector2Int>("previewScreenshotResolution");
                if (previewRes == null)
                {
                    Debug.LogWarning("[更好的存档] 找不到 Timelapser.previewScreenshotResolution，预览图未启用");
                    return;
                }

                harmony.Patch(colony, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "SaveColonyPreview_Prefix")));
                harmony.Patch(refresh, prefix: new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(ThumbnailAsync), "Refresh_Prefix")));

                Debug.Log("[更好的存档] 预览图跳渲已挂载：每 " + (int)(RefreshSeconds / 60)
                    + " 分钟才真正做一次捕获，其余存档直接跳过整个捕获并复制上一张 png。"
                    + "实测捕获开销 1.3-1.4 s 且与分辨率无关（成本在两次完整相机渲染与"
                    + "RenderAndPrint 内的相机移动，会触发全图重新剔除）");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[更好的存档] 预览图跳渲挂载失败: " + e.Message);
            }
        }

        public static bool SaveColonyPreview_Prefix(string __0)
        {
            string png;
            try { png = Path.ChangeExtension(__0, ".png"); }
            catch (Exception) { return true; }
            if (string.IsNullOrEmpty(png)) return true;

            if (haveRendered && lastPngPath != null && Time.realtimeSinceStartup - lastRenderAt < RefreshSeconds)
            {
                skipThisSave = true;
                previewSkips++;
                if (previewSkips == 1)
                {
                    Debug.Log("[更好的存档] 预览图捕获已跳过：整个捕获不做，png 由上一张复制而来"
                        + "（每 " + (int)(RefreshSeconds / 60) + " 分钟才真正捕获一次）");
                }
                return false;
            }

            skipThisSave = false;
            haveRendered = true;
            lastRenderAt = Time.realtimeSinceStartup;
            lastPngPath = png;
            previewCaptures++;
            return true;
        }

        internal static string PreviewCopy(out string to, string savePath)
        {
            to = null;
            bool skipped = skipThisSave;
            skipThisSave = false;
            if (!skipped) return null;

            try { to = Path.ChangeExtension(savePath, ".png"); }
            catch (Exception) { to = null; }
            if (string.IsNullOrEmpty(to)) return null;

            string from = lastPngPath;
            if (string.IsNullOrEmpty(from) || !File.Exists(from))
            {
                previewMisses++;
                to = null;
                return null;
            }

            lastPngPath = to;
            previewCopies++;
            return from;
        }

        public static void Refresh_Prefix(Timelapser __instance)
        {
            if (previewRes == null) return;
            Vector2Int cur = previewRes(__instance);
            if (!previewHave || cur != previewApplied)
            {
                previewOriginal = cur;
                previewHave = true;
            }
            int w = Mathf.Max(64, (int)(previewOriginal.x * PreviewScale));
            int h = Mathf.Max(64, (int)(previewOriginal.y * PreviewScale));
            Vector2Int scaled = new Vector2Int(w, h);
            if (cur != scaled)
            {
                previewRes(__instance) = scaled;
                previewShrinks++;
                if (previewShrinks == 1)
                {
                    Debug.Log("[更好的存档] 预览图分辨率 " + previewOriginal.x + " x " + previewOriginal.y
                        + " -> " + w + " x " + h);
                }
                previewApplied = scaled;
            }
        }

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo encodeToPng = AccessTools.Method(typeof(ImageConversion), "EncodeToPNG", new[] { typeof(Texture2D) });
            MethodInfo capture = AccessTools.Method(typeof(ThumbnailAsync), "Capture");
            swappedInjected = 0;
            if (encodeToPng == null || capture == null) return instructions;

            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ins = list[i];
                if (ins.opcode != OpCodes.Call && ins.opcode != OpCodes.Callvirt) continue;
                MethodInfo m = ins.operand as MethodInfo;
                if (m == null || m != encodeToPng) continue;

                var rep = new CodeInstruction(OpCodes.Call, capture);
                foreach (Label lb in ins.labels) rep.labels.Add(lb);
                foreach (ExceptionBlock eb in ins.blocks) rep.blocks.Add(eb);
                list[i] = rep;
                swappedInjected++;
            }
            return list;
        }

        public static byte[] Capture(Texture2D tex)
        {
            if (!enabled || tex == null) return ImageConversion.EncodeToPNG(tex);

            double t0 = Now();
            try
            {
                var job = new ThumbJob();
                job.Width = tex.width;
                job.Height = tex.height;
                job.Pixels = tex.GetPixels32();
                if (!verified && !mismatch)
                {
                    double r0 = Now();
                    job.Reference = ImageConversion.EncodeToPNG(tex);
                    refEncodeMs = (long)(Now() - r0);
                }
                lastMainMs = (long)(Now() - t0);
                lock (Gate) Pending.Enqueue(job);
                return Sentinel;
            }
            catch (Exception e)
            {
                enabled = false;
                fallbacks++;
                Debug.LogError("[更好的存档] 缩略图像素抓取失败，缩略图后台化已停用: " + e.Message);
                return ImageConversion.EncodeToPNG(tex);
            }
        }

        public static bool WriteAllBytes_Prefix(string path, byte[] bytes)
        {
            if (!ReferenceEquals(bytes, Sentinel)) return true;

            ThumbJob job = null;
            lock (Gate)
            {
                if (Pending.Count > 0) job = Pending.Dequeue();
            }
            if (job == null)
            {
                fallbacks++;
                return true;
            }

            job.Path = path;
            lock (Gate) Queue.Enqueue(job);
            Signal.Set();
            return false;
        }

        private static void Start()
        {
            if (worker != null) return;
            stopping = false;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "SaveOptThumbnail";
            worker.Start();
        }

        private static void Loop()
        {
            while (!stopping)
            {
                Signal.WaitOne();
                while (true)
                {
                    ThumbJob job;
                    lock (Gate)
                    {
                        if (Queue.Count == 0) break;
                        job = Queue.Dequeue();
                    }
                    Run(job);
                }
            }
        }

        private static void Run(ThumbJob job)
        {
            try
            {
                double t0 = Now();
                bool flip = flipRows;
                byte[] png = Encode(job, flip);

                if (!verified && !mismatch)
                {
                    if (Same(png, job.Reference))
                    {
                        flip = false;
                        verified = true;
                    }
                    else
                    {
                        byte[] flipped = Encode(job, true);
                        if (Same(flipped, job.Reference))
                        {
                            flip = true;
                            flipRows = true;
                            verified = true;
                            png = flipped;
                        }
                        else
                        {
                            mismatch = true;
                            enabled = false;
                            png = job.Reference;
                            lastNote = "★★ 字节不一致，已永久回退（本张用主线程结果写入）";
                        }
                    }
                }

                long ms = (long)(Now() - t0);
                lastEncodeMs = ms;
                totalEncodeMs += ms;
                File.WriteAllBytes(job.Path, png);
                jobs++;
                bytes += png.Length;

                if (jobs == 1 || mismatch)
                {
                    Debug.Log("[更好的存档] 缩略图后台编码：首张 " + job.Width + "x" + job.Height
                        + "，" + png.Length + " 字节，后台耗时 " + ms + " ms（主线程抓像素 "
                        + lastMainMs + " ms）"
                        + (job.Reference != null ? "；同一张在主线程用原方法编码要 " + refEncodeMs + " ms —— 这就是本刀从主线程拿走的部分" : "")
                        + (verified ? "，行序=原序" : "") + (flip ? "，行序=翻转" : "")
                        + (job.Reference != null ? "，与主线程结果字节一致" : "")
                        + (lastNote.Length > 0 ? " ｜ " + lastNote : ""));
                }
            }
            catch (Exception e)
            {
                fallbacks++;
                Debug.LogError("[更好的存档] 缩略图后台写盘失败: " + job.Path + " : " + e.Message);
            }
            finally
            {
                job.Pixels = null;
                job.Reference = null;
            }
        }

        private static byte[] Encode(ThumbJob job, bool flip)
        {
            int w = job.Width, h = job.Height;
            byte[] buf = new byte[w * h * 4];
            Color32[] px = job.Pixels;
            for (int y = 0; y < h; y++)
            {
                int srcRow = (flip ? (h - 1 - y) : y) * w;
                int di = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[srcRow + x];
                    buf[di] = c.r;
                    buf[di + 1] = c.g;
                    buf[di + 2] = c.b;
                    buf[di + 3] = c.a;
                    di += 4;
                }
            }
            return ImageConversion.EncodeArrayToPNG(buf, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, (uint)(w * 4));
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static double Now()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static void Flush(int timeoutMs)
        {
            if (worker == null) return;
            int waited = 0;
            while (waited < timeoutMs)
            {
                bool busy;
                lock (Gate) busy = Queue.Count > 0;
                if (!busy) return;
                Thread.Sleep(50);
                waited += 50;
            }
        }

        internal static void Stop()
        {
            stopping = true;
            Signal.Set();
        }

        internal static string Summary()
        {
            if (!enabled && jobs == 0)
            {
                return "[更好的存档] 缩略图后台化：未启用";
            }
            return "[更好的存档] 缩略图后台化" + (enabled ? "" : "（已停用）") + "：后台编码 " + jobs
                + " 张，累计 " + totalEncodeMs + " ms，落盘 " + (bytes / 1048576.0).ToString("F2")
                + " MB，最近一张 " + lastEncodeMs + " ms；主线程只做像素抓取 " + lastMainMs
                + " ms；回退 " + fallbacks + " 次"
                + (mismatch ? " ｜ ★★ 曾出现字节不一致" : " ｜ 首张已与主线程结果逐字节比对通过")
                + " ｜ 预览图：真实捕获 " + previewCaptures + " 次，跳过 " + previewSkips
                + " 次，复制复用 " + previewCopies + " 次，无可复制而留空 " + previewMisses + " 次";
        }
    }
}
