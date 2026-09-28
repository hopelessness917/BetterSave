using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ionic.Zlib;

namespace SaveOpt
{
    internal sealed class WriteJob
    {
        internal byte[] Head;
        internal byte[] Source;
        internal int SourceLength;
        internal string Path;

        // 【已停用】PngFrom / PngTo 用于"自动存档复制上一张 png"，
        // 该功能已停用，两个字段不再被使用，保留仅作历史记录。
        // internal string PngFrom;
        // internal string PngTo;
    }

    internal static class Sink
    {
        private static readonly object Gate = new object();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        private static readonly ManualResetEvent Idle = new ManualResetEvent(true);
        private static readonly Queue<WriteJob> Queue = new Queue<WriteJob>();

        private static Thread worker;
        private static volatile bool stopping;
        private static bool working;
        private static long written;
        private static long rawBytes;
        private static long outBytes;
        private static double lastCompressMs;
        private static double lastWriteMs;
        private static string lastError = "";

        internal static long Written { get { return written; } }
        internal static long RawBytes { get { return rawBytes; } }
        internal static long OutBytes { get { return outBytes; } }
        internal static double LastCompressMs { get { return lastCompressMs; } }
        internal static double LastWriteMs { get { return lastWriteMs; } }
        internal static string LastError { get { return lastError; } }

        internal static bool Busy
        {
            get { lock (Gate) { return Queue.Count > 0 || working; } }
        }

        internal static void Start()
        {
            if (worker != null) return;
            stopping = false;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "SaveOptWriter";
            worker.Start();
        }

        // ───────── 改后：4 参重载 ─────────
        internal static bool Enqueue(byte[] head, byte[] source, int sourceLength, string path)
        {
            if ((head == null || head.Length == 0) && (source == null || sourceLength <= 0)) return false;
            lock (Gate)
            {
                Queue.Enqueue(new WriteJob
                {
                    Head = head,
                    Source = source,
                    SourceLength = sourceLength,
                    Path = path
                });
                Idle.Reset();
            }
            Signal.Set();
            return true;
        }

        // ───────── 改前：5 参重载（含 PngFrom / PngTo）─────────
        // 【已停用】自动存档不再复制上一张 png，因此这个重载不再被调用。
        // 保留仅作历史记录。
        //
        // internal static bool Enqueue(byte[] head, byte[] source, int sourceLength, string path, string pngFrom, string pngTo)
        // {
        //     if ((head == null || head.Length == 0) && (source == null || sourceLength <= 0)) return false;
        //     lock (Gate)
        //     {
        //         Queue.Enqueue(new WriteJob
        //         {
        //             Head = head,
        //             Source = source,
        //             SourceLength = sourceLength,
        //             Path = path,
        //             PngFrom = pngFrom,
        //             PngTo = pngTo
        //         });
        //         Idle.Reset();
        //     }
        //     Signal.Set();
        //     return true;
        // }

        private static void Loop()
        {
            while (!stopping)
            {
                Signal.WaitOne();
                while (true)
                {
                    WriteJob job;
                    lock (Gate)
                    {
                        if (Queue.Count == 0) { Idle.Set(); break; }
                        job = Queue.Dequeue();
                        working = true;
                    }
                    Run(job);
                    lock (Gate) { working = false; }
                }
            }
        }

        private static void Run(WriteJob job)
        {
            string step = "start";
            try
            {
                step = "compress";
                byte[] compressed;
                var swc = System.Diagnostics.Stopwatch.StartNew();
                using (var ms = new MemoryStream())
                {
                    using (var z = new ZlibStream(ms, CompressionMode.Compress, CompressionLevel.BestSpeed))
                    {
                        z.Write(job.Source, 0, job.SourceLength);
                        z.Flush();
                    }
                    compressed = ms.ToArray();
                }
                swc.Stop();

                step = "writeFile";
                var sww = System.Diagnostics.Stopwatch.StartNew();
                string tmp = job.Path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    if (job.Head != null && job.Head.Length > 0) fs.Write(job.Head, 0, job.Head.Length);
                    fs.Write(compressed, 0, compressed.Length);
                    fs.Flush(true);
                }
                sww.Stop();

                step = "move";
                if (File.Exists(job.Path)) File.Delete(job.Path);
                File.Move(tmp, job.Path);

                // ───────── 改前：这里有一段 preview 复制 ─────────
                // 【已停用】自动存档不再复制上一张 png，因此这段被移除。
                // 保留仅作历史记录。
                //
                // step = "preview";
                // if (!string.IsNullOrEmpty(job.PngFrom) && !string.IsNullOrEmpty(job.PngTo)
                //     && job.PngFrom != job.PngTo && File.Exists(job.PngFrom))
                // {
                //     File.Copy(job.PngFrom, job.PngTo, true);
                // }

                lock (Gate)
                {
                    written++;
                    rawBytes += job.SourceLength;
                    outBytes += (job.Head == null ? 0 : job.Head.Length) + (long)compressed.Length;
                    lastCompressMs = swc.Elapsed.TotalMilliseconds;
                    lastWriteMs = sww.Elapsed.TotalMilliseconds;
                }
                Diag.Trace("[更好的存档] 后台完成：压缩 " + ((long)lastCompressMs) + " ms + 写盘 " + ((long)lastWriteMs)
                    + " ms，输出 " + (outBytes / 1048576.0).ToString("F1") + " MB 累计 -> " + Path.GetFileName(job.Path));
            }
            catch (Exception e)
            {
                lastError = e.GetType().Name + " " + e.Message;
                Debug.LogError("[更好的存档] 后台写盘失败于步骤[" + step + "]: " + job.Path + " : " + lastError);
            }
            finally
            {
                SaveBuffer.Release(job.Source);
            }
        }

        internal static void Flush(int timeoutMs)
        {
            if (worker == null || !Busy) return;
            Debug.Log("[更好的存档] 退出前等待后台写盘完成…");
            Idle.WaitOne(timeoutMs);
            if (Busy) Debug.LogWarning("[更好的存档] 后台写盘超时未完成");
        }

        internal static void Stop()
        {
            stopping = true;
            Signal.Set();
        }
    }
}