# Release checklist

- [x] Full test suite passes in Release.
- [x] Release build has zero compiler warnings.
- [x] Portable single-file EXE launches and completes the read-only scan.
- [x] No profile changes desktop or in-game output resolution.
- [x] Every executable optimization has snapshot, verification, and rollback.
- [x] Repair Center accepts only fixed action identifiers and structured arguments.
- [x] No security mitigation, anti-cheat, driver clock, voltage, or firmware changes.
- [x] No network request, updater, telemetry, or download code.
- [x] In-process WPF runtime and screenshot acceptance passes at 1120x720 and 100% (96 DPI) across all eight pages.
- [x] Layout verified at the smallest device-independent window sizes that 100%, 125% and 150% scaling produce on common screens.
- [x] Manual UI review on physical Windows 10 and Windows 11 systems at 100%, 125%, and 150% scaling.
      *Reported complete by the testers, not verified in this repository.*
- [x] Build scanned for false positives before public distribution.
      *Reported complete by the testers, not verified in this repository.*
- [ ] ~~Release executable Authenticode-signed and timestamped.~~ Declined: the project ships unsigned and
      says so in the README. Signing removes the SmartScreen warning; it is unrelated to the licence, so
      the warning stays and the published SHA-256 is what users check instead.

Verified local artifact: `artifacts\66mods-tweaker-1.1.0\66mods Tweaker.exe` v1.1.0, 78,752,053 bytes, SHA-256 `B65767AD6E87178E8453D410AB7CB427A40BAE509D11DC340A6EF679211C229B`, built 21 Aug 2026. The published folder contains the single executable only. The exact published process opened `MainWindowTitle = 66mods Tweaker` and remained responsive during the non-mutating smoke test. Automated acceptance covers real WPF layout at three window sizes across all eight pages, clipping and collision checks, navigation, focus, motion policy and window controls, with captured evidence under `docs/acceptance/frontend-redesign`.

## 1.1

Five boot-configuration settings are no longer executed:

    bcdedit /set disableelamdrivers yes
    bcdedit /set integrityservices disable
    bcdedit /set vsmlaunchtype off
    bcdedit /set hypervisorlaunchtype off
    bcdedit /set pae ForceDisable

They switch off Early Launch Anti-Malware, code integrity services, Virtual Secure Mode, the hypervisor
behind Memory Integrity, and the PAE setting DEP depends on. The README and the About page both state
this product does not touch any of them. The documentation was right about the intent and wrong about
the build, so the build is what changed.

The Windows group drops from 251 changes to 246, and the total from 1115 to 1110. Nothing else changed.

**What this does not fix.** BitLocker seals its key to the boot configuration, so the twenty remaining
`bcdedit` commands still send an encrypted machine to a recovery-key prompt on the next start — which is
how this was found, on a real user's machine. Undo does not cover any of them either: snapshots capture
registry values, and bcdedit is a process. Both are open.

## 1.0

Every optimization group has been run on real hardware and reported working by the testers. That closes
the one gap this repository could never close on its own: three of the four field defects in this
document were found by running the thing, and none of them by a test.

Two defects were fixed on the way to 1.0, both found by checking release readiness rather than by any
test — see the two sections below. One more was user-facing: an AMD or Intel PC was told nothing when the
driver half of a Roblox profile did not happen, so the page looked identical to an NVIDIA one while
writing half as much. The Games page now names the GPU and says what it does and does not write.
`DriverLayerNoteTests` covers it, which the previous attempt lacked — that one was computed into a
collection nothing was bound to, and reached a screen exactly never.

## Ultra Potato, and the frozen source rename

The Roblox profiles wrote nothing but NVIDIA driver settings, which meant Ultra Potato did nothing at all
on the Intel and AMD machines it exists for, and on NVIDIA it spent its whole visual budget on texture
filtering — the one layer that does not cost frames in a CPU-bound client. Both layers now apply as a
single transaction, so a failure in either rolls the other back: `RobloxSettingsTransformer` writes the
client's own `GlobalBasicSettings_13.xml`, and the driver profile grew from 21 accepted settings to 48,
previewed against a real driver rather than assumed. Six of the additions are gates — `Driver Controlled
LOD Bias`, `No override of Anisotropic filtering`, the two predefined-usage flags and the two
behaviour-flag sets — without which overrides we already wrote could be discarded by the driver.
Resolution and `FramerateCap` are deliberately untouched. The milder profiles are unchanged, and a test
pins that, because they are values read out of a real driver rather than derived.

