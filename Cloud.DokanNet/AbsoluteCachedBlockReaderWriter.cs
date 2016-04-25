namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Tools;

    public class AbsoluteCachedBlockReaderWriter : AbstractReaderWriter
    {
        private readonly AbsoluteCache cache;
        private readonly FSItem item;

        public AbsoluteCachedBlockReaderWriter(FSItem item, AbsoluteCache cache)
        {
            this.cache = cache;
            this.item = item;
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(long position, byte[] buffer, int offset, int count, int timeout = 1000)
        {
            if (position >= item.Length)
            {
                return 0;
            }

            if (position + count > item.Length)
            {
                count = (int)(item.Length - position);
            }

            var bs = position / cache.BlockSize;
            var be = (position + count - 1) / cache.BlockSize;
            var blocks = cache.GetBlocks(item, bs, be).Result;
            var bspos = bs * cache.BlockSize;
            var bepos = ((be + 1) * cache.BlockSize) - 1;
            if (bs == be)
            {
                Array.Copy(blocks[0].Data, position - bspos, buffer, offset, count);
                return count;
            }

            var inblock = (int)(position - bspos);
            var inblockcount = blocks[0].CurrentSize - inblock;
            Array.Copy(blocks[0].Data, inblock, buffer, offset, inblockcount);
            offset += inblockcount;
            var left = count - inblockcount;

            for (int i = 1; i < blocks.Length - 1; i++)
            {
                Array.Copy(blocks[i].Data, 0, buffer, offset, blocks[i].CurrentSize);
                offset += blocks[i].CurrentSize;
                left -= blocks[i].CurrentSize;
            }

            Array.Copy(blocks[blocks.Length - 1].Data, 0, buffer, offset, left);

            return count;
        }

        public override void SetLength(long len)
        {
            throw new NotImplementedException();
        }

        public override void Write(long position, byte[] buffer, int offset, int count, int timeout = 1000)
        {
            throw new NotImplementedException();
        }

        protected override void Dispose(bool disposing)
        {
            throw new NotImplementedException();
        }
    }
}