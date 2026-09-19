using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using SoftReferenceableAssets;
using UnityEngine;

namespace AssetManagerCrashMRE
{
    /// <summary>
    /// Asset Manager Crash (MRE) - minimal reproducible example: Jotunn's AssetManager transpiler on
    /// AssetBundleLoader.GetAllAssetPathsMappedToAssetID throws ArgumentOutOfRangeException when
    /// another mod has already rewritten the Dictionary&lt;string, AssetID&gt;.Add() call it searches for.
    ///
    /// The plugin does three things:
    ///   1. (optional) Awake: applies an equivalent of STUWard >= 1.3.11's AssetPathsPatch transpiler,
    ///      so the conflict can be shown without STUWard installed. Off by default so the real
    ///      STUWard can be tested instead.
    ///   2. Update: once in-game, calls GUIManager.Instance.GetSprite(). Jotunn's managers are lazy:
    ///      GUIManager's static constructor runs InitializeAssets, which calls
    ///      PrefabManager.Cache.GetPrefab, which runs AssetManager's static constructor, which applies
    ///      the Harmony patch that crashes. Nothing triggers the crash unless some mod touches Jotunn's
    ///      GUI/prefab APIs; this call stands in for "any mod that uses Jotunn".
    ///   3. Logs which Harmony transpilers are registered on the target method and, step by step,
    ///      what the method's IL looks like after each of them (see LogIlDiagnostics).
    /// </summary>
    [BepInPlugin(PluginGuid, "Asset Manager Crash (MRE)", "1.1.0")]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class AssetManagerCrashMrePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.hampus.asset-manager-crash-mre";

        private ConfigEntry<bool> simulateStuWardPatch;
        private bool triggered;

        private void Awake()
        {
            simulateStuWardPatch = Config.Bind(
                "Repro",
                "SimulateStuWardPatch",
                false,
                "Apply a copy of STUWard's AssetPathsPatch at plugin load (same timing as STUWard's Plugin.Awake) " +
                "so the conflict reproduces without STUWard installed. Leave false when testing with real STUWard.");

            if (simulateStuWardPatch.Value)
            {
                new Harmony(PluginGuid + ".simulated-stuward").PatchAll(typeof(SimulatedStuWardAssetPathsPatch));
                Logger.LogInfo("SimulateStuWardPatch=true: applied simulated STUWard AssetPathsPatch.");
            }
            else
            {
                Logger.LogInfo("SimulateStuWardPatch=false: no simulated patch applied (relying on other installed mods).");
            }
        }

