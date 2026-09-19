# Asset Manager Crash (MRE)

**MRE = minimal reproducible example.** A tiny BepInEx plugin for Valheim that reproduces a crash in Jotunn's
`AssetManager` when STUWard 1.3.11+ is installed alongside Jotunn:

```
[Error  :  HarmonyX] Failed to patch ... SoftReferenceableAssets.AssetBundleLoader::GetAllAssetPathsMappedToAssetID():
  System.Reflection.TargetInvocationException ---> System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
  at HarmonyLib.CodeMatcher.SetInstruction (...)
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
| Jotunn 2.30.0 / 2.30.1 | [`AssetManager.Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID`](https://github.com/Valheim-Modding/Jotunn/blob/2d4d875ce16c21ad8c99e99864d42e2c4821886b/JotunnLib/Managers/AssetManager.cs#L92-L102) (transpiler) | `CodeMatcher.MatchForward(Calls Dictionary<string,AssetID>.Add).SetInstruction(AddSafe)`. **No check that the match succeeded.** Applied lazily, the first time `AssetManager` is touched. |
| STUWard >= 1.3.11 (checked against 1.3.15) | [`WardUiResources.AssetPathsPatch`](https://github.com/sighsorry1029/STUWard/blob/e3aac84cf824cd765928d1e525e8e5adc9b8430d/WardUiResources.cs#L320-L344) (transpiler) | Same rewrite: every call to `Dictionary<string,AssetID>.Add` becomes a safe-add. Tolerant of finding nothing. Applied eagerly in `Plugin.Awake()`. |

STUWard runs first, so by the time Jotunn's transpiler runs the `Add` call is gone. `MatchForward` finds nothing and
leaves the `CodeMatcher` past the end of the instruction list (`IsValid=False`, `Pos == Length`, 30 of 30 in the game
logs), and `SetInstruction` then throws `ArgumentOutOfRangeException`. Both transpilers have the default Harmony
priority (400), so the order is just registration order: STUWard registers in `Awake`, Jotunn only when `AssetManager`
is first touched. Jotunn's `AssetManager` static
constructor is then permanently broken, so every later `GUIManager.GetSprite()` / prefab registration through Jotunn
rethrows. If Jotunn's patch happened to be applied first, STUWard's tolerant transpiler would just find nothing to do
and there would be no error. The failure is order-dependent.

STUWard 1.3.11's changelog: "Removed STUWard's Jotunn runtime dependency ... native asset lookup ... now use a focused
implementation inside STUWard". Its 1.3.15 manifest no longer lists Jotunn, but Jotunn is still commonly installed
alongside it. STUWard's public GitHub repo (`sighsorry1029/STUWard`) is at 1.3.15 and contains the patch; a comment in
it says "Preserve the first mapping, as the former Jotunn asset lookup did", so it deliberately copies Jotunn's
`AddSafe` behaviour. Jotunn's `AddSafe` (`key != null && !ContainsKey(key)`) and STUWard's `AddPath` are equivalent, so
either transpiler alone gives the same result. The conflict is only about who rewrites the call first.

## Tested versions

| Component | Version used for the repro | Version in the upstream issues |
| --- | --- | --- |
| Valheim | 1.0.15 | 1.0.12 (n-40) |
| BepInExPack_Valheim | 5.4.2350 (BepInEx 5.4.23.5) | 5.4.2350 |
| Jotunn | 2.30.1 | 2.30.0 |
| STUWard | 1.3.15 | 1.3.15 |

The failing line in Jotunn's transpiler is `AssetManager.cs:98` in both the 2.30.0 traces in the issues and the 2.30.1
trace in `logs/`, so this is the same code. Jotunn's `dev` branch (the default branch, at the v2.30.1 release commit when
this was written) still has no validity check on the match. Line 98 is the start of the `return new CodeMatcher(...)`
statement that contains the `SetInstruction` call.

## What the plugin does

- `Awake`: reads the `SimulateStuWardPatch` config option. If `true`, it applies a copy of STUWard's `AssetPathsPatch`
  (same target, same rewrite, same timing). If `false`, it patches nothing.
- `Update`: once you are in-game (`Player.m_localPlayer != null`), calls `GUIManager.Instance.GetSprite("button")`, the
  same call that triggers `GUIManager.InitializeAssets -> PrefabManager.Cache.GetPrefab -> AssetManager..cctor` in the
  real crash. It then logs the verdict (`REPRODUCED` / `NOT REPRODUCED`), lists the Harmony owners of transpilers on
  the target method, and replays the transpiler chain one step at a time (`IL diagnostic:` lines), showing how many
  `Dictionary.Add` calls each step leaves and whether Jotunn's `MatchForward` is valid over the IL it receives.

The plugin has a hard dependency on Jotunn, so Jotunn must be installed in every run below.

## Usage

All three runs use a BepInEx profile (r2modman / Thunderstore Mod Manager, or a manual BepInEx install) containing
**BepInExPack_Valheim**, **Jotunn 2.30.1** and **this plugin**. They differ only in whether STUWard is installed and in
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

1. Profile: Jotunn 2.30.1 + this plugin + **STUWard 1.3.11 or newer** (reproduced with 1.3.15; first seen on 1.3.12).
2. Leave `SimulateStuWardPatch = false` (the default). Do not enable it here: STUWard already provides the patch.
3. Start the game and load into a world.

Expect, in this order (trimmed; full excerpt in [`logs/run1-with-stuward.log`](logs/run1-with-stuward.log)):

```
SimulateStuWardPatch=false: no simulated patch applied (relying on other installed mods).
... (STUWard's Awake has already rewritten the Dictionary.Add call) ...
[Error  :  HarmonyX] Failed to patch ... AssetBundleLoader::GetAllAssetPathsMappedToAssetID(): System.Reflection.TargetInvocationException: ... ---> System.ArgumentOutOfRangeException: Index was out of range. ...
REPRODUCED: GUIManager.GetSprite threw TypeInitializationException: The type initializer for 'Jotunn.Managers.AssetManager' threw an exception.
Root cause: ArgumentOutOfRangeException: Index was out of range. ...
Harmony transpilers currently registered on AssetBundleLoader.GetAllAssetPathsMappedToAssetID: sighsorry.STUWard [STUWard.WardUiResources+AssetPathsPatch.Transpiler] (priority 400), com.jotunn.jotunn [Jotunn.Managers.AssetManager+Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID] (priority 400)
IL diagnostic: vanilla AssetBundleLoader.GetAllAssetPathsMappedToAssetID has 30 instructions, 1 call(s) to ...Dictionary...Add.
IL diagnostic: transpiler 1/2 (sighsorry.STUWard): applied OK; 0 call(s) to Dictionary.Add remain; callees introduced so far: STUWard.WardUiResources+AssetPathsPatch.AddPath.
IL diagnostic: transpiler 2/2 (com.jotunn.jotunn): Jotunn's MatchForward(Calls Dictionary.Add) over its input IL -> IsValid=False, Pos=30, Length=30, Add calls in input=0.
IL diagnostic: transpiler 2/2 (com.jotunn.jotunn): applying it threw ArgumentOutOfRangeException: Index was out of range. ...
```

- The HarmonyX `Failed to patch` error is the crash itself. It appears when Jotunn's `AssetManager` is first touched,
  which can be before this plugin's own `Update` runs.
- Both `sighsorry.STUWard` and Jotunn's Harmony id show up as transpiler owners: that is the conflict.
- The IL diagnostic lines show why: STUWard's step removes the only `Add` call, so Jotunn's `MatchForward` has nothing
  to match (`IsValid=False`, 0 `Add` calls in its input).
- Jotunn stays broken until you restart the game. Other Jotunn-based features (custom GUI sprites, prefab
  registration) will also fail.

### 2. Without STUWard (control)

Confirms the plugin and Jotunn are fine on their own and that the crash needs the second patch.

1. Profile: Jotunn 2.30.1 + this plugin only. **No STUWard.**
2. Leave `SimulateStuWardPatch = false`.
3. Start the game and load into a world.

Expect (trimmed; full excerpt in [`logs/run2-control-without-stuward.log`](logs/run2-control-without-stuward.log)):

```
SimulateStuWardPatch=false: no simulated patch applied (relying on other installed mods).
NOT REPRODUCED: GUIManager.GetSprite returned 'button(Clone)'; Jotunn's AssetManager initialised.
Harmony transpilers currently registered on AssetBundleLoader.GetAllAssetPathsMappedToAssetID: com.jotunn.jotunn [Jotunn.Managers.AssetManager+Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID] (priority 400)
IL diagnostic: vanilla AssetBundleLoader.GetAllAssetPathsMappedToAssetID has 30 instructions, 1 call(s) to ...Dictionary...Add.
IL diagnostic: transpiler 1/1 (com.jotunn.jotunn): Jotunn's MatchForward(Calls Dictionary.Add) over its input IL -> IsValid=True, Pos=17, Length=30, Add calls in input=1.
IL diagnostic: transpiler 1/1 (com.jotunn.jotunn): applied OK; 0 call(s) to Dictionary.Add remain; callees introduced so far: Jotunn.Managers.AssetManager+Patches.AddSafe.
```

- No HarmonyX `Failed to patch` error, no `TypeInitializationException`.
- Jotunn is the only transpiler owner, and its `MatchForward` finds the `Add` call (`IsValid=True`, 1 `Add` call in its
  input), unlike run 1.
- Jotunn logs a few `Ambiguous asset name for path ... using old path` warnings while initialising `AssetManager`.
  They also appear in this control and are not errors.
- If this run crashes, something else in the profile is rewriting the same method, and the transpiler-owner line names
  it. Remove the other mods and retry.

### 3. Standalone (simulated STUWard)

Reproduces the crash without STUWard, for anyone who wants to debug or test a fix without installing it. The plugin
applies its own copy of STUWard's `AssetPathsPatch` in `Awake`, which matches STUWard's timing (before Jotunn's lazy
patch).

1. Profile: Jotunn 2.30.1 + this plugin only. **No STUWard.**
2. Set `SimulateStuWardPatch = true` and restart the game.
3. Load into a world.

Expect (trimmed; full excerpt in [`logs/run3-standalone-simulated.log`](logs/run3-standalone-simulated.log)):

```
SimulateStuWardPatch=true: applied simulated STUWard AssetPathsPatch.
[Error  :  HarmonyX] Failed to patch ... AssetBundleLoader::GetAllAssetPathsMappedToAssetID(): System.Reflection.TargetInvocationException: ... ---> System.ArgumentOutOfRangeException: Index was out of range. ...
REPRODUCED: GUIManager.GetSprite threw TypeInitializationException: The type initializer for 'Jotunn.Managers.AssetManager' threw an exception.
Root cause: ArgumentOutOfRangeException: Index was out of range. ...
Harmony transpilers currently registered on AssetBundleLoader.GetAllAssetPathsMappedToAssetID: dev.hampus.asset-manager-crash-mre.simulated-stuward [AssetManagerCrashMRE.AssetManagerCrashMrePlugin+SimulatedStuWardAssetPathsPatch.Transpiler] (priority 400), com.jotunn.jotunn [Jotunn.Managers.AssetManager+Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID] (priority 400)
IL diagnostic: vanilla AssetBundleLoader.GetAllAssetPathsMappedToAssetID has 30 instructions, 1 call(s) to ...Dictionary...Add.
IL diagnostic: transpiler 1/2 (dev.hampus.asset-manager-crash-mre.simulated-stuward): applied OK; 0 call(s) to Dictionary.Add remain; callees introduced so far: ...SimulatedStuWardAssetPathsPatch.AddPath.
IL diagnostic: transpiler 2/2 (com.jotunn.jotunn): Jotunn's MatchForward(Calls Dictionary.Add) over its input IL -> IsValid=False, Pos=30, Length=30, Add calls in input=0.
IL diagnostic: transpiler 2/2 (com.jotunn.jotunn): applying it threw ArgumentOutOfRangeException: Index was out of range. ...
```

- Same failure as run 1, down to the IL diagnostic numbers (`IsValid=False, Pos=30, Length=30, Add calls in input=0`).
  The only difference is that the first transpiler owner is `dev.hampus.asset-manager-crash-mre.simulated-stuward`
  instead of `sighsorry.STUWard`.
- Don't enable this together with real STUWard; there is nothing to gain and it muddies the owner list.

### Reading the verdict

| Log line | Meaning |
| --- | --- |
| `NOT REPRODUCED: ... Jotunn's AssetManager initialised.` | Jotunn's transpiler applied cleanly. |
| `REPRODUCED: ...` + `Root cause: ArgumentOutOfRangeException` | Jotunn's transpiler found no `Dictionary<string,AssetID>.Add` call because another patch had already rewritten it. |
| `IL diagnostic: ... IsValid=True, ... Add calls in input=1` | Jotunn's `MatchForward` found the `Add` call in the IL it was handed (control run). |
| `IL diagnostic: ... IsValid=False, ... Add calls in input=0` | Another transpiler ran first and removed the `Add` call, so Jotunn's match is invalid and `SetInstruction` throws. |
| Neither line appears | You never loaded into a world (the check waits for `Player.m_localPlayer`), or the plugin isn't loading (check Jotunn is installed and the plugin is in `BepInEx/plugins`). |

## Testing a fix (Jotunn with a validity check)

A candidate fix for Jotunn lives on the branch
[`fix/assetmanager-transpiler-isvalid-guard`](https://github.com/Hampus-Toft/Jotunn/tree/fix/assetmanager-transpiler-isvalid-guard)
of a personal fork (based on the v2.30.1 release commit). It keeps the `MatchForward` as is, and if the match is invalid it
logs a warning and returns the instructions unchanged instead of calling `SetInstruction`. The same two profiles as runs
1 and 2 were repeated with a Jotunn 2.30.1 build from that branch (`Jotunn.dll`, `Jotunn.pdb` and `Jotunn.dll.mdb` from the
build replaced the ones in the profile; nothing else changed):

| Run | Profile | Result | Log |
| --- | --- | --- | --- |
| 4 | patched Jotunn + STUWard 1.3.15 + plugin (`SimulateStuWardPatch=false`) | No crash. `AssetManager` and `PrefabManager` initialise, verdict `NOT REPRODUCED` (`GetSprite` returned `button(Clone)`). One `Could not find Dictionary.Add ...` warning on init, plus one more when the plugin's IL diagnostic replays the chain. | [`run4`](logs/run4-with-stuward-patched-jotunn.log) |
| 5 | patched Jotunn + plugin, no STUWard (control) | Unchanged normal path: `IsValid=True, Pos=17, Add calls in input=1`, `AddSafe` applied, no warning. | [`run5`](logs/run5-control-patched-jotunn.log) |

To repeat it: build Jotunn from that branch (`dotnet build JotunnLib/JotunnLib.csproj -c Release`, with `VALHEIM_INSTALL`
set and `-p:SolutionDir=...\`; the final NuGet pack step may fail, `JotunnLib\bin\Release\net462\Jotunn.dll` is still
produced), copy `Jotunn.dll`, `Jotunn.pdb` and `Jotunn.dll.mdb` from that folder over the ones in the profile's
`plugins\ValheimModding-Jotunn` folder, and run 1 and 2 above again. In run 4 the plugin's own
`MatchForward(...) -> IsValid=False` line is still printed, because it runs its own matcher over the IL Jotunn receives.

## Game-free proof of the ordering

`HarmonyOrderProof/` is a small console app (HarmonyX 2.16.1 from NuGet, no Valheim install needed) that shows the
order dependence outside the game. It patches a stand-in method with one `Dictionary.Add` call using a STUWard-shaped and
a Jotunn-shaped transpiler, in both orders. Run `dotnet run -c Release` in that folder; the captured output is in
`logs/harmony-order-proof-output.txt`.

| Scenario | Result |
| --- | --- |
| A: Jotunn only (control) | Patch applies; safe add active. |
| B: STUWard first, then Jotunn (as in the game) | Jotunn's patch fails: `MatchForward` gives `IsValid=False` with 0 `Add` calls in its input. |
| C: Jotunn first, then STUWard | Both apply; STUWard's transpiler finds nothing to rewrite. No error. |

The exception type differs between the two setups: this console app throws `InvalidOperationException` ("Cannot set
instruction/opcode at invalid position") from the NuGet HarmonyX, while the game's bundled HarmonyX throws
`ArgumentOutOfRangeException` from the same `CodeMatcher.SetInstruction` call. Same cause (an invalid matcher position),
different exception in different HarmonyX builds.

## Build

```
dotnet build -c Release
```

Needs a Valheim install with Jotunn 2.30.0 or newer (2.30.1 tested) at `BepInEx\plugins\Jotunn\Jotunn.dll`. Override with
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
user's BepInEx logs, Jotunn's v2.30.0 source, and the decompiled STUWard DLL (later checked against STUWard's public
source at 1.3.15). The ordering behaviour was also confirmed outside the game against a dummy method, see
[Game-free proof of the ordering](#game-free-proof-of-the-ordering).
