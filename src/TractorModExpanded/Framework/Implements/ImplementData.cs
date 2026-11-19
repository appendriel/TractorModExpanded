using System.Collections.Generic;
using System.Linq;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>What a tractor implement does.</summary>
internal enum ImplementKind
{
    Plow,
    Seeder,
    WateringTank,
    Pickaxe,
    WoodCutter,
    HarvesterProduce,
    HarvesterGrain,
    HarvesterBush,
    HarvesterFruitTree,
    Mower
}

/// <summary>The static definition for a tractor implement item.</summary>
/// <param name="Id">The unqualified item ID in <c>Data/Objects</c>.</param>
/// <param name="Kind">What the implement does.</param>
/// <param name="Tier">The implement level (1 for implements without levels).</param>
/// <param name="SpriteIndex">The sprite index in the implements spritesheet.</param>
/// <param name="Shop">The shop ID in <c>Data/Shops</c> which sells it.</param>
/// <param name="UpgradesFrom">The unqualified item ID of the lower-tier implement required to buy it, if any.</param>
internal record ImplementDefinition(string Id, ImplementKind Kind, int Tier, int SpriteIndex, string Shop, string? UpgradesFrom)
{
    /// <summary>The qualified item ID.</summary>
    public string QualifiedId => "(O)" + this.Id;

    /// <summary>The translation key prefix.</summary>
    public string TranslationKey => "implement." + this.Id.Substring(ImplementData.IdPrefix.Length).ToLowerInvariant();
}

/// <summary>The tractor implement definitions.</summary>
internal static class ImplementData
{
    /// <summary>The prefix for implement item IDs.</summary>
    // kept from the mod's earlier name (Tractor Mod Full) on purpose: implements already in saves use these item IDs
    public const string IdPrefix = "TractorModFull_";

    /// <summary>The <c>Horse.modData</c> key which stores the qualified ID of the equipped implement.</summary>
    public const string EquippedDataKey = "Pathoschild.TractorMod/Implement";

    /// <summary>The <c>Horse.modData</c> key which stores the water left in an equipped water tank.</summary>
    public const string WaterDataKey = "Pathoschild.TractorMod/Water";

    /// <summary>The <c>Horse.modData</c> key which stores the seeder hopper contents.</summary>
    public const string HopperDataKey = "Pathoschild.TractorMod/Hopper";

    /// <summary>The shop ID for Robin.</summary>
    private const string Robin = "Carpenter";

    /// <summary>The shop ID for Clint's "upgrade tools" menu (implement level upgrades are sold there, not in his regular shop).</summary>
    private const string Clint = "ClintUpgrade";

    /// <summary>All implement definitions.</summary>
    public static readonly ImplementDefinition[] All =
    [
        new(IdPrefix + "Plow", ImplementKind.Plow, 1, 0, Robin, null),
        new(IdPrefix + "Seeder", ImplementKind.Seeder, 1, 1, Robin, null),
        new(IdPrefix + "WateringTank1", ImplementKind.WateringTank, 1, 2, Robin, null),
        new(IdPrefix + "WateringTank2", ImplementKind.WateringTank, 2, 3, Clint, IdPrefix + "WateringTank1"),
        new(IdPrefix + "WateringTank3", ImplementKind.WateringTank, 3, 4, Clint, IdPrefix + "WateringTank2"),
        new(IdPrefix + "Pickaxe1", ImplementKind.Pickaxe, 1, 5, Robin, null),
        new(IdPrefix + "Pickaxe2", ImplementKind.Pickaxe, 2, 6, Clint, IdPrefix + "Pickaxe1"),
        new(IdPrefix + "WoodCutter1", ImplementKind.WoodCutter, 1, 7, Robin, null),
        new(IdPrefix + "WoodCutter2", ImplementKind.WoodCutter, 2, 8, Clint, IdPrefix + "WoodCutter1"),
        new(IdPrefix + "HarvesterProduce", ImplementKind.HarvesterProduce, 1, 9, Robin, null),
        new(IdPrefix + "HarvesterGrain", ImplementKind.HarvesterGrain, 1, 10, Robin, null),
        new(IdPrefix + "HarvesterBush", ImplementKind.HarvesterBush, 1, 11, Robin, null),
        new(IdPrefix + "HarvesterFruitTree", ImplementKind.HarvesterFruitTree, 1, 12, Robin, null),
        new(IdPrefix + "Mower", ImplementKind.Mower, 1, 13, Robin, null)
    ];

