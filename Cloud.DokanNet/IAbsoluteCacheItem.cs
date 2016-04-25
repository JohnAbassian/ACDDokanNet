using System.Threading.Tasks;
using Azi.Cloud.Common;

namespace Azi.Cloud.DokanNet
{
    public interface IAbsoluteCacheItem
    {
        FSItem FSItem { get; }

        void Dispose();

        Task<Block> GetBlock(long blockIndex);

        void ReleaseBlock(Block block);
    }
}