using System;
using System.Collections.Generic;
using System.Net;
using CodePlex.TfsLibrary.ObjectModel;
using CodePlex.TfsLibrary.RepositoryWebSvc;
using SvnBridge.Interfaces; // CachedResult
using SvnBridge.Net;
using SvnBridge.Proxies;
using SvnBridge.SourceControl;
using SvnBridge.Cache;

namespace SvnBridge.Infrastructure
{
    [Interceptor(typeof(TracingInterceptor))]
    public class MetaDataRepositoryCache : MetaDataRepositoryBase
    {
        private readonly MemoryBasedPersistentCache persistentCache;

        public MetaDataRepositoryCache(
            TFSSourceControlService sourceControlService,
            string serverUrl,
            ICredentials credentials,
            string rootPath,
            MemoryBasedPersistentCache persistentCache)
            : base(
                sourceControlService,
                serverUrl,
                credentials,
                rootPath)
        {
            this.persistentCache = persistentCache;
        }

        // TODO: any reason why
        // in this Cache class implementation this *array-based* method
        // iterates over *single*-path QueryItems() calls,
        // whereas in NoCache class the array-based QueryItems()
        // directly forwards to array-based SourceControlProvider interface method?
        // This is a very asymmetric (and obviously performance-hampering due to huge requests overhead)
        // differing implementation between Cache/NoCache,
        // should ideally be improved if possible.
        public override SourceItem[] QueryItems(int revision, string[] paths, Recursion recursion)
        {
            List<SourceItem> items = new List<SourceItem>();
            foreach (string path in paths)
            {
                foreach (SourceItem item in QueryItems(revision, path, recursion))
                {
                    items.Add(item);
                }
            }

            return items.ToArray();
        }

        public override SourceItem[] QueryItems(int revision, int itemId)
        {
            return sourceControlService.QueryItems(serverUrl, credentials,
                new int[] { itemId },
                revision,
                0);
        }

        public override SourceItem[] QueryItems(int revision, string path, Recursion recursion)
        {
            List<SourceItem> list = null;
            persistentCache.UnitOfWork(delegate
            {
                string serverPath = GetServerPath(path);

                if (serverPath == Constants.ServerRootPath && recursion == Recursion.None)
                {
                    SourceItem[] items =
                        sourceControlService.QueryItems(serverUrl, credentials,
                            serverPath,
                            RecursionType.None,
                            VersionSpec.FromChangeset(revision),
                            DeletedState.NonDeleted,
                            ItemType.Any,
                            false, 0);

                    list = new List<SourceItem>(items);
                    return;
                }

                EnsureRevisionIsCached(revision, path);

                string cacheKey = GetItemsListCacheKey(recursion, revision, serverPath);

                list = persistentCache.GetList<SourceItem>(cacheKey);
                list.Sort(delegate(SourceItem x, SourceItem y)
                {
                    return x.RemoteName.CompareTo(y.RemoteName);
                });
            });
            return list.ToArray();
        }

        private string GetItemsListCacheKey(Recursion recursion, int revision, string path)
        {
            switch (recursion)
            {
                case Recursion.Full:
                    return GetItemFullPathCacheKey(revision, path);
                case Recursion.OneLevel:
                    return GetItemOneLevelCacheKey(revision, path);
                case Recursion.None:
                    return GetItemNoRecursionCacheKey(revision, path);
                default:
                    throw new NotSupportedException();
            }
        }

        private string GetItemNoRecursionCacheKey(int revision, string path)
        {
            return "No recursion of: " + GetItemCacheKey(revision, path);
        }

        private string CurrentUserName
        {
            get
            {
                const string strCurrentUserName = "CurrentUserName";
                if (RequestCache.Items[strCurrentUserName] == null)
                {
                    NetworkCredential credential = credentials.GetCredential(new Uri(serverUrl), "Basic");
                    RequestCache.Items[strCurrentUserName] = credential.UserName + "@" + credential.Domain;
                }
                return (string)RequestCache.Items[strCurrentUserName];
            }
        }

