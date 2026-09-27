using System;
using System.IO;

namespace SaveOpt
{
    internal static class SaveBuffer
    {
        private const double Margin = 1.5;

        private static readonly object Gate = new object();
        private static byte[] slot;
        private static bool busy;
        private static long reuses;
        private static long allocations;
        private static long fallbacks;
        private static bool warned;
        private static int floor;
        private static long grew;

        internal static MemoryStream Rent(int capacity)
        {
            if (capacity < 1) capacity = 1;
            lock (Gate)
            {
                int need = capacity;
                if (need < floor) need = floor;
                if (slot == null || slot.Length < need)
                {
                    int want = (int)(capacity * Margin);
                    if (want < need) want = need;
                    slot = new byte[want];
                    allocations++;
                }

                if (!busy)
                {
                    busy = true;
                    reuses++;
                    return new PooledStream(slot);
                }
            }

            fallbacks++;
            return new MemoryStream(capacity);
        }

        internal static void NoteGrowth(int size)
        {
            lock (Gate)
            {
                if (size <= floor) return;
                floor = size;
                grew++;
            }
        }

        internal static void Release(byte[] buffer)
        {
            if (buffer == null) return;
            lock (Gate)
            {
                if (ReferenceEquals(buffer, slot)) busy = false;
            }
        }

        internal static void CheckUsage(int written)
        {
            lock (Gate)
            {
                if (slot == null || written <= 0 || warned) return;
                if ((long)written * 10 > (long)slot.Length * 9)
                {
                    warned = true;
                    Debug.LogWarning("[更好的存档] 存档缓冲池余量偏低：本次写入 " + (written / 1048576)
                        + " MB / 池 " + (slot.Length / 1048576) + " MB，下次会自动扩大");
                }
            }
        }

        internal static void ReleaseStale()
        {
            if (Sink.Busy) return;
            lock (Gate)
            {
                busy = false;
            }
        }

        internal static string Summary()
        {
            int mb = 0;
            lock (Gate)
            {
                if (slot != null) mb = slot.Length / 1048576;
            }
            return "[更好的存档] 存档缓冲池：复用 " + reuses + " 次，分配 " + allocations
                + " 次，退化为一次性分配 " + fallbacks + " 次，自动扩容 " + grew + " 次，当前池 " + mb + " MB";
        }
    }
}
