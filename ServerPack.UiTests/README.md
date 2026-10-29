# Portable server UI helper tests

Run from the repository root with the .NET 9 SDK and Python 3:

```sh
dotnet run --project ServerPack.UiTests/ServerPack.UiTests.vbproj
python3 ServerPack.UiTests/ui_contract.py
```

The VB executable compiles the **actual** `ModServerPack.vb` and all unchanged
`ServerPack.Core/*.cs` sources. Only PCL's unavailable desktop/game/worker types
are stubbed. Newtonsoft.Json is referenced from the SDK, so no package download
is required. Fixtures use the platform temporary directory (`TMPDIR` is honored).
The inherited catalog load assertion may attempt the normal catalog endpoint,
then uses the copied bundled catalog when offline; rollback tests themselves use
only local fixtures and no mrpack, preparation, catalog version or network.

The rollback coverage exercises pending recovery (normal `IsManaged` rejects it),
missing state with a valid pending journal, actual backed-up file restoration,
user-edit conflict preservation, malformed metadata/pointers, exact server IDs
including overlapping prefixes, multiple dependency/Minecraft versions,
nonrecursive scanning, and linked paths. Python checks WPF routing and explicit
multi-instance selection contracts; it is not a Windows UI execution test.

Red/green observed during development:
- Missing local resolver: expected assertion failure, then local pending recovery passed.
- Missing state: resolver returned no roots, then journal-based identity recovery passed.
- Existing rollback branch performed preparation/managed validation: all three routing
  contracts failed, then passed after moving rollback ahead of preparation.
- Malformed state, oversized format integers, and non-string journal IDs each
  reproduced resolver failures before their bounded parsing fixes.

Full Windows/WPF build and interactive dialog verification remain the parent
agent's responsibility. No authentication or normal launch checks are stubbed in
production, and the core rollback safety checks remain unchanged.
