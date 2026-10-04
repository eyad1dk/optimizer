# EZoptimizer V2

A compact Windows utility for tuning supported settings, inspecting hardware and restoring saved originals. No fake FPS scores, RAM cleaners or ping promises.

![V2 interface](docs/overview.png)

## 2.1.0 preview

The same compact V2 interface, with hardware-aware recommendations inside each category and an automatic optimizer built on the existing recovery engine.

- Binary settings use Enable/Disable from the current Windows state. Numeric and multi-value settings keep their selectors.
- Scan PC, category Apply Recommended and Optimize My PC read real hardware/capabilities, skip correct settings, save originals and verify changes. Profiles share this engine. Custom/OEM power plans are preserved.
- CPU bounds/boost, installed power plans, memory compression, automatic/custom pagefiles, TRIM, documented permission policies, optional services and native desktop/input preferences.
- Active-adapter DNS, RSS/RSC and real driver-reported choices; custom DNS, network workload profiles, ICMP/DNS/gateway tests and explicit DHCP troubleshooting.
- DXGI graphics memory, driver/display inventory, safe cache categories, Windows-managed cleanup, system repairs and in-app command output.
- Recovery preserves external changes. Irreversible cleanup/repairs are separate, explicit actions. Unsupported capabilities never become automatic recommendations.

[Implementation, methods and limits](docs/V2.1-IMPLEMENTATION.md) · [Recovery](docs/RECOVERY.md) · [Validation](docs/VALIDATION.md) · [Releases](https://github.com/eyad1dk/optimizer/releases)

## Run or build

The release includes a portable Windows x64 executable with .NET 10 included. Extract the ZIP and run EZoptimizer.exe. No installer. This is an unsigned preview; SHA-256 verifies integrity, not publisher identity. Keep the local EZoptimizer recovery folder when upgrading.

Build from source with the .NET 10 SDK:

```powershell
dotnet run --project tests/ForgePC.Tests -c Release
dotnet publish src/ForgePC/ForgePC.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts
```

## What is tested

109 regression checks, 612 rendered layouts and a bounded native Windows 10 integration check. The latter verified menu apply/restore, opposite-button restoration, failure feedback, DNS flush and protected scanning. Hardware/maintenance inventory was exercised read-only. Full native mutation and Windows 11 certification remain open; see the detailed validation matrix.

Some graphics, privacy and startup settings still use Windows interfaces where a reliable implementation was not established. Recommendations can legitimately select zero changes. Performance depends on the workload and hardware; the app does not promise faster games.

The old root PowerShell entry points are now read-only compatibility tools. Earlier unconditional scripts remain only in Git history.

MIT license. Contributions should include a documented Windows effect, capability check, independent verification, tradeoff and exact recovery for reversible changes.
