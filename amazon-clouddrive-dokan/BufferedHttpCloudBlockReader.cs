﻿namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Azi.Cloud.Common;
    using Azi.Tools;

    public class BufferedHttpCloudBlockReader : AbstractReaderWriter
    {
        private const int BlockSize = 1 * 1024 * 1024;
        private const int KeepLastBlocks = 5;

        private readonly ConcurrentDictionary<long, Block> cachedBlocks = new ConcurrentDictionary<long, Block>(5, KeepLastBlocks * 5);
        private IHttpCloud cloud;
        private FSItem item;
        private long lastBlock = 0;

        public BufferedHttpCloudBlockReader(FSItem item, IHttpCloud cloud)
        {
            this.item = item;
            this.cloud = cloud;
        }

        public override void Flush()
        {
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

            var bs = position / BlockSize;
            var be = (position + count - 1) / BlockSize;
            var blocks = GetBlocks(bs, be);
            var bspos = bs * BlockSize;
            var bepos = ((be + 1) * BlockSize) - 1;
            if (bs == be)
            {
                Array.Copy(blocks[0], position - bspos, buffer, offset, count);
                return count;
            }

            var inblock = (int)(position - bspos);
            var inblockcount = blocks[0].Length - inblock;
            Array.Copy(blocks[0], inblock, buffer, offset, inblockcount);
            offset += inblockcount;
            var left = count - inblockcount;

            for (int i = 1; i < blocks.Length - 1; i++)
            {
                Array.Copy(blocks[i], 0, buffer, offset, blocks[i].Length);
                offset += blocks[i].Length;
                left -= blocks[i].Length;
            }

            Array.Copy(blocks[blocks.Length - 1], 0, buffer, offset, left);

            return count;
        }

        public override void SetLength(long len)
        {
            throw new NotSupportedException();
        }

        public override void Write(long position, byte[] buffer, int offset, int count, int timeout = 1000)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
        }

        private Block DownloadBlock(long block)
        {
            if (lastBlock != block)
            {
                Log.Warn($"Buffered Read block changed from {lastBlock} to {block}");
            }

            lastBlock = block + 1;
            var pos = block * BlockSize;
            var count = pos + BlockSize <= item.Length ? BlockSize : (int)(item.Length - pos);
            if (count == 0)
            {
                return new Block(block, new byte[0]);
            }

            var result = new byte[count];
            int offset = 0;
            int left = count;
            while (left > 0)
            {
                int red = cloud.Files.Download(item.Id, result, offset, pos, left).Result;
                if (red == 0)
                {
                    Log.Error("Download 0");
                    throw new InvalidOperationException("Download 0");
                }

                offset += red;
                left -= red;
            }

            return new Block(block, result);
        }

        private byte[][] GetBlocks(long v1, long v2)
        {
            var result = new byte[v2 - v1 + 1][];
            var tasks = new List<Task>();
            for (long block = v1; block <= v2; block++)
            {
                long blockcopy = block;
                tasks.Add(Task.Run(() =>
                {
                    var b = cachedBlocks.GetOrAdd(blockcopy, DownloadBlock);
                    b.Access = DateTime.UtcNow;

                    while (cachedBlocks.Count > KeepLastBlocks)
                    {
                        var del = cachedBlocks.Values.Aggregate((curMin, x) => (curMin == null || (x.Access < curMin.Access)) ? x : curMin);
                        Block remove;
                        cachedBlocks.TryRemove(del.N, out remove);
                    }

                    result[blockcopy - v1] = b.Data;
                }));
            }

            Task.WhenAll(tasks).Wait();
            return result;
        }
    }
}