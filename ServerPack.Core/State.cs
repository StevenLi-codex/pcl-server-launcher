using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace PclServerPack
{
    public static partial class PackService
    {
        const int MaxFiles = 20000;
        const long MaxFileBytes = 512L * 1024 * 1024;
        const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
        internal sealed class State
        {
            public int Format { get; set; } = 1;
            public string Version { get; set; }
            public string DependencyKey { get; set; }
            public Dictionary<string, string> Files { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        static string Meta(string root, string name)
        {
            var full = Path.GetFullPath(root);
            CheckTree(full);
            CheckCaseAlias(full, StateDir);
            var dir = Path.Combine(full, StateDir); CheckTree(dir);
            if (!string.IsNullOrEmpty(name)) CheckCaseAlias(dir, name);
            return Path.Combine(dir, name);
        }
        static void CheckCaseAlias(string directory, string name)
        {
            if (!Directory.Exists(directory)) return;
            var aliases = Directory.EnumerateFileSystemEntries(directory).Where(p => Equal(Path.GetFileName(p), name)).Take(2).ToArray();
            if (aliases.Length > 1 || (aliases.Length == 1 && !string.Equals(Path.GetFileName(aliases[0]), name, StringComparison.Ordinal))) throw new IOException("Noncanonical existing case alias: " + name);
        }
        static FileStream Acquire(string root)
        {
            CheckTree(Path.GetFullPath(root));
            Directory.CreateDirectory(root);
            CheckTree(Meta(root, "lock"));
            Directory.CreateDirectory(Meta(root, ""));
            return new FileStream(Meta(root, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        static void CheckTree(string fullPath)
        {
            for (var p = Path.GetFullPath(fullPath); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symlink/reparse path is forbidden: " + p); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        static State ReadState(string root)
        {
            var path = Meta(root, "state.json"); CheckTree(path);
            if (!File.Exists(path)) throw new InvalidOperationException("Instance is not managed; adopt a verified isolated installation first.");
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized state.");
            var s = JsonConvert.DeserializeObject<State>(File.ReadAllText(path));
            if (s == null || s.Format != 1 || string.IsNullOrEmpty(s.DependencyKey) || s.Files == null || s.Files.Count > MaxFiles) throw new InvalidDataException("Invalid state.");
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in s.Files) { ValidatePath(f.Key); RequireHash(f.Value, 128); if (normalized.ContainsKey(f.Key)) throw new InvalidDataException("Duplicate state path."); normalized.Add(f.Key, f.Value); }
            s.Files = normalized;
            return s;
        }
        static void WriteState(string root, State state)
        {
            var path = Meta(root, "state.json"); CheckTree(path);
            var tmp = Meta(root, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllText(tmp, JsonConvert.SerializeObject(state, Formatting.Indented), new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        static void RequireHash(string hash, int length)
        {
            if (hash == null || hash.Length != length || hash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Missing or invalid pinned hash.");
        }
        static string Hash(string path)
        {
            CheckTree(path);
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("File exceeds limit.");
            using (var s = File.OpenRead(path)) using (var h = SHA512.Create()) return Hex(h.ComputeHash(s));
        }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static string Target(string root, string relative)
        {
            ValidatePath(relative); var path = Path.GetFullPath(root); CheckTree(path);
            foreach (var part in relative.Split('/'))
            {
                if (File.Exists(path)) throw new IOException("File blocks parent directory: " + path);
                CheckCaseAlias(path, part);
                path = Path.Combine(path, part); CheckTree(path);
            }
            return path;
        }
        static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 220 || path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal) || path.Any(c => c < 32 || c == 127 || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format || ":*?\"<>|~".IndexOf(c) >= 0)) throw new InvalidDataException("Unsafe pack path: " + path);
            ValidateSegments(path);
            var parts = path.Split('/');
            var top = parts[0].ToLowerInvariant();
            if (!(parts.Length > 1 && new[] { "mods", "resourcepacks", "shaderpacks", "config", "defaultconfigs", "kubejs", "scripts" }.Contains(top)) && !(parts.Length == 1 && (Equal(path, "README.txt") || Equal(path, "CREDITS.json")))) throw new InvalidDataException("Protected or unsupported pack path: " + path);
            if (parts.Any(p => p.Equals(".pcl-server-pack", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Managed metadata path is protected.");
        }
        static void ValidateSegments(string path)
        {
            foreach (var p in path.Split('/'))
            {
                if (p.Length == 0 || p == "." || p == ".." || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) || p.Length > 100 || p.Any(c => c < 32 || c == 127 || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format || ":*?\"<>|~\\".IndexOf(c) >= 0)) throw new InvalidDataException("Unsafe path component.");
                var device = p.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(device) || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3]))) throw new InvalidDataException("Windows device path.");
            }
        }
        static void ValidatePack(PreparedPack pack)
        {
            if (pack == null || string.IsNullOrEmpty(pack.DependencyKey) || pack.Files == null || pack.Files.Count > MaxFiles) throw new InvalidDataException("Invalid prepared pack.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in pack.Files)
            {
                if (f == null) throw new InvalidDataException("Null pack file.");
                ValidatePath(f.Path);
                if (!seen.Add(f.Path)) throw new InvalidDataException("Duplicate case-insensitive pack path.");
                if (!string.IsNullOrEmpty(f.Sha512)) RequireHash(f.Sha512, 128); else RequireHash(f.Sha1, 40);
            }
            foreach (var path in seen) { var p = path; while (p.Contains('/')) { p = p.Substring(0, p.LastIndexOf('/')); if (seen.Contains(p)) throw new InvalidDataException("File/directory alias."); } }
        }
        static bool Matches(string path, PackFile file)
        {
            if (!File.Exists(path)) return false;
            if (!string.IsNullOrEmpty(file.Sha512) && !Equal(Hash(path), file.Sha512)) return false;
            if (!string.IsNullOrEmpty(file.Sha1)) { RequireHash(file.Sha1, 40); using (var s = File.OpenRead(path)) using (var h = SHA1.Create()) if (!Equal(Hex(h.ComputeHash(s)), file.Sha1)) return false; }
            return true;
        }
        static void CheckHashBudget(string root, IEnumerable<string> paths)
        {
            long total = 0;
            foreach (var relative in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = Target(root, relative);
                if (!File.Exists(path)) continue;
                var size = new FileInfo(path).Length;
                if (size > MaxFileBytes) throw new InvalidDataException("File exceeds limit.");
                total += size; if (total > MaxTotalBytes) throw new InvalidDataException("Installed aggregate hash budget exceeded.");
            }
        }
        public static void AdoptInstalled(string instanceRoot, PreparedPack pack)
        {
            ValidatePack(pack);
            using (Acquire(instanceRoot))
            {
                NoPending(instanceRoot);
                if (File.Exists(Meta(instanceRoot, "state.json"))) throw new InvalidOperationException("Already managed; use sync.");
                CheckHashBudget(instanceRoot, pack.Files.Select(f => f.Path));
                var state = new State { Version = pack.Version, DependencyKey = pack.DependencyKey };
                foreach (var f in pack.Files)
                {
                    var path = Target(instanceRoot, f.Path);
                    if (f.Optional && !File.Exists(path)) continue;
                    if (!Matches(path, f)) throw new InvalidDataException("Installed pack hash mismatch: " + f.Path);
                    state.Files.Add(f.Path, Hash(path));
                }
                WriteState(instanceRoot, state);
            }
        }
        public static SyncPlan Plan(string instanceRoot, PreparedPack pack)
        {
            ValidatePack(pack);
            using (Acquire(instanceRoot)) { NoPending(instanceRoot); return BuildPlan(instanceRoot, pack, ReadState(instanceRoot)); }
        }
        static SyncPlan BuildPlan(string root, PreparedPack pack, State state)
        {
            if (!Equal(pack.DependencyKey, state.DependencyKey)) throw new InvalidOperationException("Minecraft/loader dependencies changed; install a new isolated instance.");
            CheckHashBudget(root, state.Files.Keys.Concat(pack.Files.Select(f => f.Path)));
            var plan = new SyncPlan();
            foreach (var f in pack.Files)
            {
                if (f.Optional && !state.Files.ContainsKey(f.Path)) continue;
                if (state.Files.TryGetValue(f.Path, out var old))
                {
                    var path = Target(root, f.Path);
                    if (!File.Exists(path) || !Equal(Hash(path), old)) throw new IOException("Managed file was changed by user: " + f.Path);
                    if (Matches(path, f)) plan.KeepCount++; else plan.ReplaceCount++;
                }
                else { var path = Target(root, f.Path); if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Unknown existing file conflicts with pack: " + f.Path); plan.AddCount++; }
            }
            var wanted = new HashSet<string>(pack.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var f in state.Files.Where(f => !wanted.Contains(f.Key)))
            {
                var path = Target(root, f.Key);
                if (!File.Exists(path) || !Equal(Hash(path), f.Value)) throw new IOException("Removed managed file was changed by user: " + f.Key);
                plan.RemoveCount++;
            }
            plan.Summary = $"Add {plan.AddCount}, replace {plan.ReplaceCount}, remove {plan.RemoveCount}, keep {plan.KeepCount}";
            return plan;
        }
    }
}
