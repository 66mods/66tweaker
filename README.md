# 66mods Tweaker

Free, open-source Windows 10/11 optimizer that shows you every command it runs — and can take all of it back.

Most tweakers are a button and a promise. This one records the original value of everything it touches before
touching it, checks afterwards that the change actually landed, prints every command as it goes, and keeps a
transaction journal so a run can be undone. No telemetry, no updater, no ads, no network calls.

> **1.1.** Every optimization group has been run on real hardware. It still changes your system in ways
> some of which need a restart and one of which cannot be undone — read
> [Before you run it](#before-you-run-it) before your first run.

## Download

Grab the latest `66mods-tweaker-<version>.exe` from the [Releases page](../../releases). One portable
executable — nothing to install, and nothing left behind but its own transaction journal.

Check what you downloaded before you run it:

```powershell
Get-FileHash '.\66mods Tweaker.exe' -Algorithm SHA256
```

It must match the SHA-256 published with that release. The build is unsigned, so SmartScreen will warn you
and you will have to choose *More info → Run anyway* — read [Before you run it](#before-you-run-it) first,
and if you would rather not trust a binary at all, [build it yourself](#build-from-source).

Building from source will **not** reproduce the published hash: .NET single-file executables embed build
identifiers, so two builds of the same commit differ by a few bytes. What *is* reproducible is the effect
bundle — `legacy/legacy-bundle.json` regenerates byte-for-byte from `legacy/source/`, and a test enforces
it, so you can verify that the thousand-odd commands in the app are exactly the ones in the script.

## What it does

Optimization is split into groups taken from the original script's own menu. Each has its own **Run** button
and is its own transaction, so a failure in one leaves the others applied.

| Group | Changes | Notes |
| --- | --- | --- |
| Power & CPU | 207 | Core parking, power throttling, high-performance scheme |
| GPU & DirectX | 372 | NVIDIA / AMD / Intel latency and scheduling, DirectX |
| Network & Ping | 124 | Nagle, TCP stack, per-adapter latency |
| Mouse & Keyboard | 48 | Pointer acceleration, polling delays. Fully reversible |
| Windows | 246 | Telemetry, background tasks, visual overhead |
| Memory | 5 | Paging and cache policy for the installed RAM |
| Debloat & Services | 108 | Bundled apps and services. **Uninstalls cannot be undone** |

Also included: per-game profiles (Fortnite, Valorant, GTA V, Minecraft, Roblox) with NVIDIA driver-level
settings applied through NVAPI, a Repair Center limited to a fixed list of fixes, transaction history, and
recovery for interrupted runs.

## How the safety works

- **Exact snapshot.** Every registry value is read and stored before it is written.
- **Verified writes.** Each write is read back at the moment it is made. If it did not stick, the run fails
  instead of reporting success.
- **Rollback.** A failed run restores the snapshot and then verifies the restore. Values the run never
  changed are left alone rather than blindly rewritten.
- **Restore point** before every group except Mouse & Keyboard, which is instantly reversible anyway.
- **A visible log.** Every effect is printed with its command and its result while it runs, with an
  *Only problems* filter and a Copy button.

What it will not do: disable Defender, Windows Update, exploit mitigations, IPv6, recovery or anti-cheat
components; overclock; change your display resolution; or write undocumented GPU values.

## Before you run it

- **It is unsigned.** SmartScreen will warn you. Check the SHA-256 published with each release, or build it
  yourself — the source is here.
- **Antivirus may flag it.** Software that writes a thousand registry values and disables services looks like
  exactly what it is. Nothing here contacts the network; every effect is readable in
  `legacy/legacy-bundle.json`.
- **Some changes need a restart** to take effect. The app never restarts your PC by itself.
- **Debloat removes apps.** That part is a real uninstall, and a rollback cannot bring them back.

If a run misbehaves, `C:\ProgramData\66mods Tweaker\worker.log` records what the elevated helper did, step by
step. Attach it to a bug report.

## Build from source

Requires the .NET 8 SDK.

```powershell
dotnet test 66mods.Tweaker.sln --configuration Release
dotnet publish src\Tweaker.App\Tweaker.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:AllowedReferenceRelatedFileExtensions=none --output artifacts\66mods-tweaker
```

The result is one portable executable. It is self-contained because the .NET 8 desktop runtime is not
preinstalled on Windows 10 — that is why the download is around 79 MB.

## Layout

| Path | What is in it |
| --- | --- |
| `src/Tweaker.App` | WPF interface, elevation handoff, view models |
| `src/Tweaker.Domain` | Transaction model, contracts, parity ledger |
| `src/Tweaker.Infrastructure.Windows` | Registry, power, NVAPI, the effect bundle |
| `legacy/` | The original batch scripts and the bundle compiled from them |
| `tools/Tweaker.LegacyImporter` | Turns those scripts into the frozen bundle |
| `tests/` | 500 tests, including real WPF rendering and pipe round-trips |
| `docs/RELEASE-CHECKLIST.md` | What has been verified, and what has not |

## Where the tweaks come from

The effects are imported from the original **66mods Tweaks** batch script, written by 66 and slavr for the
GTA V community, and frozen into `legacy/legacy-bundle.json` under a SHA-256 lock. Every command in it is
accounted for: applied, replaced with a checked equivalent, or excluded with a recorded reason. Nothing is
invented — see `docs/IMPLEMENTATION-AUDIT.md`.

## Contributing

Bug reports are the most useful thing right now, especially with `worker.log` attached. For pull requests,
`dotnet test` must pass — the test suite is the reason the transaction model can be trusted.

## Licence

MIT — see [LICENSE](LICENSE).

[YouTube](https://www.youtube.com/@66mods) · [Discord](https://discord.com/invite/66mods)
