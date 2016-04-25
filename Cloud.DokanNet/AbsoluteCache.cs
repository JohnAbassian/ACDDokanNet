namespace Azi.Cloud.DokanNet
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;

    public class AbsoluteCache : IDisposable
    {
        public const int NoninterrutBlocksInterval = 5;
        public const int PrefetchBlocksNumber = 5;
        public static readonly TimeSpan PrefetchWait = TimeSpan.FromMinutes(5);

        private const int BufferSize = 4096;
        private const int EofWaitInterval = 100;
        private readonly string cachePath;
        private readonly IHttpCloud cloud;
        private readonly TimeSpan timeout;

        private Dictionary<string, AbsoluteCacheItem> items = new Dictionary<string, AbsoluteCacheItem>();

        public AbsoluteCache(IHttpCloud cloud, int blockSize, string cachePath)
            : this(cloud, blockSize, cachePath, TimeSpan.FromSeconds(25))
        {
        }

        public AbsoluteCache(IHttpCloud cloud, int blockSize, string cachePath, TimeSpan timeout)
        {
            this.timeout = timeout;
            BlockSize = blockSize;
            this.cachePath = cachePath;
            this.cloud = cloud;
        }

        public int BlockSize { get; }

        public IHttpCloud Cloud => cloud;

        public void Dispose()
        {
            foreach (var item in items.Values)
            {
                item.Dispose();
            }
        }

        public async Task<Block[]> GetBlocks(FSItem fsitem, long bs, long be)
        {
            var item = MakeItem(fsitem);
            var result = new Block[be - bs + 1];
            for (long index = 0; index < result.Length; index++)
            {
                result[index] = await item.GetBlock(index + bs).ConfigureAwait(false);
            }

            return result;
        }

        internal string GetPath(FSItem item, long blockIndex)
        {
            return Path.Combine(cachePath, item.Id + "-" + blockIndex);
        }

        internal void ReleaseItem(AbsoluteCacheItem item)
        {
            lock (items)
            {
                items.Remove(item.FSItem.Id);
                item.Dispose();
            }
        }

        private CancellationToken GetPrefetchWaitToken() => new CancellationTokenSource(PrefetchWait).Token;

        private CancellationToken GetTimeoutToken() => new CancellationTokenSource(timeout).Token;

        private AbsoluteCacheItem MakeItem(FSItem fsitem)
        {
            AbsoluteCacheItem result;
            lock (items)
            {
                if (items.TryGetValue(fsitem.Id, out result))
                {
                    return result;
                }

                result = new AbsoluteCacheItem(this, fsitem);
                items.Add(fsitem.Id, result);
                return result;
            }
        }
    }
}