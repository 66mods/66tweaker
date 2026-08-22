# Implementation audit

What this app does, what it deliberately refuses to do, and what has not been checked yet.

## Implemented

- Windows 10/11 WPF control centre, English, local only: custom borderless chrome, a 214 px vector-icon
  sidebar, dark control templates throughout, restrained motion and Reduce Motion support.
- Optimization is split into seven groups taken from the original script's own menu — Power & CPU, GPU &
  DirectX, Network & Ping, Mouse & Keyboard, Windows, Memory, Debloat & Services. Each has its own Run
  button and is its own transaction, so a failure in one leaves the others applied.
- 1500 canonical effects compiled from 1917 source mutations in the frozen batch script, under a SHA-256
  lock. The per-command ledger is `legacy/parity-manifest.json`; the compiled bundle is
  `legacy/legacy-bundle.json`.
- Transaction journals written atomically before any mutation, verification after apply, verified
  rollback, recovery for interrupted sessions, and history that survives a restart.
- Every write is read back at the moment it is made, and anything still present at the end must hold the
  value it was given. A key the run itself destroyed is skipped rather than counted as a failure.
- A run console that prints every effect as it executes — status, position, section and the actual
  command — with an *Only problems* filter and a Copy button.
- A scoped elevated worker over a secured named pipe: admin-only ACL, first-instance-only, peer PID and
  SID validation, nonce attestation. Approval and execution have separate time budgets.
- Game profiles for Fortnite, Valorant, GTA V, Minecraft and Roblox. Roblox writes two layers in one
  transaction: the client's own `GlobalBasicSettings_13.xml`, and the NVIDIA application profile for
  `RobloxPlayerBeta.exe` through official NVAPI DRS, with setting ids resolved from the installed driver
  by name and values checked against the driver's own list before anything is written.
- Allowlisted repair actions only. The legacy Wi-Fi repair inspects every target service first, changes
  only legacy-disabled states, and compensates on partial failure.
- Self-contained single-file win-x64 executable. No updater, telemetry, advertising, downloads or cloud
  dependency, and no network code of any kind.

## Deliberate safety boundaries

- Nothing touching Defender, Windows Update, ELAM, exploit mitigations, IPv6, recovery, integrity
  services, anti-cheat or firmware. No clock, voltage or overclocking changes.
- No kernel-mode driver, which is why the app reports no temperatures or clock speeds.
- No unofficial Roblox FastFlags.
- No forced rendering API: the right one depends on GPU, driver and game version.
- No undocumented vendor registry writes. A driver setting is written only where a supported interface
  exists — NVAPI DRS today — and a setting the driver does not enumerate is skipped with a visible reason
  rather than guessed. AMD and Intel have no equivalent per-application interface in use here, so their
  profiles change the game's own configuration and say so instead of pretending otherwise.
- Display resolution is never changed, on the desktop or in a game. `GameProfilePolicy.ProtectedKeys`
  names the configuration keys this rule protects, and a test enforces it.
- The player's frame-rate limit is never lowered by a profile.
- Debloat uninstalls applications. That is a real uninstall and rollback cannot bring them back; it is
  stated on the page and in the README.

## Verified

- 496 tests pass in Release with zero compiler warnings, including real WPF rendering, real named-pipe
  round-trips, and registry fakes that reproduce the field failures this project has actually hit.
- Layout is checked at three window sizes — 1000x620, 1366x728 and 1280x693, the sizes 100%, 125% and
  150% scaling produce on common screens — for horizontal overflow, clipped text and overlapping content,
  across all eight pages. Captured evidence is under `docs/acceptance/frontend-redesign`.
- The published single-file executable launches, opens its window, stays responsive and exits cleanly.

## Before public distribution

Two checks remain, and neither can be honestly replaced by unit tests or a local smoke test:

- A manual pass on physical Windows 10 and Windows 11 hardware across the 100/125/150% DPI matrix.
- An antivirus false-positive review. Software that writes a thousand registry values and disables
  services looks like what it is, and the build is unsigned by choice — see the README.

A full run of every group on real hardware has also not been done end to end.
