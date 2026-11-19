using Microsoft.Xna.Framework;
using Pathoschild.Stardew.TractorMod.Framework.Config;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.TractorMod.Framework.Attachments;

/// <summary>An attachment for the watering can.</summary>
internal class WateringCanAttachment : BaseAttachment
{
    /*********
    ** Fields
    *********/
    /// <summary>The attachment settings.</summary>
    private readonly GenericAttachmentConfig Config;

    /// <summary>An infinite watering can to apply.</summary>
    private readonly WateringCan WateringCan = new()
    {
        IsBottomless = true, // no water drain
        IsEfficient = true, // no stamina drain
        WaterLeft = 100
    };


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="config">The attachment settings.</param>
    /// <param name="modRegistry">Fetches metadata about loaded mods.</param>
    public WateringCanAttachment(GenericAttachmentConfig config, IModRegistry modRegistry)
        : base(modRegistry)
    {
        this.Config = config;
    }

    /// <inheritdoc />
    public override bool IsEnabled(Farmer player, Tool? tool, Item? item, GameLocation location)
    {
        return
            this.Config.Enable
            && tool is WateringCan
            && (tool is not Implements.TractorWaterTank tank || tank.HasWater); // TractorMod Expanded: tank must have water
    }

    /// <inheritdoc />
    /// <remarks>Volcano logic derived from <see cref="VolcanoDungeon.performToolAction"/>.</remarks>
    public override bool Apply(Vector2 tile, SObject? tileObj, TerrainFeature? tileFeature, Farmer player, Tool? tool, Item? item, GameLocation location)
    {
        // water dirt
        if (this.TryGetHoeDirt(tileFeature, tileObj, out HoeDirt? dirt, out _, out _) && dirt.state.Value != HoeDirt.watered)
            return this.UseTankWater(tool) && this.UseWateringCanOnTile(tile, player, tool, location);

        // cool lava
        int x = (int)tile.X;
        int y = (int)tile.Y;
        if (location is VolcanoDungeon dungeon && dungeon.isTileOnMap(x, y) && dungeon.waterTiles[x, y] && !dungeon.cooledLavaTiles.ContainsKey(tile))
            return this.UseTankWater(tool) && this.UseWateringCanOnTile(tile, player, tool, location);

        return false;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Use one tile of water from the tractor's water tank, if the tool is one.</summary>
    /// <param name="tool">The tool passed to the attachment.</param>
    /// <returns>Returns whether there was water to use.</returns>
    private bool UseTankWater(Tool? tool)
    {
        if (tool is not Implements.TractorWaterTank tank || tank.Infinite)
            return true;
        if (tank.Water <= 0)
            return false;

        tank.Water--;
        return true;
    }

    /// <summary>Use the watering can on a tile.</summary>
    /// <param name="tile">The tile to affect.</param>
    /// <param name="player">The current player.</param>
    /// <param name="location">The current location.</param>
    /// <returns>Returns <c>true</c> for convenience when implementing tools.</returns>
    /// <param name="tool">The tool passed to the attachment.</param>
    private bool UseWateringCanOnTile(Vector2 tile, Farmer player, Tool? tool, GameLocation location)
    {
        if (this.UseToolOnTile(this.WateringCan, tile, player, location))
        {
            if (tool?.PlayUseSounds is true)
                location.localSound("wateringCan"); // normally played in Tool.endUsing
            return true;
        }

        return false;
    }
}
