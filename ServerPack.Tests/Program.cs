using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
using PclServerPack;
using System.Threading.Tasks;

class Program
{
    static int passed;
    static async Task Main()
    {
        await Test("public API contract and unmanaged detection", () => {
            var a = Assembly.Load("ServerPack.Core");
            var service = a.GetType("PclServerPack.PackService");
            Check(service != null, "PackService is missing");
            foreach (var name in new[] { "PrepareAsync", "Plan", "ApplyAsync", "AdoptInstalled", "IsManaged", "ReadDependencyKey", "RollbackLast" })
                Check(service.GetMethods().Any(m => m.Name == name), "Missing API: " + name);
            foreach (var pair in new[] { (typeof(ServerDefinition), new[] { "Id", "Name", "Address", "Version", "PackUrl", "PackSha256" }), (typeof(PreparedPack), new[] { "ArchivePath", "Version", "MinecraftVersion", "LoaderDescription", "DependencyKey" }), (typeof(PackFile), new[] { "Path", "Sha1", "Sha512" }) })
                foreach (var property in pair.Item2) { var p = pair.Item1.GetProperty(property); Check(p?.PropertyType == typeof(string) && p.CanRead && p.CanWrite, "Exact string property contract: " + property); }
            Check(typeof(PreparedPack).GetProperty("Files").PropertyType == typeof(IList<PackFile>) && typeof(PackFile).GetProperty("Optional").PropertyType == typeof(bool), "Exact collection/bool model contract");
            foreach (var property in new[] { "AddCount", "ReplaceCount", "RemoveCount", "KeepCount" }) Check(typeof(SyncPlan).GetProperty(property).PropertyType == typeof(int), "Exact count property contract");
            Check(typeof(SyncPlan).GetProperty("Summary").PropertyType == typeof(string), "Exact summary contract");
            Check(service.GetMethod("PrepareAsync", new[] { typeof(ServerDefinition), typeof(string), typeof(CancellationToken) })?.ReturnType == typeof(Task<PreparedPack>), "Exact PrepareAsync signature");
            Check(service.GetMethod("ApplyAsync", new[] { typeof(string), typeof(PreparedPack), typeof(SyncPlan), typeof(CancellationToken), typeof(Action<string>) })?.ReturnType == typeof(Task), "Exact ApplyAsync signature");
            Check(service.GetMethod("Plan", new[] { typeof(string), typeof(PreparedPack) })?.ReturnType == typeof(SyncPlan), "Exact Plan signature");
            Check(service.GetMethod("AdoptInstalled", new[] { typeof(string), typeof(PreparedPack) })?.ReturnType == typeof(void), "Exact AdoptInstalled signature");
            Check(service.GetMethod("IsManaged", new[] { typeof(string) })?.ReturnType == typeof(bool) && service.GetMethod("ReadDependencyKey", new[] { typeof(string) })?.ReturnType == typeof(string) && service.GetMethod("RollbackLast", new[] { typeof(string) })?.ReturnType == typeof(void), "Exact instance operation signatures");
            Check(!(bool)service.GetMethod("IsManaged").Invoke(null, new object[] { Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }), "Fresh path must not be managed");
            return Task.CompletedTask;
        });
        await Test("adopt verifies required hashes and tracks installed optionals only", () => {
            using var dir = new TempDir();
            Put(dir.Path, "mods/a.jar", "synthetic a");
            Put(dir.Path, "mods/optional.jar", "synthetic optional");
            Put(dir.Path, "saves/personal.txt", "personal");
            var pack = Pack(F("mods/a.jar", "synthetic a"), F("mods/optional.jar", "synthetic optional", true), F("mods/absent.jar", "absent", true));
            PackService.AdoptInstalled(dir.Path, pack);
            Check(PackService.IsManaged(dir.Path), "adopt did not write state");
            Check(PackService.ReadDependencyKey(dir.Path) == pack.DependencyKey, "dependencies not stored");
            var plan = PackService.Plan(dir.Path, pack);
            Check(plan.KeepCount == 2 && plan.AddCount == 0, "optional absent must remain absent");
            Check(File.ReadAllText(System.IO.Path.Combine(dir.Path, "saves/personal.txt")) == "personal", "personal changed");
            using var bad = new TempDir();
            Put(bad.Path, "mods/a.jar", "bad");
            Throws(() => PackService.AdoptInstalled(bad.Path, pack));
            Check(!PackService.IsManaged(bad.Path), "failed adopt wrote state");
            return Task.CompletedTask;
        });
        await Test("prepare pins archive and parses client dependencies plus first-party overrides", async () => {
            using var dir = new TempDir();
            var bytes = Archive(new[] { F("mods/a.jar", "a"), F("mods/optional.jar", "b", true) }, new Dictionary<string,string> { ["overrides/config/a.json"] = "{}", ["overrides/README.txt"] = "Synthetic readme", ["overrides/CREDITS.json"] = "{}", ["overrides/servers.dat"] = "synthetic personal" });
            using var transport = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes });
            // The transport hook is scoped and never disables production HTTPS policy.
            var method = typeof(PackService).GetMethod("UseHttpTransport");
            Check(method != null, "Injectable transport missing");
            using var scope = (IDisposable)method.Invoke(null, new object[] { transport });
            var p = await PackService.PrepareAsync(Server(bytes), dir.Path, CancellationToken.None);
            Check(p.Files.Count == 5 && p.Files.Any(f => f.Optional), "files/overrides not parsed");
            Check(!p.Files.Any(f => f.Path == "servers.dat"), "servers.dat must never be managed");
            Check(p.MinecraftVersion == "1.21.1" && p.DependencyKey.Contains("fabric-loader=0.16.0"), "dependencies missing");
            Check(File.Exists(p.ArchivePath), "verified archive not cached");
            var bad = Server(bytes); bad.PackSha256 = new string('0', 64);
            await ThrowsAsync(() => PackService.PrepareAsync(bad, dir.Path, CancellationToken.None));
        });
        await Test("delta stages add replace remove and identical sync skips network", async () => {
            using var dir = new TempDir();
            Put(dir.Path, "mods/a.jar", "old-a"); Put(dir.Path, "mods/stale.jar", "stale"); Put(dir.Path, "mods/keep.jar", "keep");
            Put(dir.Path, "config/personal.json", "personal-config"); Put(dir.Path, "saves/world.dat", "world"); Put(dir.Path, "servers.dat", "my servers");
            PackService.AdoptInstalled(dir.Path, Pack(F("mods/a.jar", "old-a"), F("mods/stale.jar", "stale"), F("mods/keep.jar", "keep")));
            var next = Pack(F("mods/a.jar", "new-a"), F("mods/new.jar", "new"), F("mods/keep.jar", "keep"));
            var plan = PackService.Plan(dir.Path, next);
            Check(plan.AddCount == 1 && plan.ReplaceCount == 1 && plan.RemoveCount == 1 && plan.KeepCount == 1, "wrong delta");
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/mods/a.jar"] = Encoding.UTF8.GetBytes("new-a"), ["https://synthetic.invalid/mods/new.jar"] = Encoding.UTF8.GetBytes("new") });
            using var scope = PackService.UseHttpTransport(fixture);
            await PackService.ApplyAsync(dir.Path, next, plan, CancellationToken.None, null);
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "new-a" && !File.Exists(Path.Combine(dir.Path, "mods/stale.jar")), "delta not committed");
            Check(File.ReadAllText(Path.Combine(dir.Path, "config/personal.json")) == "personal-config" && File.ReadAllText(Path.Combine(dir.Path, "saves/world.dat")) == "world" && File.ReadAllText(Path.Combine(dir.Path, "servers.dat")) == "my servers", "personal files touched");
            Check(fixture.Calls == 2, "unchanged files downloaded");
            await PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null);
            Check(fixture.Calls == 2, "identical sync fetched files");
        });
        await Test("rollback restores old managed bytes and preserves unknown user files", async () => {
            using var dir = new TempDir();
            Put(dir.Path, "mods/a.jar", "old"); Put(dir.Path, "mods/remove.jar", "remove");
            var old = Pack(F("mods/a.jar", "old"), F("mods/remove.jar", "remove")); PackService.AdoptInstalled(dir.Path, old);
            var next = Pack(F("mods/a.jar", "new"), F("mods/add.jar", "add"));
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/mods/a.jar"] = Encoding.UTF8.GetBytes("new"), ["https://synthetic.invalid/mods/add.jar"] = Encoding.UTF8.GetBytes("add") });
            using var scope = PackService.UseHttpTransport(fixture);
            await PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null);
            Put(dir.Path, "mods/user.jar", "user");
            PackService.RollbackLast(dir.Path);
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "old" && File.ReadAllText(Path.Combine(dir.Path, "mods/remove.jar")) == "remove", "old bytes not restored");
            Check(!File.Exists(Path.Combine(dir.Path, "mods/add.jar")) && File.ReadAllText(Path.Combine(dir.Path, "mods/user.jar")) == "user", "rollback touched unknown file");
            Check(PackService.Plan(dir.Path, old).KeepCount == 2, "state not restored");
        });
        await Test("hash mismatch and cancellation leave managed bytes and state unchanged", async () => {
            using var dir = new TempDir(); Put(dir.Path, "mods/a.jar", "old");
            var old = Pack(F("mods/a.jar", "old")); PackService.AdoptInstalled(dir.Path, old);
            var next = Pack(F("mods/a.jar", "new"), F("mods/b.jar", "b"));
            var statePath = Path.Combine(dir.Path, ".pcl-server-pack/state.json"); var before = File.ReadAllText(statePath);
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/mods/a.jar"] = Encoding.UTF8.GetBytes("new"), ["https://synthetic.invalid/mods/b.jar"] = Encoding.UTF8.GetBytes("bad") }); using var scope = PackService.UseHttpTransport(fixture);
            await ThrowsAsync(() => PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null));
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "old" && File.ReadAllText(statePath) == before, "hash failure mutated state/files");
            using var cts = new CancellationTokenSource();
            await ThrowsAsync(() => PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), cts.Token, message => cts.Cancel()));
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "old" && File.ReadAllText(statePath) == before, "cancel mutated files");
            Check(!Directory.EnumerateDirectories(Path.Combine(dir.Path, ".pcl-server-pack"), "tx-*").Any(), "cancel/error left staging");
        });
        await Test("unknown and user-edited managed files fail before mutation", () => {
            using var dir = new TempDir(); Put(dir.Path, "mods/a.jar", "old"); var old = Pack(F("mods/a.jar", "old")); PackService.AdoptInstalled(dir.Path, old);
            Put(dir.Path, "mods/unknown.jar", "mine"); Throws(() => PackService.Plan(dir.Path, Pack(F("mods/a.jar", "old"), F("mods/unknown.jar", "new"))));
            Put(dir.Path, "mods/a.jar", "user-edit"); Throws(() => PackService.Plan(dir.Path, old)); Throws(() => PackService.Plan(dir.Path, Pack()));
            using var unmanaged = new TempDir(); Throws(() => PackService.Plan(unmanaged.Path, old));
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "user-edit", "conflict touched user bytes"); return Task.CompletedTask;
        });
        await Test("path attacks case duplicates and protected user files are rejected", async () => {
            foreach (var path in new[] { "../escape", "/mods/a.jar", "mods/../evil.jar", "mods\\evil.jar", "mods/AUX.txt", "mods/com1.jar", "mods/lpt¹.jar", "mods/a.jar:evil", "mods/name. ", "mods//a.jar", "saves/world.dat", "options.txt", "accounts.json", "servers.dat", ".pcl-server-pack/state.json" }) {
                using var dir = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack()); Throws(() => PackService.Plan(dir.Path, Pack(F(path, "x"))));
            }
            using var cache = new TempDir(); var bytes = Archive(new[] { F("mods/A.jar", "a"), F("mods/a.jar", "a") });
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes }); using var scope = PackService.UseHttpTransport(fixture);
            await ThrowsAsync(() => PackService.PrepareAsync(Server(bytes), cache.Path, CancellationToken.None));
            Check(!Directory.EnumerateFiles(cache.Path).Any(), "rejected archive left files");
        });
        await Test("state-directory symlink cannot report managed or read state", () => {
            using var dir = new TempDir(); using var outside = new TempDir(); Put(outside.Path, "state.json", "{}");
            Directory.CreateSymbolicLink(Path.Combine(dir.Path, ".pcl-server-pack"), outside.Path);
            Check(!PackService.IsManaged(dir.Path), "symlink metadata was reported trusted");
            Throws(() => PackService.ReadDependencyKey(dir.Path)); return Task.CompletedTask;
        });
        await Test("Windows short-name and reserved-space aliases are rejected", () => {
            foreach (var path in new[] { "mods/THING~1.jar", "mods/CON .jar", "mods/LPT1 .txt", "mods/a\u202Ejar" }) { using var dir = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack()); Throws(() => PackService.Plan(dir.Path, Pack(F(path, "x")))); }
            return Task.CompletedTask;
        });
        await Test("pending crash journal blocks plan until verified recovery", async () => {
            using var dir = new TempDir(); Put(dir.Path, "mods/a.jar", "old"); var old = Pack(F("mods/a.jar", "old")); PackService.AdoptInstalled(dir.Path, old);
            var next = Pack(F("mods/a.jar", "new"));
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/mods/a.jar"] = Encoding.UTF8.GetBytes("new") }); using var scope = PackService.UseHttpTransport(fixture);
            await PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null);
            var meta = Path.Combine(dir.Path, ".pcl-server-pack");
            File.Copy(Path.Combine(meta, "last.json"), Path.Combine(meta, "pending.json")); // synthetic crash marker after commit, before clearing intent
            Throws(() => PackService.Plan(dir.Path, next));
            PackService.RollbackLast(dir.Path);
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "old", "recovery failed");
            Check(PackService.Plan(dir.Path, old).KeepCount == 1, "recovered state mismatch");
        });
        await Test("existing case alias blocks unknown additions on every platform", () => {
            using var dir = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack()); Put(dir.Path, "Mods/USER.jar", "personal");
            Throws(() => PackService.Plan(dir.Path, Pack(F("mods/user.jar", "new"))));
            Check(File.ReadAllText(Path.Combine(dir.Path, "Mods/USER.jar")) == "personal", "case alias changed"); return Task.CompletedTask;
        });
        await Test("rollback refuses edited updated files without partial restoration", async () => {
            using var dir = new TempDir(); Put(dir.Path, "mods/a.jar", "old-a"); Put(dir.Path, "config/b.json", "old-b");
            PackService.AdoptInstalled(dir.Path, Pack(F("mods/a.jar", "old-a"), F("config/b.json", "old-b")));
            var next = Pack(F("mods/a.jar", "new-a"), F("config/b.json", "new-b"));
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/mods/a.jar"] = Encoding.UTF8.GetBytes("new-a"), ["https://synthetic.invalid/config/b.json"] = Encoding.UTF8.GetBytes("new-b") }); using var scope = PackService.UseHttpTransport(fixture);
            await PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null);
            Put(dir.Path, "config/b.json", "my edit"); Throws(() => PackService.RollbackLast(dir.Path));
            Check(File.ReadAllText(Path.Combine(dir.Path, "mods/a.jar")) == "new-a" && File.ReadAllText(Path.Combine(dir.Path, "config/b.json")) == "my edit", "rollback partially changed files before conflict");
        });
        await Test("loader changes require a separate installation", () => {
            using var dir = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack()); var next = Pack(); next.DependencyKey = "forge=47.0;minecraft=1.20.1";
            Throws(() => PackService.Plan(dir.Path, next)); return Task.CompletedTask;
        });
        await Test("HTTPS redirect downgrade invalid pin and archive limit fail closed", async () => {
            using var dir = new TempDir(); var bytes = Archive(Array.Empty<PackFile>());
            using var fixture = new Fixture(new Dictionary<string,byte[]>()); using var scope = PackService.UseHttpTransport(fixture);
            fixture.Respond = request => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://synthetic.invalid/cleartext") }, RequestMessage = request };
            await ThrowsAsync(() => PackService.PrepareAsync(Server(bytes), dir.Path, CancellationToken.None)); Check(fixture.Calls == 1, "followed cleartext redirect");
            fixture.Respond = request => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request }; r.Content.Headers.ContentLength = 64L * 1024 * 1024 + 1; return r; };
            await ThrowsAsync(() => PackService.PrepareAsync(Server(bytes), dir.Path, CancellationToken.None));
            var bad = Server(bytes); bad.PackSha256 = null; var calls = fixture.Calls; await ThrowsAsync(() => PackService.PrepareAsync(bad, dir.Path, CancellationToken.None)); Check(fixture.Calls == calls, "missing pin made HTTP request");
            bad = Server(bytes); bad.PackUrl = "http://synthetic.invalid/pack"; await ThrowsAsync(() => PackService.PrepareAsync(bad, dir.Path, CancellationToken.None));
            Check(!Directory.EnumerateFiles(dir.Path).Any(), "failed prepare left downloads");
        });
        await Test("per-instance lock blocks concurrent mutations", () => {
            using var dir = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack());
            using var held = new FileStream(Path.Combine(dir.Path, ".pcl-server-pack/lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Throws(() => PackService.Plan(dir.Path, Pack())); return Task.CompletedTask;
        });
        await Test("metadata and content-path symlinks cannot escape instance", () => {
            using var dir = new TempDir(); using var outside = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack());
            Directory.CreateSymbolicLink(Path.Combine(dir.Path, "mods"), outside.Path);
            Throws(() => PackService.Plan(dir.Path, Pack(F("mods/x.jar", "x"))));
            Check(!Directory.EnumerateFiles(outside.Path).Any(), "outside instance changed"); return Task.CompletedTask;
        });
        await Test("first-party config edits survive update conflict", async () => {
            using var dir = new TempDir(); using var cache = new TempDir();
            var bytes = Archive(Array.Empty<PackFile>(), new Dictionary<string,string> { ["overrides/config/a.json"] = "original" });
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes }); using var scope = PackService.UseHttpTransport(fixture);
            var pack = await PackService.PrepareAsync(Server(bytes), cache.Path, CancellationToken.None); Put(dir.Path, "config/a.json", "original"); PackService.AdoptInstalled(dir.Path, pack);
            Put(dir.Path, "config/a.json", "personal-edit"); Throws(() => PackService.Plan(dir.Path, pack)); Check(File.ReadAllText(Path.Combine(dir.Path, "config/a.json")) == "personal-edit", "override changed edited config");
        });
        await Test("archive override traversal is rejected before extraction", async () => {
            using var dir = new TempDir(); var bytes = Archive(Array.Empty<PackFile>(), new Dictionary<string,string> { ["overrides/../escape.txt"] = "bad" });
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes }); using var scope = PackService.UseHttpTransport(fixture);
            await ThrowsAsync(() => PackService.PrepareAsync(Server(bytes), dir.Path, CancellationToken.None));
            Check(!Directory.EnumerateFileSystemEntries(dir.Path).Any(), "bad archive extracted data");
        });
        await Test("host-specific headers and HTTPS redirects are explicit", async () => {
            using var dir = new TempDir(); var bytes = Archive(Array.Empty<PackFile>()); var server = Server(bytes); server.PackUrl = "https://mc-download.220719.uk/synthetic.mrpack";
            using var fixture = new Fixture(new Dictionary<string,byte[]>()); using var scope = PackService.UseHttpTransport(fixture);
            fixture.Respond = request => {
                if (request.RequestUri.Host == "mc-download.220719.uk") { Check(request.Headers.UserAgent.ToString().Contains("Mozilla/5.0") && request.Headers.Referrer.AbsoluteUri == "https://mc-download.220719.uk/", "site headers missing"); return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://synthetic.invalid/final") }, RequestMessage = request }; }
                Check(request.Headers.Referrer == null && request.Headers.UserAgent.ToString() == "PclServerPack/1.0", "headers leaked across hosts");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
            };
            var p = await PackService.PrepareAsync(server, dir.Path, CancellationToken.None); Check(fixture.Calls == 2 && File.Exists(p.ArchivePath), "HTTPS redirect failed");
        });
        await Test("adopt refuses interrupted transaction even when state is missing", () => {
            using var dir = new TempDir(); Put(dir.Path, ".pcl-server-pack/pending.json", new string('a', 32));
            Throws(() => PackService.AdoptInstalled(dir.Path, Pack()));
            Check(!File.Exists(Path.Combine(dir.Path, ".pcl-server-pack/state.json")), "adopt overwrote pending recovery"); return Task.CompletedTask;
        });
        await Test("unsafe ZIP directory aliases are rejected even without file content", async () => {
            using var dir = new TempDir(); var bytes = Archive(Array.Empty<PackFile>(), new Dictionary<string,string> { ["overrides/config/CON/"] = "" });
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes }); using var scope = PackService.UseHttpTransport(fixture);
            await ThrowsAsync(() => PackService.PrepareAsync(Server(bytes), dir.Path, CancellationToken.None));
        });
        await Test("managed-state case aliases are rejected without creating shadow state", () => {
            using var dir = new TempDir(); Put(dir.Path, ".PCL-SERVER-PACK/personal.txt", "mine");
            Throws(() => PackService.AdoptInstalled(dir.Path, Pack()));
            Check(!File.Exists(Path.Combine(dir.Path, ".pcl-server-pack/state.json")), "created shadow managed directory"); return Task.CompletedTask;
        });
        await Test("unmanaged detection is read-only", () => {
            using var dir = new TempDir(); var fresh = Path.Combine(dir.Path, "missing");
            Check(!PackService.IsManaged(fresh) && !Directory.Exists(fresh), "IsManaged created an instance directory");
            Check(!PackService.IsManaged(dir.Path) && !Directory.EnumerateFileSystemEntries(dir.Path).Any(), "IsManaged wrote metadata for unmanaged root"); return Task.CompletedTask;
        });
        await Test("aggregate hash budget rejects oversized installed set before scanning bytes", () => {
            using var dir = new TempDir();
            var files = Enumerable.Range(0, 5).Select(i => F("mods/sparse" + i + ".jar", "synthetic")).ToArray();
            foreach (var f in files) { var path = Path.Combine(dir.Path, f.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)); using var stream = File.Create(path); stream.SetLength(512L * 1024 * 1024); }
            // Sparse files are real, but their aggregate size must be rejected before any hash work.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { PackService.AdoptInstalled(dir.Path, Pack(files)); throw new Exception("Oversized set accepted"); }
            catch (InvalidDataException e) { Check(e.Message.Contains("aggregate"), "Expected aggregate size rejection, got: " + e.Message); }
            Check(watch.Elapsed < TimeSpan.FromSeconds(2), "oversized set hashed before budget validation");
            Check(!PackService.IsManaged(dir.Path), "oversized adopt wrote state"); return Task.CompletedTask;
        });
        await Test("content-identical version change updates state and remains rollbackable", async () => {
            using var dir = new TempDir(); Put(dir.Path, "mods/a.jar", "a"); var old = Pack(F("mods/a.jar", "a")); PackService.AdoptInstalled(dir.Path, old);
            var next = Pack(F("mods/a.jar", "a")); next.Version = "synthetic-2";
            await PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null);
            var state = Path.Combine(dir.Path, ".pcl-server-pack/state.json"); Check((string)JObject.Parse(File.ReadAllText(state))["Version"] == "synthetic-2", "state version stayed stale");
            PackService.RollbackLast(dir.Path); Check((string)JObject.Parse(File.ReadAllText(state))["Version"] == old.Version, "state-only rollback failed");
        });
        await Test("existing file in parent directory blocks plan before downloads", () => {
            using var dir = new TempDir(); PackService.AdoptInstalled(dir.Path, Pack()); Put(dir.Path, "config", "user-file-not-directory");
            Throws(() => PackService.Plan(dir.Path, Pack(F("config/a.json", "new"))));
            Check(File.ReadAllText(Path.Combine(dir.Path, "config")) == "user-file-not-directory", "parent file changed"); return Task.CompletedTask;
        });
        await Test("client-unsupported entries skip while client-overrides remain first-party", async () => {
            using var dir = new TempDir(); var bytes = Archive(new[] { F("mods/server-only.jar", "x"), F("mods/client.jar", "client") }, new Dictionary<string,string> { ["client-overrides/config/client.json"] = "{}" }, f => f.Path.Contains("server-only") ? "unsupported" : "required");
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes }); using var scope = PackService.UseHttpTransport(fixture);
            var p = await PackService.PrepareAsync(Server(bytes), dir.Path, CancellationToken.None);
            Check(p.Files.Count == 2 && p.Files.Any(f => f.Path == "config/client.json") && !p.Files.Any(f => f.Path.Contains("server-only")), "wrong client files");
        });
        await Test("interrupted partially committed delta recovers both before and after bytes", async () => {
            using var dir = new TempDir(); Put(dir.Path, "mods/a.jar", "old-a"); Put(dir.Path, "mods/b.jar", "old-b"); var old = Pack(F("mods/a.jar", "old-a"), F("mods/b.jar", "old-b")); PackService.AdoptInstalled(dir.Path, old);
            var next = Pack(F("mods/a.jar", "new-a"), F("mods/b.jar", "new-b"), F("mods/add.jar", "add"));
            using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/mods/a.jar"] = Encoding.UTF8.GetBytes("new-a"), ["https://synthetic.invalid/mods/b.jar"] = Encoding.UTF8.GetBytes("new-b"), ["https://synthetic.invalid/mods/add.jar"] = Encoding.UTF8.GetBytes("add") }); using var scope = PackService.UseHttpTransport(fixture);
            await PackService.ApplyAsync(dir.Path, next, PackService.Plan(dir.Path, next), CancellationToken.None, null);
            var meta = Path.Combine(dir.Path, ".pcl-server-pack"); File.Copy(Path.Combine(meta, "last.json"), Path.Combine(meta, "pending.json"));
            // Synthetic process-interruption snapshot: first replacement complete; second replacement/addition not yet made.
            Put(dir.Path, "mods/b.jar", "old-b"); File.Delete(Path.Combine(dir.Path, "mods/add.jar")); Put(dir.Path, "mods/user.jar", "user");
            PackService.RollbackLast(dir.Path);
            Check(PackService.Plan(dir.Path, old).KeepCount == 2 && File.ReadAllText(Path.Combine(dir.Path, "mods/user.jar")) == "user", "partial commit recovery incorrect");
        });
        var intake = Environment.GetEnvironmentVariable("PCL_PACK_INTAKE");
        if (!string.IsNullOrEmpty(intake)) foreach (var row in JArray.Parse(File.ReadAllText(Path.Combine(intake, "reports.json")))) {
            await Test("real pinned catalog archive " + (string)row["file"], async () => {
                using var dir = new TempDir(); var bytes = File.ReadAllBytes(Path.Combine(intake, (string)row["file"]));
                using var fixture = new Fixture(new Dictionary<string,byte[]> { ["https://synthetic.invalid/pack"] = bytes }); using var scope = PackService.UseHttpTransport(fixture);
                var server = Server(bytes); server.PackSha256 = (string)row["sha256"]; server.Version = (string)row["versionId"];
                var p = await PackService.PrepareAsync(server, dir.Path, CancellationToken.None);
                Check(p.Files.Any(f => f.Path == "README.txt") && p.Files.Any(f => f.Path == "CREDITS.json") && !p.Files.Any(f => f.Path == "servers.dat"), "real overrides wrong");
                Console.WriteLine("  actual archive " + server.Version + " " + p.DependencyKey + " managed entries=" + p.Files.Count);
            });
        }
        var liveCatalog = Environment.GetEnvironmentVariable("PCL_LIVE_CATALOG");
        if (!string.IsNullOrEmpty(liveCatalog)) foreach (var row in (JArray)JObject.Parse(File.ReadAllText(liveCatalog))["servers"]) {
            await Test("live HTTPS pinned catalog " + (string)row["Id"], async () => {
                using var dir = new TempDir(); var server = row.ToObject<ServerDefinition>();
                var p = await PackService.PrepareAsync(server, dir.Path, CancellationToken.None);
                Check(File.Exists(p.ArchivePath) && p.Version == server.Version, "live archive not prepared");
            });
        }
        Console.WriteLine($"PASS {passed} behavioral tests (synthetic fixtures except explicitly labeled real pinned catalog archives/live HTTPS; real filesystem)");
    }
    static ServerDefinition Server(byte[] bytes) => new ServerDefinition { Id = "synthetic", Version = "synthetic-1", PackUrl = "https://synthetic.invalid/pack", PackSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    static byte[] Archive(IEnumerable<PackFile> files, Dictionary<string,string> entries = null, Func<PackFile,string> client = null)
    {
        using var m = new MemoryStream();
        using (var zip = new ZipArchive(m, ZipArchiveMode.Create, true)) {
            var index = new JObject { ["formatVersion"] = 1, ["game"] = "minecraft", ["versionId"] = "synthetic-1", ["dependencies"] = new JObject { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.0" }, ["files"] = new JArray(files.Select(f => new JObject { ["path"] = f.Path, ["hashes"] = new JObject { ["sha512"] = f.Sha512 }, ["downloads"] = new JArray(f.Downloads), ["env"] = new JObject { ["client"] = client != null ? client(f) : f.Optional ? "optional" : "required", ["server"] = "required" } })) };
            using (var w = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open())) w.Write(index.ToString());
            if (entries != null) foreach (var e in entries) using (var w = new StreamWriter(zip.CreateEntry(e.Key).Open())) w.Write(e.Value);
        }
        return m.ToArray();
    }
    sealed class Fixture : HttpMessageHandler
    {
        public Dictionary<string,byte[]> Data; public int Calls;
        public Func<HttpRequestMessage,HttpResponseMessage> Respond;
        public Fixture(Dictionary<string,byte[]> data) { Data = data; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Respond != null ? Respond(request) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data[request.RequestUri.AbsoluteUri]), RequestMessage = request }); }
    }
    static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch (Exception e) when (!(e is NotImplementedException)) { return; } throw new Exception("Expected safe async rejection"); }
    static PackFile F(string path, string bytes, bool optional = false) => new PackFile { Path = path, Sha512 = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(bytes))).ToLowerInvariant(), Optional = optional, Downloads = new List<string> { "https://synthetic.invalid/" + path } };
    static PreparedPack Pack(params PackFile[] files) => new PreparedPack { Version = "synthetic-1", MinecraftVersion = "1.21.1", LoaderDescription = "fabric-loader 0.16.0", DependencyKey = "fabric-loader=0.16.0;minecraft=1.21.1", Files = files.ToList() };
    static void Put(string root, string path, string text) { var p = System.IO.Path.Combine(root, path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)); File.WriteAllText(p, text, new UTF8Encoding(false)); }
    static void Throws(Action action) { try { action(); } catch (Exception e) when (!(e is NotImplementedException)) { return; } throw new Exception("Expected safe rejection"); }
    sealed class TempDir : IDisposable { public string Path { get; } = System.IO.Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? System.IO.Path.GetTempPath(), "pcl-sync-tests-" + Guid.NewGuid().ToString("N")); public TempDir() { Directory.CreateDirectory(Path); } public void Dispose() { Directory.Delete(Path, true); } }
    static async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