The previous authors' pseudonyms are gone from everything that ships, including the frozen script, which
is now `legacy/source/66mods Tweaks v40012(RUN AS ADMIN).bat`. Regenerating the bundle from the unmodified
source first proved byte-identical output, so every difference in the rebuilt artifacts is an intended one:
still 1917 source fingerprints and 1500 canonical effects, 7 excluded, new bundle SHA-256
`B6AA8D0C12DF3020CE9ED03071F40479BA01C3E19B734CF7C908107790D0C35A`.

A second brand was also embedded throughout that script and had been missed: a "Rip" ASCII wordmark
repeated ten times, nine copies of a banner advertising a third party's **paid** tweak packs, a Russian
dedication, and a menu entry pointing at their Discord. All of it is display text the app never executes,
so removing it moved no counts.

One was not display text. `legacy-0007` is an executed PowerShell effect that creates the restore point,
and its description still carried the other product's name — so every user who ran a profile got a
stranger's branding written into their own System Restore list. It now reads
`'66mods Tweaks Restore Point'`.
This is the only executed command changed; it is a description string with no behavioural effect, and the
ledger totals are unchanged.

- [x] Rebuilt and smoke-tested as 1.0.0 (details above). The single-file build compresses its resources,
      so searching the executable for the old strings proves nothing either way — the guarantee is that the
      source tree is clean and the bundle regenerates from it reproducibly, both of which are checked.
- [ ] **Replace the copy on the CDN.** `66mods-tweaker-0.9.16.exe` predates all of this and still carries
      the old names inside its bundle, including the user-visible restore point description.
- [ ] Ultra Potato measured before and after on one fixed spot in one game, on a genuinely weak machine.
      Nothing in this section is benchmarked. The profile is a documented set of changes, not a
      measured speed-up, and the app does not claim a number anywhere.

## Rollback across an update (found while checking release readiness)

Renaming the frozen script changed the bundle hash, and `RestoreAsync` rejected any snapshot whose hash was
not the current one. Every group applied by 0.9.16 — the one build distributed before this — would have
become permanently un-undoable the moment its user updated, silently, with Restore reporting only "does
not match this compiled profile".

A snapshot carries the registry values it captured, and rolling one back never reads the bundle, so the
hash was a provenance check rather than a correctness requirement. `RestorableBundles` now names the
hashes this product actually shipped, and restore accepts those as well as the current one. Apply still
stamps only the current hash, so the list cannot grow on its own, and a hash that was never shipped is
still refused. `LegacyBundleSnapshotCompatibilityTests` covers both directions and was confirmed to fail
with the fix reverted.

Nothing in the suite caught this, because every existing test applied and rolled back inside one build.

## The frozen sources were not actually frozen (found while checking release readiness)

Cloning the repository and rebuilding produced a different bundle than the one that ships. Git normalises
line endings on checkout, so a fresh clone got a batch file six bytes longer than `source-hashes.json`
records, and the bundle compiled from it carried a different SHA-256 than `LegacyBundleIdentity`. The
whole "frozen under a SHA-256 lock" claim was unverifiable by anyone but the machine that built it.

Nothing failed. `source-hashes.json` was written, published, and never checked by anything.

`.gitattributes` now marks the frozen sources binary so Git leaves their bytes alone, and
`FrozenSourceLockTests` compares every frozen file against its recorded size and hash — size first,
because that is the symptom a line-ending rewrite produces and it reads better in a failure than two hex
strings. Confirmed to fail when the rewrite is simulated. A clone now regenerates the shipped bundle
byte-for-byte.

- [x] A fresh clone builds, passes the full suite, and reproduces `legacy/legacy-bundle.json` exactly.

## Elevated apply budgets

The scoped worker handoff used one two-minute budget for everything: process start, the UAC prompt, the
worker's own confirmation dialog and the entire run. Full Legacy starts PowerShell 88 times at about a
second each, plus 134 other processes, a restore point and over a thousand registry writes, so the client
tore the pipe down mid-run. That surfaced as "Pipe is broken" and left the protected transaction stuck
in progress with zero results.

