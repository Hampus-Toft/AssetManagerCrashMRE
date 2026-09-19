// Game-free demonstration that two Harmony transpilers rewriting the same Dictionary.Add call are order dependent.
//
//   StuWardLike : tolerant rewrite of every Dictionary.Add call (same shape as STUWard's WardUiResources.AssetPathsPatch).
//   JotunnLike  : MatchForward(Calls Add).SetInstruction(...) with no validity check (same shape as
//                 Jotunn.Managers.AssetManager.Patches.AssetBundleLoader_GetAllAssetPathsMappedToAssetID).
//   Target.Build: stand-in for the vanilla AssetBundleLoader.GetAllAssetPathsMappedToAssetID (one Dictionary.Add call).
//
// Run with: dotnet run -c Release      (uses the HarmonyX NuGet package, no Valheim install needed)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

// Stand-in for AssetBundleLoader.GetAllAssetPathsMappedToAssetID: builds a dictionary with Dictionary<string,int>.Add.
public static class Target
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Dictionary<string, int> Build(string[] keys)
    {
        var d = new Dictionary<string, int>();
        foreach (var k in keys) d.Add(k, 1);   // throws on duplicate keys when unpatched
        return d;
    }
}

// STUWard-shaped: tolerant rewrite of every Dictionary.Add call.
public static class StuWardLike
{
    public static void AddPath(Dictionary<string, int> d, string k, int v) { if (k != null && !d.ContainsKey(k)) d.Add(k, v); }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var add = AccessTools.DeclaredMethod(typeof(Dictionary<string, int>), "Add");
        var rep = AccessTools.DeclaredMethod(typeof(StuWardLike), nameof(AddPath));
        foreach (var i in ins) { if (i.Calls(add)) { i.opcode = OpCodes.Call; i.operand = rep; } yield return i; }
    }
}

// Jotunn-shaped: MatchForward + SetInstruction, no validity check (same as AssetManager.cs:94-100).
public static class JotunnLike
{
    public static void AddSafe(Dictionary<string, int> d, string k, int v) { if (k != null && !d.ContainsKey(k)) d.Add(k, v); }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins)
    {
        var add = AccessTools.Method(typeof(Dictionary<string, int>), "Add");
        var safe = AccessTools.Method(typeof(JotunnLike), nameof(AddSafe));
        return new CodeMatcher(ins).MatchForward(false, new CodeMatch(i => i.Calls(add)))
            .SetInstruction(new CodeInstruction(OpCodes.Call, safe)).InstructionEnumeration();
    }
}

public static class Diag
{
    // Identical logic to LogIlDiagnostics in the plugin (reflection helpers because of ILGenerator/mscorlib).
    static List<CodeInstruction> GetOriginalInstructions(MethodBase t) =>
        (List<CodeInstruction>)typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "GetOriginalInstructions" && m.GetParameters().Length == 2 && !m.GetParameters()[1].ParameterType.IsByRef)
            .Invoke(null, new object[] { t, null });
    static List<CodeInstruction> GetCurrentInstructions(MethodBase t, int n) =>
        (List<CodeInstruction>)typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "GetCurrentInstructions" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(int))
            .Invoke(null, new object[] { t, n, null });
    static CodeMatcher NewCodeMatcher(IEnumerable<CodeInstruction> i) => (CodeMatcher)Activator.CreateInstance(typeof(CodeMatcher), new object[] { i, null });
    static IEnumerable<MethodBase> Callees(IEnumerable<CodeInstruction> ins) =>
        ins.Where(i => i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt).Select(i => i.operand as MethodBase).Where(m => m != null);
    static string Describe(MethodBase m) => $"{m.DeclaringType?.Name}.{m.Name}";
    static Exception Root(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }

    public static void Run(string jotunnOwner)
    {
        MethodBase target = AccessTools.DeclaredMethod(typeof(Target), "Build");
        MethodInfo add = AccessTools.DeclaredMethod(typeof(Dictionary<string, int>), "Add");
        var vanilla = GetOriginalInstructions(target);
        var vanillaCallees = new HashSet<MethodBase>(Callees(vanilla));
        Console.WriteLine($"  vanilla: {vanilla.Count} instructions, {vanilla.Count(i => i.Calls(add))} call(s) to {Describe(add)}");
        var info = Harmony.GetPatchInfo(target);
        if (info == null || info.Transpilers.Count == 0) return;
        var chain = info.Transpilers.OrderByDescending(p => p.priority).ThenBy(p => p.index).ToList();
        var input = vanilla;
        for (int step = 1; step <= chain.Count; step++)
        {
            var patch = chain[step - 1];
            string label = $"  transpiler {step}/{chain.Count} ({patch.owner})";
            if (patch.owner == jotunnOwner)
            {
                var m = NewCodeMatcher(input).MatchForward(false, new CodeMatch(i => i.Calls(add)));
                Console.WriteLine($"{label}: Jotunn MatchForward over its input -> IsValid={m.IsValid}, Pos={m.Pos}, Length={m.Length}, Add calls in input={input.Count(i => i.Calls(add))}");
            }
            try
            {
                var output = GetCurrentInstructions(target, step);
                string introduced = string.Join(", ", Callees(output).Except(vanillaCallees).Distinct().Select(Describe));
                Console.WriteLine($"{label}: applied OK; {output.Count(i => i.Calls(add))} Add call(s) remain; introduced: {(introduced.Length == 0 ? "(none)" : introduced)}");
                input = output;
            }
            catch (Exception ex) { Console.WriteLine($"{label}: applying it threw {Root(ex).GetType().Name}: {Root(ex).Message.Replace("\n", " ")}"); break; }
        }
    }
}

public static class Program
{
    static Exception RootOf(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }

    static void Scenario(string name, params string[] order)
    {
        Console.WriteLine($"=== {name} ===");
        var target = AccessTools.DeclaredMethod(typeof(Target), "Build");
        var patched = new List<Harmony>();
        foreach (var who in order)
        {
            var h = new Harmony(who);
            var tp = who == "com.jotunn.jotunn" ? typeof(JotunnLike) : typeof(StuWardLike);
            try { h.Patch(target, transpiler: new HarmonyMethod(tp.GetMethod("Transpiler"))); Console.WriteLine($"  patch by {who}: OK"); }
            catch (Exception ex) { Console.WriteLine($"  patch by {who}: FAILED ({RootOf(ex).GetType().Name})"); }
            patched.Add(h);
        }
        try { var d = Target.Build(new[] { "a", "a", "b" }); Console.WriteLine($"  Target.Build with duplicate keys returned {d.Count} entries (safe add active)"); }
        catch (Exception ex) { Console.WriteLine($"  Target.Build with duplicate keys threw {ex.GetType().Name} (safe add NOT active)"); }
        Diag.Run("com.jotunn.jotunn");
        foreach (var h in patched) h.UnpatchSelf();
    }

    public static void Main()
    {
        Scenario("A: Jotunn only (control)", "com.jotunn.jotunn");
        Scenario("B: STUWard first, then Jotunn (as in the game)", "sighsorry.STUWard", "com.jotunn.jotunn");
        Scenario("C: Jotunn first, then STUWard", "com.jotunn.jotunn", "sighsorry.STUWard");
    }
}
