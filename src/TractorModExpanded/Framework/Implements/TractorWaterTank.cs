using StardewValley.Tools;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>The watering can used internally by a water tank implement, which tracks the tank's own water level.</summary>
/// <remarks>This is never added to an inventory or saved; the water level is stored in the tractor's mod data.</remarks>
internal sealed class TractorWaterTank : WateringCan
{
    /// <summary>The number of tiles the tank can still water.</summary>
    public int Water { get; set; }

    /// <summary>The number of tiles a full tank can water.</summary>
    public int Capacity { get; set; }

    /// <summary>Whether the tank never runs out of water.</summary>
    public bool Infinite { get; set; }

    /// <summary>Whether the tank has water left.</summary>
    public bool HasWater => this.Infinite || this.Water > 0;

    /// <summary>Construct an instance.</summary>
    public TractorWaterTank()
    {
        this.IsBottomless = true; // the vanilla water counter isn't used
        this.IsEfficient = true; // no stamina drain
    }
}