Approval and execution now have separate clocks — five minutes to start and connect, thirty minutes to
run — and the worker's own limit sits above the client's at thirty-five minutes so it never aborts a run
the client is still waiting on. `Checkpoint-Computer` also got its own four-minute budget; the shared
thirty-second process default was silently losing the restore point that Maximum and Full Legacy depend
on. `OptimizationElevationTimeoutTests` drives the real pipe, protocol and peer validation with an
in-process worker to prove a run that outlasts the approval budget still completes.

## Full Legacy apply failure (found by the first real UAC run, 0.9.3)

The timeout fix let Full Legacy run to completion for the first time, and it then failed verification.
The protected journal recorded the exact reason, and a read-only probe of the profile's 1191 registry
targets on real hardware confirmed three separate defects:

1. **110 of the 1184 writes are deleted by a later effect in the same run.** The bundle writes the IFEO
   `PerfOptions` values for each game executable and then deletes the keys holding them. Verification
   tracked every write and read them all back at the end, found 110 missing, and failed — so Full Legacy
   could never verify on any machine. Deletes now drop the affected targets from the verification set.
2. **Two keys refuse writes even to an elevated administrator** — Edge's
   `TaskCache\Tree\MicrosoftEdgeUpdateTaskMachine{Core,UA}`, owned by SYSTEM. Apply skipped them
   correctly, but rollback rewrote every captured entry blindly and was denied on the same two keys,
   turning every rollback into a partial rollback over changes the run had never made. Restore now skips
   entries whose live state still matches the snapshot exactly.
3. **The failure message carried no reason.** "Did not complete every requested operation" named neither
   the key nor the command. The first recorded failure reason is now appended.

35 values are written more than once; that case was already handled.

`FullLegacyBundleShapeTests` locks properties 1 and 3 of the bundle itself, and
`LegacyBundleVerificationTests` reproduces both failures against a registry fake that deletes subtrees and
denies protected keys. Both new verification tests were confirmed to fail without the fix.

### Second field run (0.9.4): the rollback fix held, verification still failed

The journal recorded a clean full restore — `Restored`, `verified: true` — so the rollback fix worked. The
remaining failure was verification itself, and the cause was a fourth defect no registry-action analysis
could see: a PowerShell effect in the bundle runs
`Remove-Item "HKLM:\...\Image File Execution Options\*" -Recurse`, wiping the very keys the run had just
written 110 values into. Dropping targets on `DeleteKey` actions did not help, because this deletion is a
script, not a registry action.

Two read-only probes ruled out the alternatives before the fix was written: no target writes a default
value, and all 1184 values round-trip exactly through the real registry (written into a scratch key under
HKCU, then deleted).

**Verification now runs in two parts**, because neither alone is honest:

- Every write is read back the instant it is made. That is the only thing a write can truthfully claim,
  and it is immune to anything the rest of the run does afterwards.
- At the end, anything still present must hold the value it was given. A key the run itself destroyed is
  skipped; a value clobbered while its key survives still fails.

## Run console

The elevated worker is a separate process with no console, so a 1493-effect profile's only visible output
was one pass/fail sentence — which is why diagnosing the field failures meant reading the protected journal
by hand. The worker now narrates **every effect**: a four-character status column, the position in the run,
the section, and the actual command, followed by the failure text when one refuses.

```
  ok  147/1493 [windows] reg add "HKLM\...\SystemProfile" /v SystemResponsiveness /t REG_DWORD /d 0 /f
skip  148/1493 [amd] reg add "HKLM\...\Class\{4d36e968}" /v EnableUlps ... (not applicable here)
FAIL  900/1493 [windows] reg delete "HKLM\...\TaskCache\Tree\..." /f -> Access to the registry key is denied.
```

Lines travel on the same authenticated pipe, ahead of the response, in nonce-attested frames on the strict
allowlist; a line carrying a foreign nonce is rejected. 1493 lines cross the real pipe in 165 ms with none
dropped, and the client cap sits at 20,000.

