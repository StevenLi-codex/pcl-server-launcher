using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PclServerPack
{
    public static partial class PackService
    {
        sealed class Change
        {
            public string Path { get; set; }
            public string Before { get; set; }
            public string After { get; set; }
            public string Backup { get; set; }
            public string Stage { get; set; }
        }
        sealed class Transaction
        {
            public string Id { get; set; }
            public State Before { get; set; }
            public State After { get; set; }
            public List<Change> Changes { get; set; } = new List<Change>();
        }
        static void NoPending(string root)
        {
            CheckTree(Meta(root, "pending.json"));
            if (File.Exists(Meta(root, "pending.json"))) throw new IOException("Interrupted sync requires RollbackLast before continuing.");
        }
        public static async Task ApplyAsync(string instanceRoot, PreparedPack pack, SyncPlan plan, CancellationToken cancellationToken, Action<string> progress)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            ValidatePack(pack); cancellationToken.ThrowIfCancellationRequested();
            using (Acquire(instanceRoot))
            {
                NoPending(instanceRoot);
                var state = ReadState(instanceRoot);
                var current = BuildPlan(instanceRoot, pack, state);
                if (current.AddCount != plan.AddCount || current.ReplaceCount != plan.ReplaceCount || current.RemoveCount != plan.RemoveCount || current.KeepCount != plan.KeepCount) throw new InvalidOperationException("Sync plan is stale.");
                if (current.AddCount + current.ReplaceCount + current.RemoveCount == 0 && string.Equals(state.Version, pack.Version, StringComparison.Ordinal)) return;
                var tx = new Transaction { Id = Guid.NewGuid().ToString("N"), Before = state, After = new State { Version = pack.Version, DependencyKey = pack.DependencyKey } };
                var txdir = Meta(instanceRoot, "tx-" + tx.Id); CheckTree(txdir); Directory.CreateDirectory(txdir);
                var scratchFiles = new List<string>(); bool journaled = false; long staged = 0;
                try
                {
                    foreach (var f in pack.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (f.Optional && !state.Files.ContainsKey(f.Path)) continue;
                        var path = Target(instanceRoot, f.Path);
                        if (state.Files.TryGetValue(f.Path, out var old) && Matches(path, f)) { tx.After.Files.Add(f.Path, old); continue; }
                        var stage = Path.Combine(txdir, "stage-" + tx.Changes.Count); scratchFiles.Add(stage);
                        progress?.Invoke("Stage " + f.Path);
                        if (f.Content != null) File.WriteAllBytes(stage, f.Content);
                        else
                        {
                            if (f.Downloads == null || f.Downloads.Count == 0 || f.Downloads.Count > 8) throw new InvalidDataException("No download source: " + f.Path);
                            // Fail closed on an unsuccessful source instead of hiding a hash error with fallback data.
                            await Download(f.Downloads[0], stage, MaxFileBytes, cancellationToken).ConfigureAwait(false);
                        }
                        if (!Matches(stage, f)) throw new InvalidDataException("Downloaded hash mismatch: " + f.Path);
                        staged += new FileInfo(stage).Length; if (staged > MaxTotalBytes) throw new InvalidDataException("Sync exceeds total byte limit.");
                        var hash = Hash(stage); tx.After.Files.Add(f.Path, hash);
                        tx.Changes.Add(new Change { Path = f.Path, Before = old, After = hash, Stage = Path.GetFileName(stage) });
                    }
                    var wanted = new HashSet<string>(pack.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                    foreach (var file in state.Files.Where(f => !wanted.Contains(f.Key))) tx.Changes.Add(new Change { Path = file.Key, Before = file.Value });
                    BuildPlan(instanceRoot, pack, state); // Revalidate all paths and expected bytes after network I/O.
                    for (var i = 0; i < tx.Changes.Count; i++)
                    {
                        var change = tx.Changes[i];
                        if (change.Before == null) continue;
                        change.Backup = "before-" + i;
                        var backup = Path.Combine(txdir, change.Backup); scratchFiles.Add(backup);
                        File.Copy(Target(instanceRoot, change.Path), backup, false);
                        if (!Equal(Hash(backup), change.Before)) throw new IOException("File changed while backing up: " + change.Path);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.WriteAllText(Path.Combine(txdir, "transaction.json"), JsonConvert.SerializeObject(tx, Formatting.Indented));
                    CheckTree(Meta(instanceRoot, "pending.json"));
                    File.WriteAllText(Meta(instanceRoot, "pending.json"), tx.Id); journaled = true;
                    // Per-file replace/move, NOT a filesystem-wide atomic transaction. Persisted intent enables fail-closed process-crash recovery.
                    foreach (var change in tx.Changes)
                    {
                        var path = Target(instanceRoot, change.Path);
                        Expect(path, change.Before);
                        if (change.After == null) File.Delete(path);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)); CheckTree(path);
                            var stage = Path.Combine(txdir, change.Stage); CheckTree(stage);
                            if (change.Before == null) File.Move(stage, path); else File.Replace(stage, path, null);
                        }
                    }
                    WriteState(instanceRoot, tx.After);
                    WritePointer(instanceRoot, "last.json", tx.Id);
                    File.Delete(Meta(instanceRoot, "pending.json"));
                }
                catch
                {
                    // Leave journal/backups intact on commit errors; explicit rollback validates every target before touching it.
                    throw;
                }
                finally
                {
                    if (!journaled)
                    {
                        foreach (var path in scratchFiles) { CheckTree(path); if (File.Exists(path)) File.Delete(path); }
                        var transactionFile = Path.Combine(txdir, "transaction.json"); if (File.Exists(transactionFile)) File.Delete(transactionFile);
                        if (Directory.Exists(txdir)) Directory.Delete(txdir, false);
                    }
                }
            }
        }
        static void Expect(string path, string expected)
        {
            CheckTree(path);
            if (expected == null) { if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Unknown file blocks transaction: " + path); }
            else if (!File.Exists(path) || !Equal(Hash(path), expected)) throw new IOException("File changed during transaction: " + path);
        }
        static void WritePointer(string root, string name, string id)
        {
            var path = Meta(root, name); CheckTree(path); var tmp = Meta(root, Guid.NewGuid().ToString("N") + ".tmp");
            try { File.WriteAllText(tmp, id); if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
