# 66mods Tweaker 2.0: Ultra Potato on AMD and Intel, and the visual refresh

Approved 2026-09-15 by the owner. Two workstreams, independent of each other, released together as 2.0 (planned as 1.2, renamed at release).

## Decisions taken

| Question | Decision |
| --- | --- |
| How the 21st.dev visual is delivered | Stay on WPF. 21st.dev components are the design source; every piece is ported to XAML by hand. No WebView2, no React runtime. |
| AMD has no per-application driver interface | Apply through ADLX to the whole GPU, with the same snapshot, read-back and rollback as NVIDIA, and say "applies to every game on this GPU" in the interface. |
| Which games get the driver layer | All five: Roblox, Fortnite, Valorant, GTA V, Minecraft. Today it is Roblox only, on NVIDIA only. |

## What exists in 1.1

- `GamePerformanceProfile` has four levels; Ultra Potato is the lowest. Every game gets the client half
  (its own config file plus render scale); the driver half exists only in
  `NvidiaDrsProfileOperation`, hard-wired to `RobloxPlayerBeta.exe`, catalogued in
  `NvidiaGameProfileCatalog.ForRoblox`.
- `GpuProfilePlanner` in the Domain describes AMD and Intel intents as text and marks them
  `RequiresVendorApplication`; nothing applies them.
- On an AMD or Intel PC the Games page says the driver half does not happen and why
  (`DriverLayerNote`). That note goes away where a provider exists.
- The product rule from the About page stands: a driver setting is written only through a
  vendor-supported interface, never through an undocumented registry store.

## Part 1. A driver layer for every vendor

### Model

- `GameDriverTarget(Game, Executables[])` in the Domain. Executables are what the vendor profile
  is keyed on; the client layer keeps its own detection.
- `IGpuDriverProfileProvider` per vendor with the same life cycle the NVIDIA operation has now:
  `IsSupported(snapshot)`, `Preview(profile, target)` returning applied and skipped-with-reason,
  `Snapshot`, `Apply` with read-back, `Rollback` from the snapshot.
- One transaction per apply: client layer plus every present provider. A failure in any layer
  rolls back the others. This is the rule 1.0 set for Roblox and it does not change.
- Preview lines are prefixed by their source: `Roblox ·`, `NVIDIA ·`, `Intel ·`,
  `AMD (whole GPU) ·`.

### Games and executables

| Game | Client layer today | Driver profile executables |
| --- | --- | --- |
| Roblox | `GlobalBasicSettings_13.xml`, FastFlags | `RobloxPlayerBeta.exe` (unchanged) |
| Fortnite | `GameUserSettings.ini` (Unreal) | `FortniteClient-Win64-Shipping.exe` |
| Valorant | `GameUserSettings.ini` (Unreal) | `VALORANT-Win64-Shipping.exe` |
| GTA V | `settings.xml` | `GTA5.exe`, `GTA5_Enhanced.exe` |
| Minecraft | `options.txt` (Java edition) | `javaw.exe` |

Notes to carry into the implementation:
- `javaw.exe` is every Java program, not only Minecraft. The preview must say so.
- Fortnite and Valorant use NVIDIA Reflex, which overrides the driver's low-latency mode. The
  setting stays in the catalogue; the preview does not promise it does anything there.
- Resolution keys stay protected; nothing here changes display mode or refresh rate.

### NVIDIA

Generalise, do not rewrite. `NvidiaDrsProfileOperation` takes a `GameDriverTarget`; the owned
profile name becomes `66mods <Game>`; the catalogue gains a per-game entry. The Roblox catalogue
stays byte-identical and a test pins it, as `NvidiaGameProfileCatalogTests` pins the milder
profiles today. The other four games start from the Ultra Potato set minus the Roblox-only
reasoning in its comments, and each entry is previewed against the owner's RTX 3060 Ti before
it is accepted, the same way the Roblox set was.

### Intel through IGCL

- Intel Graphics Control Library: official, open source, a flat C API in `ControlLib.dll`, which
  the graphics driver installs. Nothing is redistributed; absence means "not supported here".
- `ctlGetSet3DFeature` takes an `ApplicationName`; empty means the adapter's global settings, so
  per-game profiles are native. Capabilities come from `ctlGetSupported3DCapabilities`, and a
  feature the driver does not list is skipped with the reason, as with NVAPI.
- First catalogue, to be checked against a live driver: `ANISOTROPIC` app choice / 2x,
  `TEXTURE_FILTERING_QUALITY` performance, `CMAA` off, `MSAA` off, `SHARPENING_FILTER` off,
  `ADAPTIVE_TESSELLATION` lowest, `LOW_LATENCY` on, `ENDURANCE_GAMING` off,
  `FRAME_LIMIT` off, `GAMING_FLIP_MODES` application default, `VRR_WINDOWED_BLT` off.
- P/Invoke only; the header is the source of truth for structs and enums.

### AMD through ADLX

- AMD Device Library eXtra: official, shipped by Adrenalin as `amdadlx64.dll`. C bindings are
  provided, but every interface is a vtable, so the interop is function-pointer calls through
  `Marshal.GetDelegateForFunctionPointer` behind a small wrapper. No C++/CLI.