The console keeps the status column in the text so a pasted log reads the same outside the app, colours the
row from it, and opens on the newest line while following further output unless the user scrolls up. The
list is virtualized — under 120 realised containers for 1494 rows, asserted, because a plain `ItemsControl`
would realise every row and stall the UI. Appends are queued and flushed on a 120 ms timer so a fast run
costs a handful of collection updates instead of 1493 blocking dispatcher round-trips.

**Only problems** hides the successes and the deliberate skips, leaving the refusals and the summary: two
refusals among 1493 lines cannot be found by scrolling. A problem count sits next to the line count.

Filtering rebuilds the bound collection rather than using an `ICollectionView`; the first attempt used
`CollectionViewSource.GetDefaultView`, which is thread-affine and broke nine unrelated tests that construct
this view model off the UI thread.

Rendered evidence: `docs/acceptance/frontend-redesign/run-console.png`, `run-console-failed.png` and
`run-console-issues.png`, all captured from a simulated full-size 1494-line run.

**Not yet verified on real hardware:** an end-to-end Full Legacy apply on 0.9.6. The handoff refuses any
initiator that is not the same executable, so this cannot be exercised from a test harness — it needs one
manual run of the published EXE. The console now makes the outcome self-explanatory either way.

## Visual pass (0.9.14)

Four stages, planned before any of it was written. The diagnosis was not ugliness but uniform
emphasis: fourteen boxes of identical grey, so nothing was memorable.

**Landed**

- **Score ring** is now its own control. The arc and the digits are driven by one animated value so they
  can never disagree, it sweeps from 0 over 900 ms, and the stroke states a verdict rather than the brand:
  under 50 amber, 50-85 accent, above 85 green. Re-measuring the same value does not replay the sweep.
- **Aurora backdrop** behind the hero — three blurred radial blobs drifting over tens of seconds. Pure
  vector, no assets, no downloads.
- **Staggered entrance**: hero, live tiles, hardware row, cards, footer, 60 ms apart.
- **Sparklines** on CPU and memory, sixty seconds of history behind each number. The flat meters they
  replaced could not say whether a machine is busy now or busy always.
- **Determinate run ring** reading the effect counter the worker already narrates, floored so it cannot
  read 100% mid-run.
- **Counting numbers** in the before/after panel: each figure travels from the old value to the new one.
- **Sidebar lockup** replacing the plain "66 MODS" label, and the header row now sizes to it.
- **Games empty state** says what to do (launch a game once, then rescan) instead of only what failed.

**Verified, not assumed**

- All ten pages rendered and inspected under `docs/acceptance/frontend-redesign/`.
- `WindowSizeOverflowTests` walks every page at 1000x620, 1366x728 and 1280x693 and fails on any
  horizontal overflow. This is automated now; it used to be a manual note.
- Reduce Motion is covered by tests on the ring and honoured by the aurora, the entrance and the counters.
- Zero network calls and no kernel-mode driver, confirmed by grep over `src/`. `OpenSCManager` appears
  once, opened read-only to count running services.
- Release build has zero warnings.

**Corrected while looking**

The Settings page still claimed "the full interface is never relaunched elevated", which stopped being
true when elevated startup was allowed. The Home hero still named the "Safe" profile after presets were
removed from the page.

**Landed (second pass, completing stages 2-4)**

- **Page dimming during a run.** Everything above the run card fades to 38% while a group is working, so
  the run owns the screen. Applied on both pages that run things.
- **Ring pulse**: a breathing halo behind the run ring, gated on *both* the run being active and Reduce
  Motion being off, and stopped explicitly when the run ends rather than left looping.
- **Card edge highlight**: card borders are a vertical gradient, brightest along the top edge. Surfaces
  gained a slight top-to-bottom lift and raised surfaces are a touch warmer than the base — in a dark
  theme a perfectly even panel reads as a hole.
- **One accent per page.** The breakdown counts, the category change counts and the second "Undo last
  changes" button no longer compete with the primary action. What still carries full accent by design:
  the primary button, the score ring, and the sparkline stroke, which is a data colour.

**Verified**

`VisualMotionTests` covers the sparkline drawing ink with data and drawing nothing with one sample, the
dimmer being instant under Reduce Motion and restoring afterwards, the entrance settling immediately under
Reduce Motion, the panel pushing the preference into the view model, and — read out of the XAML itself —
the ring pulse being gated on both conditions with an explicit stop.

