namespace Azi.Cloud.DokanNet
{
    using Common;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Tools;
    public class AbsoluteCache
    {
        private const int BufferSize = 4096;
        private const int EofWaitInterval = 100;
        private const int PrefetchBlocksNumber = 5;
        private const int NoninterrutBlocksInterval = 5;
        private readonly TimeSpan prefetchWait = TimeSpan.FromMinutes(5);
        private readonly int blockSize;
        private readonly string cachePath;
        private readonly IHttpCloud cloud;
        private readonly TimeSpan timeout;

        public AbsoluteCache(IHttpCloud cloud, int blockSize, string cachePath)
            : this(cloud, blockSize, cachePath, TimeSpan.FromSeconds(25))
        {
        }

        public AbsoluteCache(IHttpCloud cloud, int blockSize, string cachePath, TimeSpan timeout)
        {
            this.timeout = timeout;
            this.blockSize = blockSize;
            this.cachePath = cachePath;
            this.cloud = cloud;
        }

        private string GetPath(FSItem item, long blockIndex)
        {
            return Path.Combine(cachePath, item.Id + "-" + blockIndex);
        }

        private CancellationToken GetTimeoutToken() => new CancellationTokenSource(timeout).Token;

        private CancellationToken GetPrefetchWaitToken() => new CancellationTokenSource(prefetchWait).Token;

        private class AbsoluteCacheItem
        {
            private readonly AbsoluteCache cache;
            private readonly FSItem item;
            private Dictionary<long, Block> blocks = new Dictionary<long, Block>();

            public AbsoluteCacheItem(AbsoluteCache cache, FSItem item)
            {
                this.item = item;
                this.cache = cache;
            }

            private int BlockSize => cache.blockSize;

            private IHttpCloud Cloud => cache.cloud;

            private int NumberOfBlocks => (int)((item.Length - 1) / cache.blockSize) + 1;

            public Block MakeBlock(long blockIndex)
            {
                Block result;
                lock (blocks)
                {
                    if (blocks.TryGetValue(blockIndex, out result))
                    {
                        return result;
                    }

                    result = new Block(blockIndex, BlockSize);
                    blocks.Add(blockIndex, result);
                    return result;
                }
            }

            public async Task<Block> GetBlock(long blockIndex)
            {
                var block = MakeBlock(blockIndex);
                if (await TryLoadFromDisk(block).ConfigureAwait(false))
                {
                    return block;
                }

                Prefetch(block);

                return block;
            }

            private async Task<bool> TryLoadFromDisk(Block block)
            {
                try
                {
                    var stream = new FileStream(cache.GetPath(item, block.BlockIndex), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    int red;
                    do
                    {
                        red = await block.AddBytesFromStream(stream).ConfigureAwait(false);
                    }
                    while (red != 0);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
            }

            private readonly BlockingCollection<Block> blocksQueue = new BlockingCollection<Block>(new ConcurrentQueue<Block>());

            private Stream Prefetch(Block block)
            {
                lock (blocksQueue)
                {
                    blocksQueue.Add(blockIndex);
                    if (!prefetchRunning)
                    {
                        var task = Task.Run(StartPrefetch);
                    }
                }

                try
                {
                    return new FileStream(cache.GetPath(item, blockIndex), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                }
                catch (FileNotFoundException)
                {
                    // Ignore
                }
            }

            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

            private bool prefetchRunning = false;

            private async Task StartPrefetch()
            {
                try
                {
                    var token = cancellation.Token;
                    long startBlockIndex;
                    RemoveCachedQueuedBlock();
                    while (blocksQueue.TryTake(out startBlockIndex))
                    {

                        token.ThrowIfCancellationRequested();
                        var offset = startBlockIndex * BlockSize;
                        await Cloud.Files.Download(item.Id, fileOffset: offset, streammer: async (stream) =>
                        {
                            long currentBlock = startBlockIndex;
                            long lastBlock = startBlockIndex + PrefetchBlocksNumber;
                            do
                            {
                                using (var file = cache.CreateNewBlockFile(item, currentBlock))
                                {
                                    var buf = new byte[BufferSize];
                                    int totalRed = 0;
                                    while (totalRed < BlockSize)
                                    {
                                        var red = await stream.ReadAsync(buf, 0, Math.Min(BlockSize - totalRed, BufferSize), token).ConfigureAwait(false);
                                        totalRed += red;
                                        await file.WriteAsync(buf, 0, red).ConfigureAwait(false);
                                    }
                                }

                                if (ShouldInterrupt(currentBlock))
                                {
                                    return;
                                }
                            }
                            while (currentBlock < lastBlock);
                        }).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Trace("Cancelled prefetch");
                }
                catch (Exception)
                {
                    Log.Error("Prefetch failed");
                }

                prefetchRunning = false;
            }

            private bool ShouldInterrupt(long currentBlock)
            {
                do
                {
                    if (RemoveCachedQueuedBlock())
                    {
                        break;
                    }
                } while (true);

                long next = blocksQueue.First();

                if ((next < currentBlock) || (next > currentBlock + NoninterrutBlocksInterval))
                {
                    return true;
                }

                return false;
            }


            /// <summary>
            /// Remove all blocks in prefetch queue which are already cached and not outdated
            /// </summary>
            /// <returns>True if any blocks left in queue</returns>
            private bool RemoveCachedQueuedBlock()
            {
                while (blocksQueue.Count != 0)
                {
                    long next = blocksQueue.First();
                    var path = cache.GetPath(item, next);
                    if (!File.Exists(path))
                    {
                        // TODO check for outdated block
                        return true;
                    }

                    blocksQueue.Take();
                }

                return false;
            }
        }

        private Stream CreateNewBlockFile(FSItem item, long currentBlock)
        {
            return new FileStream(GetPath(item, currentBlock), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
    }

    public class AbsoluteCachedBlockReaderWriter : AbstractReaderWriter
    {
        private readonly AbsoluteCache cache;
        private readonly FSItem item;

        public AbsoluteCachedStream(FSItem item, AbsoluteCache cache)
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
            var blocks = cache.GetBlocks(item, bs, be);
            var bspos = bs * cache.BlockSize;
            var bepos = ((be + 1) * cache.BlockSize) - 1;
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