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

        internal static MemoryStream Rent(int capacity)
        {
            if (capacity < 1) capacity = 1;
            lock (Gate)
            {
                if (slot == null)
                {
                    slot = new byte[(int)(capacity * Margin)];
                    allocations++;
                }
                else if (slot.Length < capacity)
                {
                    slot = new byte[(int)(capacity * Margin)];
                    allocations++;
                }

                if (!busy)
                {
                    busy = true;
                    reuses++;
                    return Wrap(slot);
                }
            }

            fallbacks++;
            return new MemoryStream(capacity);
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
                + " 次，退化为一次性分配 " + fallbacks + " 次，当前池 " + mb + " MB";
        }

        private static MemoryStream Wrap(byte[] buffer)
        {
            var ms = new MemoryStream(buffer, 0, buffer.Length, true, true);
            ms.SetLength(0);
            return ms;
        }
    }
}
