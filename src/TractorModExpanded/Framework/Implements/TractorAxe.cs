using StardewValley.Tools;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>The axe used internally by the wood cutter implement, so the axe attachment can apply the wood cutter's own rules.</summary>
/// <remarks>Wood cutter rules: cut wild trees at every growth stage (not tapped ones) and tree stumps; never fruit trees. Large stumps and hollow logs depend on the upgrade level.</remarks>
internal sealed class TractorAxe : Axe
{
    /// <summary>Construct an instance.</summary>
    /// <param name="upgradeLevel">The axe upgrade level (0 = level 1 cutter: no large stumps or logs; 3 = heavy cutter: everything).</param>
    public TractorAxe(int upgradeLevel)
    {
        this.UpgradeLevel = upgradeLevel;
    }
}