    /// <summary>The default price and materials for each implement, indexed by unqualified item ID.</summary>
    public static Dictionary<string, ImplementPriceConfig> GetDefaultPrices()
    {
        const string copperBar = "(O)334", ironBar = "(O)335", goldBar = "(O)336", iridiumBar = "(O)337";

        return new Dictionary<string, ImplementPriceConfig>
        {
            [IdPrefix + "Plow"] = new(10_000, ironBar, 10),
            [IdPrefix + "Seeder"] = new(25_000, ironBar, 10),
            [IdPrefix + "WateringTank1"] = new(15_000, copperBar, 10),
            [IdPrefix + "WateringTank2"] = new(30_000, goldBar, 10),
            [IdPrefix + "WateringTank3"] = new(60_000, iridiumBar, 5),
            [IdPrefix + "Pickaxe1"] = new(15_000, ironBar, 10),
            [IdPrefix + "Pickaxe2"] = new(40_000, goldBar, 5),
            [IdPrefix + "WoodCutter1"] = new(15_000, ironBar, 10),
            [IdPrefix + "WoodCutter2"] = new(40_000, goldBar, 5),
            [IdPrefix + "HarvesterProduce"] = new(20_000, goldBar, 5),
            [IdPrefix + "HarvesterGrain"] = new(20_000, goldBar, 5),
            [IdPrefix + "HarvesterBush"] = new(15_000, goldBar, 5),
            [IdPrefix + "HarvesterFruitTree"] = new(25_000, goldBar, 5),
            [IdPrefix + "Mower"] = new(5_000, copperBar, 5)
        };
    }

    /// <summary>Extra harvest item IDs that count as grain, besides items with the grain_item, rice_item or wheat_item context tags. Set from the config.</summary>
    public static HashSet<string> ExtraGrainItems { get; set; } = [];

    /// <summary>Cached grain checks by harvest item ID.</summary>
    private static readonly Dictionary<string, bool> GrainCache = [];

    /// <summary>The base game's grains (wheat, corn, unmilled rice, amaranth). Always grain, whatever the config, since their items don't have a grain context tag in the base game.</summary>
    public static readonly HashSet<string> VanillaGrainItems = ["(O)262", "(O)270", "(O)271", "(O)300"];

    /// <summary>The default extra grain items (grains whose items have no grain context tag).</summary>
    public static string[] DefaultExtraGrainItems => ["(O)271", "(O)BFV.FruitVeggie_Blue_Corn", "(O)BFV.FruitVeggie_Oats"];

    /// <summary>Get whether a crop is a grain (for the grain harvester) rather than produce.</summary>
    /// <param name="crop">The crop to check.</param>
    public static bool IsGrainCrop(StardewValley.Crop crop)
    {
        string? harvestId = crop.indexOfHarvest.Value;
        if (string.IsNullOrWhiteSpace(harvestId))
            return false;

        string qualifiedId = StardewValley.ItemRegistry.QualifyItemId(harvestId) ?? harvestId;
        if (!GrainCache.TryGetValue(qualifiedId, out bool isGrain))
        {
            isGrain =
                VanillaGrainItems.Contains(qualifiedId)
                || ExtraGrainItems.Contains(qualifiedId)
                || StardewValley.ItemContextTagManager.HasBaseTag(qualifiedId, "grain_item")
                || StardewValley.ItemContextTagManager.HasBaseTag(qualifiedId, "rice_item")
                || StardewValley.ItemContextTagManager.HasBaseTag(qualifiedId, "wheat_item");
            GrainCache[qualifiedId] = isGrain;
        }
        return isGrain;
    }

    /// <summary>Clear the grain cache after the config or data changes.</summary>
    public static void ClearGrainCache()
    {
        GrainCache.Clear();
    }

    /// <summary>Get the qualified item ID for an implement ID suffix like <c>Plow</c>.</summary>
    /// <param name="suffix">The ID without the <see cref="IdPrefix"/>.</param>
    public static string QualifiedIdFor(string suffix)
    {
        return "(O)" + IdPrefix + suffix;
    }

    /// <summary>Get an implement definition by its qualified or unqualified item ID.</summary>
    /// <param name="itemId">The item ID.</param>
    public static ImplementDefinition? Get(string? itemId)
    {
        if (itemId is null)
            return null;
        if (itemId.StartsWith("(O)"))
            itemId = itemId.Substring(3);
        return All.FirstOrDefault(p => p.Id == itemId);
    }
}

/// <summary>The configurable price for an implement.</summary>
/// <param name="Price">The gold price.</param>
/// <param name="MaterialId">The qualified item ID for the required material, if any.</param>
/// <param name="MaterialAmount">The number of materials required.</param>
internal record ImplementPriceConfig(int Price, string? MaterialId, int MaterialAmount);
