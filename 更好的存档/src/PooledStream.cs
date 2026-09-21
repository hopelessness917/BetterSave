using System;
using System.IO;

namespace SaveOpt
{
    internal sealed class PooledStream : MemoryStream
    {
        private byte[] buf;
        private int len;
        private int pos;

        internal PooledStream(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            buf = buffer;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return len; } }

        public override long Position
        {
            get { return pos; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException("value");
                pos = (int)value;
            }
        }

        public override int Capacity
        {
            get { return buf.Length; }
            set
            {
                if (value < len) throw new ArgumentOutOfRangeException("value");
                if (value > buf.Length) Ensure(value);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentException("offset/count");
            if (count == 0) return;
            Ensure(pos + count);
            Buffer.BlockCopy(buffer, offset, buf, pos, count);
            pos += count;
            if (pos > len) len = pos;
        }

        public override void WriteByte(byte value)
        {
            Ensure(pos + 1);
            buf[pos++] = value;
            if (pos > len) len = pos;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            int n = len - pos;
            if (n > count) n = count;
            if (n <= 0) return 0;
            Buffer.BlockCopy(buf, pos, buffer, offset, n);
            pos += n;
            return n;
        }

        public override int ReadByte()
        {
            return pos < len ? buf[pos++] : -1;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long t;
            if (origin == SeekOrigin.Begin) t = offset;
            else if (origin == SeekOrigin.Current) t = pos + offset;
            else t = len + offset;
            if (t < 0) throw new IOException("Seek before begin");
            pos = (int)t;
            return pos;
        }

        public override void SetLength(long value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException("value");
            int v = (int)value;
            Ensure(v);
            if (v > len) Array.Clear(buf, len, v - len);
            len = v;
            if (pos > len) pos = len;
        }

        public override byte[] GetBuffer()
        {
            return buf;
        }

        public override bool TryGetBuffer(out ArraySegment<byte> segment)
        {
            segment = new ArraySegment<byte>(buf, 0, len);
            return true;
        }

        public override byte[] ToArray()
        {
            byte[] r = new byte[len];
            Buffer.BlockCopy(buf, 0, r, 0, len);
            return r;
        }

        public override void Flush()
        {
        }

        private void Ensure(int need)
        {
            if (need <= buf.Length) return;
            int size = buf.Length;
            if (size < 4096) size = 4096;
            while (size < need)
            {
                int next = size * 2;
                size = next <= size ? need : next;
            }
            byte[] grown = new byte[size];
            Buffer.BlockCopy(buf, 0, grown, 0, len);
            buf = grown;
            SaveBuffer.NoteGrowth(size);
        }
    }
}
