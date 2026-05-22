using System;

namespace TNovUtils
{
    public class FamilyCacheItem
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public DateTime LastModified { get; set; }
        public int VersionNumber { get; set; }
        public string VersionString { get; set; }
        public string PreviewImageBase64 { get; set; }
    }
}