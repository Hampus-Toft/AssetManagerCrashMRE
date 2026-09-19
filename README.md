# Asset Manager Crash (MRE)

**MRE = minimal reproducible example.** A tiny BepInEx plugin for Valheim that reproduces a crash in Jotunn's
`AssetManager` when STUWard 1.3.11+ is installed alongside Jotunn:

```
[Error  :  HarmonyX] Failed to patch ... SoftReferenceableAssets.AssetBundleLoader::GetAllAssetPathsMappedToAssetID():
  System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
  at Jotunn.Managers.AssetManager+Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID (...) AssetManager.cs:98
```

followed by a permanent `TypeInitializationException` from `Jotunn.Managers.AssetManager..cctor()` for the rest of the
session.

You can run it three ways, described under [Usage](#usage): **with the real STUWard**, **without STUWard** (a control
that must not crash), and **standalone** (STUWard's patch is simulated, so the crash reproduces without STUWard).

## Cause

Two mods rewrite the same IL in the same vanilla method:

| Mod | Patch | Behaviour |
| --- | --- | --- |
| Jotunn 2.30.0 | `AssetManager.Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID` (transpiler) | `CodeMatcher.MatchForward(Calls Dictionary<string,AssetID>.Add).SetInstruction(AddSafe)`. **No check that the match succeeded.** Applied lazily, the first time `AssetManager` is touched. |
| STUWard >= 1.3.11 (decompiled from 1.3.15) | `WardUiResources.AssetPathsPatch` (transpiler) | Same rewrite: every call to `Dictionary<string,AssetID>.Add` becomes a safe-add. Tolerant of finding nothing. Applied eagerly in `Plugin.Awake()`. |

STUWard runs first, so by the time Jotunn's transpiler runs the `Add` call is gone. `MatchForward` leaves
`CodeMatcher.Pos == -1`, and `SetInstruction` throws `ArgumentOutOfRangeException`. Jotunn's `AssetManager` static
constructor is then permanently broken, so every later `GUIManager.GetSprite()` / prefab registration through Jotunn
rethrows. If Jotunn's patch happened to be applied first, STUWard's tolerant transpiler would just find nothing to do
and there would be no error. The failure is order-dependent.

STUWard 1.3.11's changelog: "Removed STUWard's Jotunn runtime dependency ... native asset lookup ... now use a focused
implementation inside STUWard". Its 1.3.15 manifest no longer lists Jotunn, but Jotunn is still commonly installed
alongside it. STUWard's public GitHub repo is still at 1.3.10, before this change.

## What the plugin does

- `Awake`: reads the `SimulateStuWardPatch` config option. If `true`, it applies a copy of STUWard's `AssetPathsPatch`
  (same target, same rewrite, same timing). If `false`, it patches nothing.
- `Update`: once you are in-game (`Player.m_localPlayer != null`), calls `GUIManager.Instance.GetSprite("button")`, the
  same call that triggers `GUIManager.InitializeAssets -> PrefabManager.Cache.GetPrefab -> AssetManager..cctor` in the
  real crash. It then logs the verdict (`REPRODUCED` / `NOT REPRODUCED`) and lists the Harmony owners of transpilers on
  the target method.

The plugin has a hard dependency on Jotunn, so Jotunn must be installed in every run below.

## Usage

All three runs use a BepInEx profile (r2modman / Thunderstore Mod Manager, or a manual BepInEx install) containing
**BepInExPack_Valheim**, **Jotunn 2.30.0** and **this plugin**. They differ only in whether STUWard is installed and in
the `SimulateStuWardPatch` option.

The option lives in `BepInEx/config/dev.hampus.asset-manager-crash-mre.cfg` (section `[Repro]`). The file is created the
first time the game starts with the plugin installed; edit it (or use r2modman's config editor) and restart the game
for a change to take effect. Read the results in `BepInEx/LogOutput.log` (or the BepInEx console).

Log lines below are trimmed. BepInEx adds its own prefix (`[Info   :Asset Manager Crash (MRE)]` etc.) and timestamps
vary. **Load into a world** for each run: the verdict is only logged once the local player exists.

### Summary

| Run | STUWard installed | `SimulateStuWardPatch` | Expected result |
| --- | --- | --- | --- |
| [1. With STUWard](#1-with-stuward-real-conflict) | 1.3.11 or newer | `false` | Crash: `REPRODUCED` |
| [2. Without STUWard](#2-without-stuward-control) | no | `false` | No crash: `NOT REPRODUCED` |
| [3. Standalone](#3-standalone-simulated-stuward) | no | `true` | Crash: `REPRODUCED` (same as run 1) |

### 1. With STUWard (real conflict)

Shows the bug with the real mods, no simulation.

1. Profile: Jotunn 2.30.0 + this plugin + **STUWard 1.3.11 or newer** (reported on 1.3.12).
2. Leave `SimulateStuWardPatch = false` (the default). Do not enable it here: STUWard already provides the patch.
3. Start the game and load into a world.

Expect, in this order:

```
SimulateStuWardPatch=false: no simulated patch applied (relying on other installed mods).
... (STUWard's Awake has already rewritten the Dictionary.Add call) ...
[Error  :  HarmonyX] Failed to patch ... AssetBundleLoader::GetAllAssetPathsMappedToAssetID(): System.ArgumentOutOfRangeException ...
REPRODUCED: GUIManager.GetSprite threw <TypeInitializationException or ArgumentOutOfRangeException>: ...
Root cause: ArgumentOutOfRangeException: Index was out of range. ...
Harmony transpilers currently registered on AssetBundleLoader.GetAllAssetPathsMappedToAssetID: sighsorry.STUWard [...], <Jotunn's Harmony id> [...]
```

- The HarmonyX `Failed to patch` error is the crash itself. It appears when Jotunn's `AssetManager` is first touched,
  which can be before this plugin's own `Update` runs.
- Both `sighsorry.STUWard` and Jotunn's Harmony id show up as transpiler owners: that is the conflict.
- Jotunn stays broken until you restart the game. Other Jotunn-based features (custom GUI sprites, prefab
  registration) will also fail.

### 2. Without STUWard (control)

Confirms the plugin and Jotunn are fine on their own and that the crash needs the second patch.

1. Profile: Jotunn 2.30.0 + this plugin only. **No STUWard.**
2. Leave `SimulateStuWardPatch = false`.
3. Start the game and load into a world.

Expect:

```
SimulateStuWardPatch=false: no simulated patch applied (relying on other installed mods).
NOT REPRODUCED: GUIManager.GetSprite returned 'button'; Jotunn's AssetManager initialised.
Harmony transpilers currently registered on AssetBundleLoader.GetAllAssetPathsMappedToAssetID: <Jotunn's Harmony id> [...]
```

- No HarmonyX `Failed to patch` error, no `TypeInitializationException`.
- Jotunn is the only transpiler owner.
- If this run crashes, something else in the profile is rewriting the same method, and the transpiler-owner line names
  it. Remove the other mods and retry.

### 3. Standalone (simulated STUWard)

Reproduces the crash without STUWard, for anyone who wants to debug or test a fix without installing it. The plugin
applies its own copy of STUWard's `AssetPathsPatch` in `Awake`, which matches STUWard's timing (before Jotunn's lazy
patch).

1. Profile: Jotunn 2.30.0 + this plugin only. **No STUWard.**
2. Set `SimulateStuWardPatch = true` and restart the game.
3. Load into a world.

Expect:

```
SimulateStuWardPatch=true: applied simulated STUWard AssetPathsPatch.
[Error  :  HarmonyX] Failed to patch ... AssetBundleLoader::GetAllAssetPathsMappedToAssetID(): System.ArgumentOutOfRangeException ...
REPRODUCED: GUIManager.GetSprite threw <TypeInitializationException or ArgumentOutOfRangeException>: ...
Root cause: ArgumentOutOfRangeException: Index was out of range. ...
Harmony transpilers currently registered on AssetBundleLoader.GetAllAssetPathsMappedToAssetID: dev.hampus.asset-manager-crash-mre.simulated-stuward [...], <Jotunn's Harmony id> [...]
```

- Same failure as run 1. The only difference is that the first transpiler owner is
  `dev.hampus.asset-manager-crash-mre.simulated-stuward` instead of `sighsorry.STUWard`.
- Don't enable this together with real STUWard; there is nothing to gain and it muddies the owner list.

### Reading the verdict

| Log line | Meaning |
| --- | --- |
| `NOT REPRODUCED: ... Jotunn's AssetManager initialised.` | Jotunn's transpiler applied cleanly. |
| `REPRODUCED: ...` + `Root cause: ArgumentOutOfRangeException` | Jotunn's transpiler found no `Dictionary<string,AssetID>.Add` call because another patch had already rewritten it. |
| Neither line appears | You never loaded into a world (the check waits for `Player.m_localPlayer`), or the plugin isn't loading (check Jotunn is installed and the plugin is in `BepInEx/plugins`). |

## Build

```
dotnet build -c Release
```

Needs a Valheim install with Jotunn 2.30.0 at `BepInEx\plugins\Jotunn\Jotunn.dll`. Override with
`VALHEIM_INSTALL_DIR` / `-p:ValheimInstallDir=...` / `-p:JotunnDllPath=...`. Add `-p:DeployToGame=true` to copy the
DLL into `BepInEx\plugins` (it never launches or kills the game).

### Thunderstore package

```
dotnet build -c Release -p:ThunderstorePackage=true
```

Writes `bin\thunderstore\AssetManagerCrashMRE.zip` (`manifest.json`, `icon.png`, `README.md` and the plugin DLL at the
zip root). In Thunderstore Mod Manager / r2modman, use **Settings > Profile > Import local mod** and pick the zip.
Version and dependencies (BepInExPack_Valheim 5.4.2350, Jotunn 2.30.1) live in `Thunderstore\manifest.json`; keep the
version in step with the plugin's `[BepInPlugin]` attribute.

## Suggested fixes (either is sufficient on its own)

- **STUWard:** skip `AssetPathsPatch` / `AssetCatalogPatch` when Jotunn is loaded
  (`Chainloader.PluginInfos.ContainsKey("com.jotunn.jotunn")`), or apply them after checking the target's IL still
  contains the call.
- **Jotunn:** guard the transpiler, e.g. only call `SetInstruction` when `matcher.IsValid`, and no-op if the `Add` call
  was already rewritten.

## Provenance

Found while debugging a ValheimRadar crash on a 14-mod Thunderstore profile (STUWard 1.3.12, Jotunn 2.30.0,
Valheim 1.0.12). The investigation and this MRE were produced with Claude (Claude Code, Anthropic) working from the
user's BepInEx logs, Jotunn's v2.30.0 source, and the decompiled STUWard DLL. The ordering behaviour was also
confirmed outside the game with the game's own Harmony (`BepInEx/core/0Harmony.dll`) against a dummy method.
