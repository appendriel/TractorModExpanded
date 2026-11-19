using StardewValley.Tools;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>The scythe used internally by harvester and mower implements, which limits what the scythe attachment may harvest.</summary>
/// <remarks>This is never added to an inventory or saved.</remarks>
internal sealed class TractorScythe : MeleeWeapon
{
    /// <summary>Which implement this scythe represents.</summary>
    public ImplementKind Mode { get; }

    /// <summary>Construct an instance.</summary>
    /// <param name="mode">Which implement this scythe represents.</param>
    public TractorScythe(ImplementKind mode)
        : base(MeleeWeapon.scytheId)
    {
        this.Mode = mode;
    }
}