        private void Update()
        {
            if (triggered || Player.m_localPlayer == null)
            {
                return;
            }

            triggered = true;

            try
            {
                Sprite sprite = GUIManager.Instance.GetSprite("button");
                Logger.LogInfo($"NOT REPRODUCED: GUIManager.GetSprite returned '{(sprite != null ? sprite.name : "null")}'; Jotunn's AssetManager initialised.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"REPRODUCED: GUIManager.GetSprite threw {ex.GetType().Name}: {ex.Message}");
                Logger.LogError($"Root cause: {Root(ex).GetType().Name}: {Root(ex).Message}");
            }
            finally
            {
                LogTranspilerOwners();
                LogIlDiagnostics();
            }
        }

        private void LogTranspilerOwners()
        {
            MethodBase target = SimulatedStuWardAssetPathsPatch.Target();
            Patches info = Harmony.GetPatchInfo(target);
            string owners = info == null || info.Transpilers.Count == 0
                ? "(none)"
                : string.Join(", ", info.Transpilers.Select(t => $"{t.owner} [{t.PatchMethod.DeclaringType?.FullName}.{t.PatchMethod.Name}] (priority {t.priority})"));
            Logger.LogInfo($"Harmony transpilers currently registered on {target.DeclaringType?.Name}.{target.Name}: {owners}");
        }

        /// <summary>
        /// Shows directly why Jotunn's transpiler fails: replays the transpiler chain one step at a time
        /// (PatchProcessor.GetCurrentInstructions with maxTranspilers = 1, 2, ...) and logs, after each step,
        /// how many calls to Dictionary&lt;string, AssetID&gt;.Add are left in the IL and which methods replaced
        /// them. For Jotunn's own step it also runs the same CodeMatcher.MatchForward Jotunn uses over the IL
        /// Jotunn receives, and reports whether the match is valid.
        /// Steps are ordered by priority (high first), then registration order; before/after constraints are not modelled.
        /// </summary>
        private void LogIlDiagnostics()
        {
            try
            {
                MethodBase target = SimulatedStuWardAssetPathsPatch.Target();
                MethodInfo add = AccessTools.DeclaredMethod(typeof(Dictionary<string, AssetID>), "Add");

                List<CodeInstruction> vanilla = GetOriginalInstructions(target);
                HashSet<MethodBase> vanillaCallees = new HashSet<MethodBase>(Callees(vanilla));
                Logger.LogInfo($"IL diagnostic: vanilla {target.DeclaringType?.Name}.{target.Name} has {vanilla.Count} instructions, " +
                               $"{vanilla.Count(i => i.Calls(add))} call(s) to {Describe(add)}.");

                Patches info = Harmony.GetPatchInfo(target);
                if (info == null || info.Transpilers.Count == 0)
                {
                    return;
                }

                List<Patch> chain = info.Transpilers.OrderByDescending(p => p.priority).ThenBy(p => p.index).ToList();
                List<CodeInstruction> input = vanilla;

                for (int step = 1; step <= chain.Count; step++)
                {
                    Patch patch = chain[step - 1];
                    string label = $"IL diagnostic: transpiler {step}/{chain.Count} ({patch.owner})";

                    if (patch.owner == Jotunn.Main.ModGuid)
                    {
                        // Same match Jotunn's AssetManager transpiler performs, over the IL it is handed at this step.
                        CodeMatcher matcher = NewCodeMatcher(input).MatchForward(false, new CodeMatch(i => i.Calls(add)));
                        Logger.LogInfo($"{label}: Jotunn's MatchForward(Calls Dictionary.Add) over its input IL -> " +
                                       $"IsValid={matcher.IsValid}, Pos={matcher.Pos}, Length={matcher.Length}, " +
                                       $"Add calls in input={input.Count(i => i.Calls(add))}.");
                    }

                    try
                    {
                        List<CodeInstruction> output = GetCurrentInstructions(target, step);
                        string introduced = string.Join(", ", Callees(output).Except(vanillaCallees).Distinct().Select(Describe));
                        Logger.LogInfo($"{label}: applied OK; {output.Count(i => i.Calls(add))} call(s) to Dictionary.Add remain; " +
                                       $"callees introduced so far: {(introduced.Length == 0 ? "(none)" : introduced)}.");
                        input = output;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogInfo($"{label}: applying it threw {Root(ex).GetType().Name}: {Root(ex).Message.Replace("\n", " ")}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"IL diagnostic failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 0Harmony's PatchProcessor/CodeMatcher members take an optional ILGenerator that 0Harmony references via mscorlib,
        // which a netstandard2.1 project cannot resolve at compile time. Call them through reflection instead.
        private static List<CodeInstruction> GetOriginalInstructions(MethodBase target)
        {
            MethodInfo method = typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "GetOriginalInstructions" && m.GetParameters().Length == 2 && !m.GetParameters()[1].ParameterType.IsByRef);
            return (List<CodeInstruction>)method.Invoke(null, new object[] { target, null });
        }

        private static List<CodeInstruction> GetCurrentInstructions(MethodBase target, int maxTranspilers)
        {
            MethodInfo method = typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "GetCurrentInstructions" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(int));
            return (List<CodeInstruction>)method.Invoke(null, new object[] { target, maxTranspilers, null });
        }

        private static CodeMatcher NewCodeMatcher(IEnumerable<CodeInstruction> instructions) =>
            (CodeMatcher)Activator.CreateInstance(typeof(CodeMatcher), new object[] { instructions, null });

        private static IEnumerable<MethodBase> Callees(IEnumerable<CodeInstruction> instructions) =>
            instructions
                .Where(i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt)
                .Select(i => i.operand as MethodBase)
                .Where(m => m != null);

        private static string Describe(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}";

        private static Exception Root(Exception ex)
        {
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            return ex;
        }

        [HarmonyPatch]
        internal static class SimulatedStuWardAssetPathsPatch
        {
            internal static MethodBase Target() =>
                AccessTools.DeclaredMethod(
                    typeof(Runtime).Assembly.GetType("SoftReferenceableAssets.AssetBundleLoader", true),
                    "GetAllAssetPathsMappedToAssetID");

            private static MethodBase TargetMethod() => Target();

            private static void AddPath(Dictionary<string, AssetID> paths, string path, AssetID id)
            {
                if (path != null && !paths.ContainsKey(path))
                {
                    paths.Add(path, id);
                }
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo add = AccessTools.DeclaredMethod(typeof(Dictionary<string, AssetID>), "Add");
                MethodInfo replacement = AccessTools.DeclaredMethod(typeof(SimulatedStuWardAssetPathsPatch), nameof(AddPath));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(add))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }

                    yield return instruction;
                }
            }
        }
    }
}
