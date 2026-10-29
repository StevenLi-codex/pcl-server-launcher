# PclServerPack core

`netstandard2.0`, C# latest, nullable disabled, Newtonsoft.Json 13.0.3. No WPF/Windows dependency; usable from the .NET Framework 4.8 launcher. All contract types are in `PclServerPack`.

## Integration

1. Parse the trusted catalog into `ServerDefinition`. `PackSha256` is **required**; no trust-on-first-use fallback exists.
2. `await PackService.PrepareAsync(server, cacheDirectory, cancellationToken)` downloads and verifies a <=64 MiB archive, then parses the Modrinth index, client environment, dependencies and first-party overrides. `ArchivePath` points at the pinned archive for the upstream installer.
3. For a **new, version-isolated instance**, let the existing launcher install the prepared archive and Minecraft/loader. Pass that instance's isolated game directory to `AdoptInstalled(instanceRoot, pack)` only after successful installation. Adoption verifies every required file and any installed optional files before writing managed state. Do not pass a shared `.minecraft` directory or an existing unrelated instance.
4. For updates, call `Plan(instanceRoot, pack)`, show `Summary`, then `await ApplyAsync(instanceRoot, pack, plan, token, progress)`. Apply revalidates current state; a plan is not authority to overwrite unexpected files. Loader/Minecraft changes require a fresh instance.
5. `RollbackLast(instanceRoot)` restores the last update's managed bytes and state. Unknown personal files remain untouched. If a previous commit was interrupted, planning/applying fails closed until this recovery succeeds. User edits conflicting with recovery are reported, not overwritten.

## Preservation and limits

- Managed paths are restricted to `mods/`, `resourcepacks/`, `shaderpacks/`, `config/`, `defaultconfigs/`, `kubejs/`, `scripts/`, plus top-level `README.txt` and `CREDITS.json`.
- `servers.dat` from overrides is deliberately **not** managed. The upstream fresh-instance installer may seed it once. This library never tracks, overwrites or removes it. Saves, options, launcher accounts and unrelated files are not managed.
- Overrides are treated as pinned first-party bytes, not extracted paths/links. Config edits are preserved by rejecting the entire plan before mutation; there is no unsafe merge or force option. Index/override collisions are rejected rather than guessing precedence.
- Client-unsupported files are skipped. Optional files are tracked only if present and verified during adoption; absent/new optional files remain opt-out during updates.
- Every stale removal must belong to the prior managed state and still match its recorded hash. Unknown additions and edits/deletions of previously managed files are conflicts. Parent-file collisions, case aliases, Windows reserved devices/8.3-like aliases, traversal, ADS, symlinks/reparse points and managed-state path traversal are rejected.
- Sequential HTTPS downloads, explicit HTTPS-only redirects (maximum five), per-request ten-minute timeout; no insecure certificate exceptions. The mc-download host gets the required browser-compatible User-Agent and same-host Referer; generic public hosts get `PclServerPack/1.0` without that Referer.
- Limits: 20,000 indexed/managed files, 512 MiB per managed file, 2 GiB installed hash/staging aggregate, 16 MiB per ZIP entry, 128 MiB ZIP expanded aggregate, 8 MiB index, bounded JSON nesting. The first declared download source is used, failing closed rather than masking download/hash errors with fallback sources.

## Transaction semantics

All changed files download and hash-check before any managed file mutation. The instance lock is held across planning/staging/commit. Original changed/removed bytes and before/after state are retained under `.pcl-server-pack/tx-*`, alongside a pending intent marker. Individual replacements use same-filesystem `File.Replace`/`File.Move`; this is **not an atomic all-files transaction**. Cancellation is honored during download/staging and before commit, then deferred while the commit finishes. A commit I/O error leaves the journal for explicit `RollbackLast`; rollback prevalidates every affected target and backup before touching files. Backups are retained, with no recursive deletion of any managed/user directory.

This protects against ordinary errors/process interruption, not hostile simultaneous local filesystem mutation or hardware/power-loss durability. It does not provide an OS-level sandbox or a directory-fsync transaction. Run updates with Minecraft stopped; another writer can still cause a conflict. Abandoned precommit transaction directories may remain after abrupt process exit and contain only staged bytes, never authorization to modify unmanaged files.

## Verification

```
dotnet run --project ServerPack.Tests
```

Tests are a net9.0 console harness referencing this library, with real temporary files. Synthetic byte fixtures are explicitly labeled. `PCL_PACK_INTAKE` enables the four previously downloaded real pinned archive fixtures (`reports.json` + mrpacks); `PCL_LIVE_CATALOG` enables real HTTPS archive preparation from a catalog file. Symlink cases require an OS/account allowed to create symlinks. No production server/site writes are made.

Public `UseHttpTransport(HttpMessageHandler)` is an async-context-scoped fixture seam; the injected handler must not auto-redirect. Production uses the built-in handler with auto-redirect disabled. The HTTPS, size and hash policy still runs with fixture handlers.
