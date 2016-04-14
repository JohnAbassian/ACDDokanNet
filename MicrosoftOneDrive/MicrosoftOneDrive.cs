﻿namespace Azi.Cloud.MicrosoftOneDrive
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Microsoft.OneDrive.Sdk;
    using Microsoft.OneDrive.Sdk.WindowsForms;

    public class MicrosoftOneDrive : IHttpCloud, IHttpCloudFiles, IHttpCloudNodes
    {
        private static readonly string[] Scopes = { "onedrive.readwrite", "wl.signin" };

        private Item rootItem;

        private IOneDriveClient oneDriveClient;

        public static string CloudServiceName => "Microsoft OneDrive";

        public static string CloudServiceIcon => "/Clouds.MicrosoftOneDrive;Component/images/cd_icon.png";

        public string Id { get; set; }

        public long AvailableFreeSpace => 0;

        string IHttpCloud.CloudServiceIcon => CloudServiceIcon;

        string IHttpCloud.CloudServiceName => CloudServiceName;

        public IHttpCloudFiles Files => this;

        public IHttpCloudNodes Nodes => this;

        public IAuthUpdateListener OnAuthUpdated { get; set; }

        public long TotalFreeSpace => GetDrive().Quota.Remaining ?? 0;

        public long TotalSize => GetDrive().Quota.Total ?? 0;

        public long TotalUsedSpace => GetDrive().Quota.Used ?? 0;

        public async Task<bool> AuthenticateNew(CancellationToken cs)
        {
            var form = new FormsWebAuthenticationUi();
            oneDriveClient = await OneDriveClient.GetAuthenticatedMicrosoftAccountClient(MicrosoftSecret.ClientId, "http://localhost:45674/authredirect", Scopes, MicrosoftSecret.ClientSecret, form).ConfigureAwait(false);

            return oneDriveClient.IsAuthenticated;
        }

        public async Task<bool> AuthenticateSaved(CancellationToken cs, string save)
        {
            return await AuthenticateNew(cs).ConfigureAwait(false);
        }

        public async Task SignOut(string save)
        {
            if (oneDriveClient != null)
            {
                await oneDriveClient.SignOutAsync().ConfigureAwait(false);
            }
        }

        public async Task<FSItem.Builder> CreateFolder(string parentid, string name)
        {
            var item = await GetItem(parentid).Children.Request().AddAsync(new Item { Name = name, Folder = new Folder() }).ConfigureAwait(false);
            return FromNode(item);
        }

        public async Task Download(string id, Func<Stream, Task> streammer, long? fileOffset = default(long?), int? length = default(int?))
        {
            using (var stream = await GetItem(id).Content.Request().GetAsync().ConfigureAwait(false))
            {
                if (fileOffset != null)
                {
                    stream.Position = fileOffset.Value;
                    await streammer(stream).ConfigureAwait(false);
                }
            }
        }

        public async Task<FSItem.Builder> GetNode(string id)
        {
            var item = await GetItem(id).Request().GetAsync().ConfigureAwait(false);
            return FromNode(item);
        }

        public async Task<int> Download(string id, byte[] result, int offset, long pos, int left)
        {
            using (var stream = await GetItem(id).Content.Request().GetAsync().ConfigureAwait(false))
            {
                stream.Position = pos;
                return await stream.ReadAsync(result, offset, left).ConfigureAwait(false);
            }
        }

        public async Task<FSItem.Builder> GetChild(string id, string name)
        {
            var items = await GetAllChildren(id).ConfigureAwait(false);
            var item = items.Where(i => i.Name == name).SingleOrDefault();
            if (item == null)
            {
                return null;
            }

            return FromNode(item);
        }

        public async Task<IList<FSItem.Builder>> GetChildren(string id)
        {
            var nodes = await GetAllChildren(id).ConfigureAwait(false);
            return nodes.Select(n => FromNode(n)).ToList();
        }

        public async Task<INodeExtendedInfo> GetNodeExtended(string id)
        {
            var item = await GetItem(id).Request().GetAsync().ConfigureAwait(false);
            var info = new CloudDokanNetItemInfo
            {
                WebLink = item.WebUrl,
                CanShareReadOnly = true,
                CanShareReadWrite = true,
                Id = item.Id
            };

            return info;
        }

        public async Task<FSItem.Builder> GetRoot()
        {
            return FromNode(await GetRootItem().ConfigureAwait(false));
        }

        public async Task<FSItem.Builder> Move(string itemId, string oldParentId, string newParentId)
        {
            var newitem = await GetItem(itemId).Request().UpdateAsync(new Item { ParentReference = new ItemReference { Id = newParentId } }).ConfigureAwait(false);
            return FromNode(newitem);
        }

        public async Task<FSItem.Builder> Overwrite(string id, Func<FileStream> streammer)
        {
            using (var stream = streammer())
            {
                var newitem = await GetItem(id).Content.Request().PutAsync<Item>(stream).ConfigureAwait(false);
                return FromNode(newitem);
            }
        }

        public async Task<FSItem.Builder> UploadNew(string parentId, string fileName, Func<FileStream> streammer)
        {
            using (var stream = streammer())
            {
                var newitem = await GetItem(parentId).ItemWithPath(fileName).Content.Request().PutAsync<Item>(stream).ConfigureAwait(false);
                return FromNode(newitem);
            }
        }

        public Task Remove(string itemId, string parentId)
        {
            throw new NotSupportedException();
        }

        public async Task<FSItem.Builder> Rename(string id, string newName)
        {
            var newitem = await GetItem(id).Request().UpdateAsync(new Item { Name = newName }).ConfigureAwait(false);
            return FromNode(newitem);
        }

        public async Task Trash(string id)
        {
            await GetItem(id).Request().DeleteAsync().ConfigureAwait(false);
        }

        public async Task<string> ShareNode(string id, NodeShareType type)
        {
            string t;
            switch (type)
            {
                case NodeShareType.ReadWrite:
                    t = "edit";
                    break;
                default:
                    t = "view";
                    break;
            }

            var perm = await GetItem(id).CreateLink(t).Request().PostAsync().ConfigureAwait(false);
            return perm.Link.WebUrl;
        }

        private static FSItem.Builder FromNode(Item node)
        {
            return new FSItem.Builder
            {
                Length = node.Size ?? 0,
                Id = node.Id,
                IsDir = node.Folder != null,
                ParentIds = (node.ParentReference != null) ? new ConcurrentBag<string>(new[] { node.ParentReference.Id }) : new ConcurrentBag<string>(),
                CreationTime = node.CreatedDateTime?.LocalDateTime ?? DateTime.UtcNow,
                LastAccessTime = node.LastModifiedDateTime?.LocalDateTime ?? DateTime.UtcNow,
                LastWriteTime = node.LastModifiedDateTime?.LocalDateTime ?? DateTime.UtcNow,
                Name = node.Name
            };
        }

        private async Task<Item> GetRootItem()
        {
            if (rootItem == null)
            {
                rootItem = await oneDriveClient
                             .Drive
                             .Root
                             .Request()
                             .GetAsync().ConfigureAwait(false);
            }

            return rootItem;
        }

        private async Task<IList<Item>> GetAllChildren(string id)
        {
            var result = new List<Item>();
            var request = GetItem(id).Children.Request();

            do
            {
                var nodes = await request.GetAsync().ConfigureAwait(false);
                result.AddRange(nodes.CurrentPage);
                request = nodes.NextPageRequest;
            }
            while (request != null);

            return result;
        }

        private Drive GetDrive()
        {
            return oneDriveClient.Drive.Request().GetAsync().Result;
        }

        private IItemRequestBuilder GetItem(string id)
        {
            return oneDriveClient.Drive.Items[id];
        }
    }
}
