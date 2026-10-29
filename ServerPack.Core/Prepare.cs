using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PclServerPack
{
    public static partial class PackService
    {
        static readonly AsyncLocal<HttpMessageHandler> Transport = new AsyncLocal<HttpMessageHandler>();
        static readonly HttpMessageHandler DefaultTransport = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        /// <summary>Scoped HTTP fixture transport. Supply a handler that does not auto-redirect; HTTPS policy remains enforced.</summary>
        public static IDisposable UseHttpTransport(HttpMessageHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var previous = Transport.Value; Transport.Value = handler; return new Scope(() => Transport.Value = previous);
        }
        sealed class Scope : IDisposable { Action end; public Scope(Action end) { this.end = end; } public void Dispose() { var action = Interlocked.Exchange(ref end, null); action?.Invoke(); } }
        static Uri Https(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Only absolute HTTPS download URLs without credentials/fragments are allowed.");
            return uri;
        }
        static async Task Download(string url, string destination, long limit, CancellationToken token)
        {
            var uri = Https(url); CheckTree(destination);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var client = new HttpClient(Transport.Value ?? DefaultTransport, false))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                for (var redirects = 0; redirects <= 5; redirects++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                    {
                        request.Headers.UserAgent.ParseAdd(uri.Host.Equals("mc-download.220719.uk", StringComparison.OrdinalIgnoreCase) ? "Mozilla/5.0 PclServerPack/1.0" : "PclServerPack/1.0");
                        if (uri.Host.Equals("mc-download.220719.uk", StringComparison.OrdinalIgnoreCase)) request.Headers.Referrer = new Uri("https://mc-download.220719.uk/");
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        {
                            if (response.RequestMessage?.RequestUri != null && response.RequestMessage.RequestUri != uri) throw new InvalidDataException("Transport auto-redirect is forbidden.");
                            var status = (int)response.StatusCode;
                            if (status >= 300 && status <= 399)
                            {
                                if (response.Headers.Location == null || redirects == 5) throw new HttpRequestException("Invalid/too many redirects.");
                                uri = Https(new Uri(uri, response.Headers.Location).AbsoluteUri); continue;
                            }
                            response.EnsureSuccessStatusCode();
                            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Download exceeds size limit.");
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                            {
                                var buffer = new byte[81920]; long total = 0; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                                {
                                    total += count; if (total > limit) throw new InvalidDataException("Download exceeds size limit.");
                                    await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                }
                            }
                            return;
                        }
                    }
                }
            }
        }
        public static async Task<PreparedPack> PrepareAsync(ServerDefinition server, string cacheDirectory, CancellationToken cancellationToken)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            RequireHash(server.PackSha256, 64); Https(server.PackUrl); cancellationToken.ThrowIfCancellationRequested();
            CheckTree(cacheDirectory); Directory.CreateDirectory(cacheDirectory);
            var final = Path.Combine(Path.GetFullPath(cacheDirectory), server.PackSha256.ToLowerInvariant() + ".mrpack"); CheckTree(final);
            var temp = Path.Combine(Path.GetFullPath(cacheDirectory), Guid.NewGuid().ToString("N") + ".download");
            try
            {
                await Download(server.PackUrl, temp, 64L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                using (var s = File.OpenRead(temp)) using (var h = SHA256.Create()) if (!Equal(Hex(h.ComputeHash(s)), server.PackSha256)) throw new InvalidDataException("Archive SHA256 does not match catalog pin.");
                var pack = ParseArchive(temp, server, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(final))
                {
                    using (var s = File.OpenRead(final)) using (var h = SHA256.Create()) if (s.Length > 64L * 1024 * 1024 || !Equal(Hex(h.ComputeHash(s)), server.PackSha256)) throw new IOException("Existing cached archive is not trusted.");
                }
                else File.Move(temp, final);
                pack.ArchivePath = final;
                return pack;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        static PreparedPack ParseArchive(string archive, ServerDefinition server, CancellationToken token)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > MaxFiles + 1) throw new InvalidDataException("Too many ZIP entries.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
                foreach (var e in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = e.FullName;
                    ValidateSegments(name.EndsWith("/", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) : name);
                    if (name.IndexOf('\\') >= 0 || name.StartsWith("/", StringComparison.Ordinal) || name.Split('/').Any(p => p == ".." || p == "." || p.Contains(':')) || !seen.Add(name)) throw new InvalidDataException("Unsafe/duplicate ZIP member.");
                    // No ZIP member is extracted as a filesystem object. When exposed by the runtime, reject link metadata as well.
                    var attributes = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
                    var external = attributes == null ? 0 : (int)attributes.GetValue(e, null);
                    if (((external >> 16) & 0xF000) == 0xA000 || (external & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ZIP link forbidden.");
                    expanded += e.Length;
                    if (e.Length > 16L * 1024 * 1024 || expanded > 128L * 1024 * 1024) throw new InvalidDataException("ZIP expanded size exceeds limit.");
                }
                var indexEntry = zip.Entries.SingleOrDefault(e => e.FullName == "modrinth.index.json");
                if (indexEntry == null || indexEntry.Length > 8L * 1024 * 1024) throw new InvalidDataException("Missing/oversized mrpack index.");
                JObject index;
                using (var reader = new StreamReader(indexEntry.Open())) using (var json = new JsonTextReader(reader) { MaxDepth = 32 })
                    index = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if ((int?)index["formatVersion"] != 1 || (string)index["game"] != "minecraft") throw new InvalidDataException("Unsupported mrpack format.");
                var deps = index["dependencies"] as JObject;
                if (deps == null || string.IsNullOrWhiteSpace((string)deps["minecraft"])) throw new InvalidDataException("Missing dependencies.");
                var allowedDeps = new[] { "minecraft", "fabric-loader", "quilt-loader", "forge", "neoforge" };
                if (deps.Properties().Any(p => !allowedDeps.Contains(p.Name) || p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value) || ((string)p.Value).Any(c => !(char.IsLetterOrDigit(c) || ".-_+".Contains(c)))) || deps.Properties().Count(p => p.Name != "minecraft") > 1) throw new InvalidDataException("Unsupported/ambiguous dependency.");
                var version = (string)index["versionId"];
                if (string.IsNullOrEmpty(version) || (!string.IsNullOrEmpty(server.Version) && server.Version != version)) throw new InvalidDataException("Catalog version does not match pack.");
                var pack = new PreparedPack { Version = version, MinecraftVersion = (string)deps["minecraft"], DependencyKey = string.Join(";", deps.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + (string)p.Value)), LoaderDescription = string.Join(", ", deps.Properties().Where(p => p.Name != "minecraft").Select(p => p.Name + " " + (string)p.Value)) };
                var files = index["files"] as JArray;
                if (files == null || files.Count > MaxFiles) throw new InvalidDataException("Missing/oversized files index.");
                var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in files)
                {
                    token.ThrowIfCancellationRequested();
                    var f = new PackFile { Path = (string)item["path"], Sha1 = (string)item["hashes"]?["sha1"], Sha512 = (string)item["hashes"]?["sha512"] };
                    ValidatePath(f.Path); if (!allPaths.Add(f.Path)) throw new InvalidDataException("Duplicate index path.");
                    if (!string.IsNullOrEmpty(f.Sha1)) RequireHash(f.Sha1, 40);
                    if (!string.IsNullOrEmpty(f.Sha512)) RequireHash(f.Sha512, 128); else RequireHash(f.Sha1, 40);
                    var client = (string)item["env"]?["client"] ?? "required";
                    if (!new[] { "required", "optional", "unsupported" }.Contains(client)) throw new InvalidDataException("Invalid client env.");
                    var urls = item["downloads"] as JArray;
                    if (urls == null || urls.Count == 0 || urls.Count > 8) throw new InvalidDataException("Missing/oversized download list.");
                    foreach (var url in urls) f.Downloads.Add(Https((string)url).AbsoluteUri);
                    if (client == "unsupported") continue;
                    f.Optional = client == "optional"; pack.Files.Add(f);
                }
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.FullName == "modrinth.index.json") continue;
                    var prefix = entry.FullName.StartsWith("client-overrides/", StringComparison.Ordinal) ? "client-overrides/" : entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) ? "overrides/" : null;
                    if (prefix == null) { if (entry.FullName.StartsWith("server-overrides/", StringComparison.Ordinal)) continue; throw new InvalidDataException("Unknown archive member."); }
                    var relative = entry.FullName.Substring(prefix.Length);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                    // Personal server list is installed only by the upstream fresh-instance installer, never tracked or overwritten here.
                    if (Equal(relative, "servers.dat")) continue;
                    ValidatePath(relative);
                    if (!allPaths.Add(relative)) throw new InvalidDataException("Override/index path conflict.");
                    byte[] bytes;
                    using (var s = entry.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); bytes = m.ToArray(); }
                    using (var h = SHA512.Create()) pack.Files.Add(new PackFile { Path = relative, Sha512 = Hex(h.ComputeHash(bytes)), Content = bytes });
                }
                ValidatePack(pack); return pack;
            }
        }
    }
}
