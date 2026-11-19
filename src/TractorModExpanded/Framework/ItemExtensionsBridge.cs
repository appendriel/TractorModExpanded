using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;

namespace Pathoschild.Stardew.TractorMod.Framework;

/// <summary>Keeps the Item Extensions integration with TractorMod Expanded's own mod ID.</summary>
/// <remarks>
/// Item Extensions only patches the tractor when a mod with the ID <c>Pathoschild.TractorMod</c> is loaded. Its patches target
/// Tractor Mod's classes by name (<c>Pathoschild.Stardew.TractorMod.Framework.TractorManager</c>, <c>...Attachments.SeedAttachment</c>),
/// which TractorMod Expanded keeps, so we ask Item Extensions to apply them through its public <c>TractorModPatches.Apply(Harmony)</c>.
/// </remarks>
internal static class ItemExtensionsBridge
{
    /// <summary>The Item Extensions mod ID.</summary>
    public const string ModId = "mistyspring.ItemExtensions";

    /// <summary>Whether the patches were applied by this bridge.</summary>
    public static bool Applied { get; private set; }

    /// <summary>Apply Item Extensions' tractor patches, if it's installed and didn't already apply them.</summary>
    /// <param name="modRegistry">Fetches metadata about loaded mods.</param>
    /// <param name="monitor">Writes to the SMAPI log.</param>
    public static void TryApply(IModRegistry modRegistry, IMonitor monitor)
    {
        if (!modRegistry.IsLoaded(ModId) || modRegistry.IsLoaded("Pathoschild.TractorMod"))
            return; // not installed, or it already patched the original Tractor Mod

        try
        {
            Type? patches = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(p => p.GetName().Name == "ItemExtensions"))
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                patches = types.FirstOrDefault(t => t.FullName == "ItemExtensions.Patches.Mods.TractorModPatches");
                if (patches != null)
                    break;
            }
            MethodInfo? apply = patches?
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Apply" && m.GetParameters() is [{ ParameterType.Name: "Harmony" }]);
            if (apply == null)
            {
                monitor.Log("Item Extensions is installed, but its tractor integration wasn't found (it may have changed). Its custom seeds and resources won't work with the tractor.", LogLevel.Warn);
                return;
            }

            apply.Invoke(null, [new Harmony("appendriel.TractorModExpanded.ItemExtensions")]);
            Applied = true;
            monitor.Log("Applied Item Extensions' tractor integration.");
        }
        catch (Exception ex)
        {
            monitor.Log($"Couldn't apply Item Extensions' tractor integration: {ex}", LogLevel.Warn);
        }
    }
}