- `IADLX3DSettingsServices` is per GPU: Anti-Lag, Chill, Boost, Image Sharpening, Enhanced Sync,
  Wait for Vertical Refresh, Frame Rate Target Control, Anti-Aliasing, Morphological AA,
  Anisotropic Filtering, Tessellation, Radeon Super Resolution, shader-cache reset. There is no
  application-scoped interface.
- Consequence, stated in the interface and the preview: on AMD the driver half applies to every
  game on this GPU. Undo and "Reset driver profile" restore the snapshot for the whole GPU.
- First catalogue, to be checked against a live driver: Chill off, Boost off, Anti-Lag on,
  Enhanced Sync off, Wait for Vertical Refresh off unless application specifies, FRTC off,
  Anti-Aliasing use application settings, Morphological AA off, Anisotropic use application
  settings, Tessellation off, Image Sharpening off, RSR off.

### Verification on hardware

The owner's PC is Ryzen 7 5800X plus RTX 3060 Ti. It can verify the NVIDIA generalisation and
nothing else. Before the AMD and Intel providers ship, each needs one real machine: preview read
from the live driver, apply, the vendor's own control panel showing the values, rollback, control
panel showing the originals. Discord testers are the source; the release checklist records who
ran it and on what.

## Part 2. The visual refresh through 21st.dev

### Constraint and stance

21st.dev serves React and Tailwind. The app is WPF and stays WPF: one self-contained executable,
no network, no runtime that may be missing on Windows 10, and the 500 tests keep running,
including the ones that render the window. 21st.dev is used for search and for the source of
each chosen component; the port to XAML is by hand, token for token.

### Rules of taste

From the owner's decisions on the launcher, which apply here unchanged:
- Motion lives inside elements: cascade on entry, springs, depth, hover light on cards, buttons,
  titles and indicators. Backgrounds may move behind content.
- Nothing is drawn over content. No masks, glows or shader transitions between pages; pages
  change by the existing 160 ms fade. Nothing that reads as noise or a flash.
- Reduce Motion still zeroes positional animation.
- Every effect is shown on a live mockup first and chosen there, not merged on trust.

### Process

1. Search 21st.dev (free) for the shell, cards, navigation, progress ring, buttons, status chips
   and motion. Shortlist by fit to the graphite-and-purple identity.
2. One artifact with two or three directions, laid out on the app's real content: Home,
   Optimize, Games. The owner picks one.
3. The chosen direction is written into this spec as tokens and component rules; the current
   token file is the starting point, not a blank page.
4. Port page by page: Home, Optimize, Games, then Restore, Settings, About, Repair, History.
   After each page a 100% scaling screenshot lands in `docs/acceptance`.
5. `FrontendVisualAcceptanceTests`, `LayoutClippingTests`, `VisualMotionTests` are updated with
   the page, never after.

### Fixed points

Segoe UI Variable stays; no downloaded font or icon package. The emblem stays. Purple for
selection and primary action, gold for warning, red for destructive. No new native light control
may become visible. The optimization, transaction, game-profile, repair and recovery semantics do
not change because of a visual.

### Status on 2026-09-15

Steps 1 and 2 are done. The 21st.dev sources used: Animated Sidebar (unlumen 21517, spring
hover highlight and active bar), Progress (edwinvakayil 26542, spring ring), Stat Card
(ravikatiyar162 7461, count-up), Radio Group with Plan Cards (shadcnspace 10156), Stagger Reveal
Grid (pulkitxm 18357). The mockup with directions A (Ink & Signal), B (Depth) and C (Instrument)
on Home, Optimize and Games, including the 2.0 vendor preview block, was reviewed as a separate
mockup; the owner chose the final direction, "4 · Final".

### Status on 2026-09-16

Both parts are implemented in the worktree. Part 1: `IGpuDriverProfileProvider` with NVIDIA (all five
games), Intel (IGCL, per game) and AMD (ADLX, whole GPU) providers, baselines per vendor, one transaction
per apply across the game file and every vendor, rollback of the layers already written when a later one
refuses; 328 tests green across the three suites. Part 2: the final mockup (round 3, "4 · Финал") ported
to WPF — pill navigation with a "more" menu, aurora pixel shader with a vector fallback, the emblem clip
as a looped H.264 clip through Media Foundation, with an in-executable frame pack as the fallback (no media player needed), glass Optimize panel with seven switch cards, Games with the
four-stop slider, the Ask 66 drawer (a FAQ filled in from this PC's facts; the DeepSeek option was
removed on 2026-09-16 — a shipped key is extractable and a relay was not worth it). The
acceptance suite renders all eight pages and the drawer; the clipping and overflow checks pass at
1000×620, 1280×693 and 1366×728.

Still open: hardware verification of the AMD and Intel providers (step 4 of the order below). The
"no network calls" promise in the README stands.

## Order

1. Part 1 model and the NVIDIA generalisation, with tests. Verifiable here.
2. Intel provider, then AMD provider, each with a preview that works on a machine without the
   vendor's driver (everything skipped with a reason) and tests for the catalogue and snapshot.
3. Part 2 steps 1 to 3 in parallel with the above; steps 4 and 5 after approval.
4. Release 2.0 after hardware verification of both providers. The release checklist gains a
   section per vendor with the tester, the driver version and the screenshots.

## Open until answered

- Minecraft Bedrock (`Minecraft.Windows.exe`) is not detected today and is not in scope.
- Which Discord testers have an AMD card and an Intel Arc or Xe GPU, and which driver versions.