Writing that last test found a real gap: `Entrance` waited for a `Loaded` event before settling, so under
Reduce Motion an element that was never loaded kept whatever opacity it had. It now settles immediately,
because with no animation to schedule there is nothing to wait for.

**Not done**

Nothing outstanding from the four stages. Beyond them, the acceptance renders are still captured with
Reduce Motion on, so the screenshots show the resting state of every animation rather than the motion
itself — the movement is covered by tests, not by pictures.

## Frontend cleanup (0.9.16)

**A test that finds clipping and collisions.** `LayoutClippingTests` walks all pages at the three supported
window sizes and reports three things: text that cannot fit one line of its own type, non-wrapping text
wider than its box with no ellipsis, and any element drawn outside the container holding it.

Getting there took four wrong mechanisms, each disproved by deliberately breaking the layout and watching
the test stay green:

1. `DesiredSize` vs `RenderSize` — WPF measures a child against the space offered, so a squeezed element
   reports a desire already clamped to it. The two agree and the fault is invisible.
2. Grid sibling overlap — the squeezed container's own `RenderSize` shrinks with the row, so its bounds
   never reach the neighbour. Its children spill instead.
3. Spill, first attempt — skipped everything inside a `ScrollContentPresenter`, which is every page.
4. Multi-line text height via `FormattedText` — that formatter and WPF's disagree about where a line
   breaks, which buried the real faults in noise.

It found two real defects immediately: the checkbox tick was a `Path` with no `Stretch`, so it drew at the
geometry's natural size and hung 7 px outside its 16 px box on every page; and the RAM tile's text ran 15 px
past its tile at the minimum window width.

**Explanatory copy cut.** Twenty strings over 70 characters, written to explain the code to its authors, are
gone or shortened; `grep` now reports none. Warnings about irreversibility, restarts and administrator
rights are kept — shortened, never dropped.

**Three dead pages merged.** Mega FPS, Experimental and Legacy Lab had zero commands between them: three
sidebar entries that promised an action and delivered a paragraph. They are now one **About the tweaks**
page with three sections, and the sidebar's advanced list is About, Repair Center, History. The old views
are deleted, not hidden.

**Optimize and Settings reworked.** Optimize was seven identical cards and a block of four large numbers
that were the same on nearly every visit; those numbers are now one caption, and the cards are split into
**Reversible** (undone completely, no restart) and **Aggressive** (needs a restart, some changes permanent),
with a **Run all safe** button that runs only the reversible ones. Settings was three cards carrying more
explanation than control; it is now a list of rows, and the four green badges are one line.

**Not done:** the "one accent per page" idea from the visual plan is still only partly applied.

## Display scaling

The window declares PerMonitorV2 DPI awareness, so Windows renders it natively instead of bitmap-stretching
it at 125% and 150%. Minimum size is 1000x620 device-independent pixels and the opening size is clamped to
the desktop work area, because 1360x860 does not fit several common configurations: 1920x1080 at 125% leaves
832 DIP of height, at 150% only 693, and 1366x768 at 100% only 728. The previous 1180x740 minimum could not
fit on any of those at all.

All ten pages were rendered at 1000x620, 1366x728 and 1280x693. No page overflows horizontally; pages taller
than the window scroll. Sidebar navigation scrolls too — before this, a short window hid Experimental,
Legacy Lab, Repair Center and History with no way to reach them.

## Signing and antivirus

This artifact is **unsigned**. `tools\sign-release.ps1` performs the signing and verification once a
certificate is installed; it references the certificate by thumbprint from the Windows certificate store, so
no key material lives in this repository. Timestamping is applied so signatures keep validating after the
certificate expires.

A certificate must be obtained ahead of the release date — issuance requires identity validation and takes
days, longer for EV. Without one, every download shows a SmartScreen publisher warning.

Windows Defender scanned the published executable locally and reported no threats. A multi-engine scan is
still worth doing before public distribution; note that uploading to a public scanning service distributes
the sample to antivirus vendors, so it is a maintainer decision.

Physical Windows 10/11 hardware review remains a manual check.
