using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PclServerPack
{
    public static partial class PackService
    {
        public static void RollbackLast(string instanceRoot)
        {
            using (Acquire(instanceRoot))
            {
                var pending = Meta(instanceRoot, "pending.json"); var last = Meta(instanceRoot, "last.json"); CheckTree(pending); CheckTree(last);
                var interrupted = File.Exists(pending); var pointer = interrupted ? pending : last;
                if (!File.Exists(pointer)) throw new InvalidOperationException("No rollback available.");
                var id = File.ReadAllText(pointer);
                if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid transaction pointer.");
                var dir = Meta(instanceRoot, "tx-" + id); var journal = Path.Combine(dir, "transaction.json"); CheckTree(journal);
                if (new FileInfo(journal).Length > 16L * 1024 * 1024) throw new InvalidDataException("Oversized journal.");
                var tx = JsonConvert.DeserializeObject<Transaction>(File.ReadAllText(journal));
                if (tx == null || tx.Id != id || tx.Before == null || tx.After == null || tx.Changes == null || tx.Changes.Count > MaxFiles) throw new InvalidDataException("Invalid journal.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var change in tx.Changes)
                {
                    ValidatePath(change.Path); if (!seen.Add(change.Path)) throw new InvalidDataException("Duplicate journal path.");
                    if (change.Before != null) RequireHash(change.Before, 128);
                    if (change.After != null) RequireHash(change.After, 128);
                    if (change.Before != null)
                    {
                        if (change.Backup == null || !change.Backup.StartsWith("before-", StringComparison.Ordinal) || change.Backup.Skip(7).Any(c => c < '0' || c > '9')) throw new InvalidDataException("Unsafe backup name.");
                        var backup = Path.Combine(dir, change.Backup); CheckTree(backup);
                        if (!File.Exists(backup) || !Equal(Hash(backup), change.Before)) throw new IOException("Backup bytes do not match journal.");
                    }
                    var path = Target(instanceRoot, change.Path);
                    var actual = File.Exists(path) ? Hash(path) : null;
                    // Interrupted recovery accepts pre-commit or post-commit bytes only, never an unknown user edit.
                    if (!Equal(actual, change.After) && !(interrupted && Equal(actual, change.Before))) throw new IOException("Rollback conflicts with user changes: " + change.Path);
                    if (Directory.Exists(path)) throw new IOException("Directory blocks rollback.");
                }
                // Persist intent before rollback too: recovery is idempotent if the process exits halfway through.
                if (!interrupted) File.WriteAllText(pending, id);
                foreach (var change in tx.Changes)
                {
                    var path = Target(instanceRoot, change.Path); var actual = File.Exists(path) ? Hash(path) : null;
                    if (Equal(actual, change.Before)) continue;
                    if (!Equal(actual, change.After)) throw new IOException("File changed during rollback.");
                    if (change.Before == null) File.Delete(path);
                    else
                    {
                        var copy = Path.Combine(dir, "restore-" + Guid.NewGuid().ToString("N")); CheckTree(copy);
                        try
                        {
                            File.Copy(Path.Combine(dir, change.Backup), copy, false);
                            Directory.CreateDirectory(Path.GetDirectoryName(path)); CheckTree(path);
                            if (File.Exists(path)) File.Replace(copy, path, null); else File.Move(copy, path);
                        }
                        finally { if (File.Exists(copy)) File.Delete(copy); }
                    }
                }
                WriteState(instanceRoot, tx.Before);
                if (File.Exists(last) && File.ReadAllText(last) == id) File.Delete(last);
                File.Delete(pending);
                // Backups are retained as evidence. Never recursively delete a directory containing user data.
            }
        }
    }
}