        private void EnsureRevisionIsCached(int revision, string path)
        {
            string serverPath = GetServerPath(path);

            // optimizing access to properties by always getting the entire 
            // properties folder when accessing the folder props
            if (serverPath.EndsWith(Constants.FolderPropFilePath))
                serverPath = GetParentName(serverPath);

            // already cached this version, skip inserting
            if (IsInCache(revision, serverPath))
                return;
            string cacheKey = CreateRevisionAndPathCacheKey(revision, serverPath);
            persistentCache.UnitOfWork(delegate
            {
                // Once we are safely within the protected lock area here,
                // we have to make a second test,
                // to ensure that another thread
                // did not already read this version
                if (IsInCache(revision, serverPath))
                    return;

                SourceItem[] items = QueryItemsInternal(revision, ref serverPath);

                foreach (SourceItem item in items)
                {
                    string itemCacheKey = GetItemCacheKey(revision, item.RemoteName);

                    persistentCache.Set(itemCacheKey, item);

                    persistentCache.Add(GetItemNoRecursionCacheKey(revision, item.RemoteName), itemCacheKey);
                    persistentCache.Add(GetItemOneLevelCacheKey(revision, item.RemoteName), itemCacheKey);
                    persistentCache.Add(GetItemFullPathCacheKey(revision, item.RemoteName), itemCacheKey);

                    string parentDirectory = GetParentName(item.RemoteName);
                    persistentCache.Add(GetItemOneLevelCacheKey(revision, parentDirectory), itemCacheKey);

                    do
                    {
                        persistentCache.Add(GetItemFullPathCacheKey(revision, parentDirectory), itemCacheKey);
                        parentDirectory = GetParentName(parentDirectory);
                    } while (parentDirectory != Constants.ServerRootPath && string.IsNullOrEmpty(parentDirectory) == false);
                }

                if (items.Length == 0)
                    AddMissingItemToCache(revision, serverPath);

                persistentCache.Set(cacheKey, true);
            });
        }

        private SourceItem[] QueryItemsInternal(int revision, ref string serverPath)
        {
            VersionSpec versionSpec = VersionSpec.FromChangeset(revision);
            SourceItem[] items =
                sourceControlService.QueryItems(serverUrl, credentials,
                    serverPath,
                    RecursionType.Full, // SVNBRIDGE_WARNING_REF_RECURSION
                    versionSpec,
                    DeletedState.NonDeleted,
                    ItemType.Any,
                    false, 0);

            if (items.Length == 1 && items[0].ItemType == ItemType.File)
            {
                //change it to the directory name, can't use the Path class
                // because that will change the '/' to '\'
                serverPath = serverPath.Substring(0, serverPath.LastIndexOf('/'));

                items =
                sourceControlService.QueryItems(serverUrl, credentials,
                    serverPath,
                    RecursionType.Full, // SVNBRIDGE_WARNING_REF_RECURSION
                    versionSpec,
                    DeletedState.NonDeleted,
                    ItemType.Any,
                    false, 0);
            }
            return items;
        }

        private void AddMissingItemToCache(int revision, string serverPath)
        {
            string parentDirectory = GetParentName(serverPath);

            if (parentDirectory == "$")
                return;

            bool parentDirDoesNotExist =
                QueryItems(revision, parentDirectory, Recursion.None).Length == 0;

            if (!parentDirDoesNotExist)
                return;

            persistentCache.Add(GetItemOneLevelCacheKey(revision, parentDirectory), null);
            // this lies to the cache system, making it think that the parent
            // directory is cached, when in truth the parent directory doesn't even exist
            // this saves going to the server again for files in the same directory
            string parentCacheKey = CreateRevisionAndPathCacheKey(revision, serverPath);
            persistentCache.Set(parentCacheKey, true);
        }

        private string GetItemFullPathCacheKey(int revision, string parentDirectory)
        {
            return "Full path of " + GetItemCacheKey(revision, parentDirectory);
        }

        private string GetItemOneLevelCacheKey(int revision, string parentDirectory)
        {
            return "One Level of " + GetItemCacheKey(revision, parentDirectory);
        }

        private string GetItemCacheKey(int revision, string path)
        {
            return "ServerUrl: " + serverUrl +
                   ", UserName: " + CurrentUserName +
                   ", Revision: " + revision +
                   ", Path: " + path;
        }

        private static string GetParentName(string name)
        {
            int lastIndexOfSlash = name.LastIndexOf('/');
            if (lastIndexOfSlash == -1)
                return name;
            string parentPath = name.Substring(0, lastIndexOfSlash);
            if (parentPath == "$")
                return Constants.ServerRootPath;
            return parentPath ;
        }

        public bool IsInCache(int revision, string path)
        {
            CachedResult result;
            string serverPath = path;
            do
            {
                string cacheKey = CreateRevisionAndPathCacheKey(revision, serverPath);
                result = persistentCache.Get(cacheKey);

                if (serverPath.IndexOf('/') == -1)
                    break;

                serverPath = serverPath.Substring(0, serverPath.LastIndexOf('/'));
            } while (result == null);


            return result != null;
        }

        private string CreateRevisionAndPathCacheKey(int revision, string serverPath)
        {
            return "Revision: " + revision +
                   ", ServerUrl: " + serverUrl +
                   ", UserName: " + CurrentUserName +
                   ", RootPath: " + serverPath;
        }

        public void ClearCache()
        {
            persistentCache.Clear();
        }
    }
}
