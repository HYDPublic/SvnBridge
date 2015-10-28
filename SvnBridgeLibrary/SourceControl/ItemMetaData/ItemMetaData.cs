using System;
using System.Collections.Generic;
using CodePlex.TfsLibrary.RepositoryWebSvc;
using SvnBridge.Infrastructure; // Configuration

namespace SvnBridge.SourceControl
{
    public class ItemMetaData
    {
    	private FolderMetaData parent;

        public string Author;
        public bool OriginallyDeleted = false;
        public bool DataLoaded = false;
        public string Base64DiffData = null;
        public string Md5Hash = null; // Important helper to support maintaining a properly end-to-end checksummed data chain
        //public Exception DataLoadedError;
        public string DownloadUrl = null;
        public int Id;
        public int ItemRevision;
        public DateTime LastModifiedDate;
        public string Name;
        public Dictionary<string, string> Properties = new Dictionary<string, string>();
        public int PropertyRevision;
        public int SubItemRevision;

        public ItemMetaData()
        {
        }

        public ItemMetaData(string name)
        {
            Name = name;
        }

        public virtual ItemType ItemType
        {
            get { return ItemType.File; }
        }

        public virtual int Revision
        {
            get
            {
                if (SubItemRevision > PropertyRevision && SubItemRevision > ItemRevision)
                {
                    return SubItemRevision;
                }
                else if (PropertyRevision > ItemRevision)
                {
                    return PropertyRevision;
                }
                else
                {
                    return ItemRevision;
                }
            }
        }

		public override string ToString()
		{
			return Name + " @" + Revision;
		}

    	public void SetParent(FolderMetaData parentFolder)
    	{
    		parent = parentFolder;
    	}

        public bool IsSamePath(string path)
        {
            return IsSamePath(Name, path);
        }

        /// <remarks>
        /// See also FolderMetaData.MightContain()
        /// </remarks>
        public static bool IsSamePath(string itemPath, string pathCompare)
        {
            string itemPathCooked = itemPath;

            if (pathCompare.StartsWith("/"))
            {
                if (itemPathCooked.StartsWith("/") == false)
                    itemPathCooked = "/" + itemPathCooked;
            }

            return (string.Equals(
                itemPathCooked, pathCompare,
                WantCaseSensitiveMatch ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase)
            );
        }

        protected static bool WantCaseSensitiveMatch
        {
            get { return Configuration.SCMWantCaseSensitiveItemMatch; }
        }
    }
}
