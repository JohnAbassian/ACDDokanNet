namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Tools;

    public class AbsoluteCacheItem : IDisposable, IAbsoluteCacheItem
    {
        private readonly BlockingQueue<Block> blocksQueue = new BlockingQueue<Block>();
        private readonly AbsoluteCache cache;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        private Dictionary<long, Block> blocks = new Dictionary<long, Block>();
        private bool prefetchRunning = false;
        private object prefetchRunningSync = new object();
        private Task prefetchTask;

        public AbsoluteCacheItem(AbsoluteCache cache, FSItem item)
        {
            FSItem = item;
            this.cache = cache;
        }

        private int BlockSize => cache.BlockSize;

        private IHttpCloud Cloud => cache.Cloud;

        public FSItem FSItem { get; }

        private int NumberOfBlocks => (int)((FSItem.Length - 1) / cache.BlockSize) + 1;

        public void Dispose()
        {
            cancellation.Cancel();
            prefetchTask.Wait();
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

        public void ReleaseBlock(Block block)
        {
            lock (blocks)
            {
                if (--block.RefCounter == 0)
                {
                    blocks.Remove(block.BlockIndex);
                }

                if (blocks.Count == 0)
                {
                    cache.ReleaseItem(this);
                }
            }
        }

        private Block MakeBlock(long blockIndex)
        {
            Block result;
            lock (blocks)
            {
                if (blocks.TryGetValue(blockIndex, out result))
                {
                    result.RefCounter++;
                    return result;
                }

                result = new Block(this, blockIndex, BlockSize);
                blocks.Add(blockIndex, result);
                result.RefCounter++;
                return result;
            }
        }

        private void Prefetch(Block block)
        {
            lock (blocksQueue)
            {
                blocksQueue.Enqueue(block);
                lock (prefetchRunningSync)
                {
                    if (!prefetchRunning)
                    {
                        prefetchRunning = true;
                        prefetchTask = Task.Factory.StartNew(PrefetchLoop, TaskCreationOptions.LongRunning);
                    }
                }
            }
        }

        private async Task PrefetchLoop()
        {
            try
            {
                var token = cancellation.Token;
                Block block;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    lock (prefetchRunningSync)
                    {
                        if (!blocksQueue.TryDequeue(out block))
                        {
                            prefetchRunning = false;
                            return;
                        }
                    }

                    var offset = block.BlockIndex * BlockSize;
                    await Cloud.Files.Download(FSItem.Id, fileOffset: offset, streammer: async (stream) =>
                    {
                        Block currentBlock = block;
                        long lastBlock = block.BlockIndex + AbsoluteCache.PrefetchBlocksNumber;
                        do
                        {
                            int totalRed = 0;
                            while (totalRed < BlockSize)
                            {
                                var red = await block.ReadFromStream(stream).ConfigureAwait(false);
                                totalRed += red;
                            }

                            block.MakeComplete();

                            var interruptTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, new CancellationTokenSource(AbsoluteCache.PrefetchWait).Token);
                            if (ShouldInterrupt(currentBlock))
                            {
                                return;
                            }
                        }
                        while (currentBlock.BlockIndex < lastBlock);
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Trace("Canceled prefetch");
            }
            catch (Exception)
            {
                Log.Error("Prefetch failed");
            }
            finally
            {
                prefetchRunning = false;
            }
        }

        private bool ShouldInterrupt(Block currentBlock)
        {
            var next = blocksQueue.Peek();
            if ((next.BlockIndex < currentBlock.BlockIndex) || (next.BlockIndex > currentBlock.BlockIndex + AbsoluteCache.NoninterrutBlocksInterval))
            {
                return true;
            }

            return false;
        }

        private async Task<bool> TryLoadFromDisk(Block block)
        {
            try
            {
                var stream = new FileStream(cache.GetPath(FSItem, block.BlockIndex), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                int red;
                do
                {
                    red = await block.ReadFromStream(stream).ConfigureAwait(false);
                }
                while (red != 0);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }
    }
}