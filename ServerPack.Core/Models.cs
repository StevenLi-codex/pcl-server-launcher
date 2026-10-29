using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PclServerPack
{
    public sealed class ServerDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string Version { get; set; }
        public string PackUrl { get; set; }
        public string PackSha256 { get; set; }
    }
    public sealed class PackFile
    {
        public string Path { get; set; }
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public bool Optional { get; set; }
        public IList<string> Downloads { get; set; } = new List<string>();
        internal byte[] Content { get; set; }
    }
    public sealed class PreparedPack
    {
        public string ArchivePath { get; set; }
        public string Version { get; set; }
        public string MinecraftVersion { get; set; }
        public string LoaderDescription { get; set; }
        public string DependencyKey { get; set; }
        public IList<PackFile> Files { get; set; } = new List<PackFile>();
    }
    public sealed class SyncPlan
    {
        public int AddCount { get; set; }
        public int ReplaceCount { get; set; }
        public int RemoveCount { get; set; }
        public int KeepCount { get; set; }
        public string Summary { get; set; }
    }
    public static partial class PackService
    {
        internal const string StateDir = ".pcl-server-pack";
        public static bool IsManaged(string instanceRoot)
        {
            try
            {
                var path = Meta(instanceRoot, "state.json"); CheckTree(path);
                if (!File.Exists(path)) return false;
                using (Acquire(instanceRoot)) { NoPending(instanceRoot); ReadState(instanceRoot); return true; }
            }
            catch (IOException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Newtonsoft.Json.JsonException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static string ReadDependencyKey(string instanceRoot) { using (Acquire(instanceRoot)) return ReadState(instanceRoot).DependencyKey; }
    }
}
