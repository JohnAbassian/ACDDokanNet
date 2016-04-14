namespace Azi.Cloud.DokanNet
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Nito.AsyncEx;

    public class Block
    {
        private DateTime? lastUpdate;

        private object updateSync = new object();

        private AsyncAutoResetEvent updateEvent = new AsyncAutoResetEvent(false);

        internal Block(long n, int expectedSize)
        {
            BlockIndex = n;
            Data = new byte[expectedSize];
            lastUpdate = DateTime.UtcNow;
        }

        public long BlockIndex { get; }

        public bool Complete { get; } = false;

        public int CurrentSize { get; } = 0;

        public byte[] Data { get; }

        public DateTime? LastUpdate => lastUpdate;

        public async Task<int> AddBytesFromStream(Stream stream) => await AddBytesFromStream(stream, CancellationToken.None).ConfigureAwait(false);

        public async Task<int> AddBytesFromStream(Stream stream, CancellationToken token)
        {
            var red = await stream.ReadAsync(Data, CurrentSize, Data.Length - CurrentSize, token).ConfigureAwait(false);

            lock (updateSync)
            {
                lastUpdate = DateTime.UtcNow;
                updateEvent.Set();
            }

            return red;
        }

        public async Task<DateTime> WaitUpdate(DateTime prevUpdate) => await WaitUpdate(prevUpdate, CancellationToken.None).ConfigureAwait(false);

        public async Task<DateTime> WaitUpdate(DateTime prevUpdate, CancellationToken token)
        {
            do
            {
                lock (updateSync)
                {
                    if (lastUpdate != null && lastUpdate > prevUpdate)
                    {
                        return lastUpdate.Value;
                    }
                }

                await updateEvent.WaitAsync(token).ConfigureAwait(false);
            }
            while (true);
        }
    }
}