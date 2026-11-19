using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;
using StardewValley.GameData.Objects;
using StardewValley.GameData.Shops;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>Registers the implement items and shop entries, and tracks which implement is equipped on each tractor.</summary>
internal sealed class ImplementManager
{
    /*********
    ** Fields
    *********/
    /// <summary>The asset name for the implements spritesheet.</summary>
    private readonly string TextureAssetName;

    /// <summary>The mod's internal asset name for the implements spritesheet file.</summary>
    private readonly string TexturePath;

    /// <summary>Loads mod files.</summary>
    private readonly IModContentHelper ModContent;

    /// <summary>Provides translations.</summary>
    private readonly ITranslationHelper Translation;

    /// <summary>Get the current implement prices.</summary>
    private readonly System.Func<Dictionary<string, ImplementPriceConfig>> GetPrices;

    /// <summary>Get the tank capacity for each level.</summary>
    private readonly System.Func<int[]> GetTankCapacities;

    /// <summary>Get whether water tanks never run out.</summary>
    private readonly System.Func<bool> GetInfiniteWater;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="publicAssetBasePath">The base path for public assets.</param>
    /// <param name="modContent">Loads mod files.</param>
    /// <param name="translation">Provides translations.</param>
    /// <param name="getPrices">Get the current implement prices.</param>
    /// <param name="getTankCapacities">Get the tank capacity for each level.</param>
    /// <param name="getInfiniteWater">Get whether water tanks never run out.</param>
    public ImplementManager(string publicAssetBasePath, IModContentHelper modContent, ITranslationHelper translation, System.Func<Dictionary<string, ImplementPriceConfig>> getPrices, System.Func<int[]> getTankCapacities, System.Func<bool> getInfiniteWater)
    {
        this.GetTankCapacities = getTankCapacities;
        this.GetInfiniteWater = getInfiniteWater;
        this.TextureAssetName = $"{publicAssetBasePath}/Implements";
        this.TexturePath = "assets/implements.png";
        this.ModContent = modContent;
        this.Translation = translation;
        this.GetPrices = getPrices;
    }

    /// <inheritdoc cref="IContentEvents.AssetRequested" />
    public void OnAssetRequested(AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(ImplementEffects.TextureAssetName))
            e.LoadFrom(() => this.ModContent.Load<Microsoft.Xna.Framework.Graphics.Texture2D>("assets/effects.png"), AssetLoadPriority.Exclusive);

        else if (e.NameWithoutLocale.IsEquivalentTo(this.TextureAssetName))
            e.LoadFrom(() => this.ModContent.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(this.TexturePath), AssetLoadPriority.Exclusive);

        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(editor =>
            {
                var data = editor.AsDictionary<string, ObjectData>().Data;
                var prices = this.GetPrices();
                foreach (ImplementDefinition implement in ImplementData.All)
                {
                    int price = prices.TryGetValue(implement.Id, out ImplementPriceConfig? config) ? config.Price : 0;
                    data[implement.Id] = new ObjectData
                    {
                        Name = implement.Id,
                        DisplayName = this.Translation.Get($"{implement.TranslationKey}.name"),
                        Description = this.Translation.Get($"{implement.TranslationKey}.description"),
                        Type = "Basic",
                        Category = 0,
                        Price = price / 2,
                        Texture = this.TextureAssetName,
                        SpriteIndex = implement.SpriteIndex,
                        Edibility = -300,
                        ExcludeFromRandomSale = true,
                        ExcludeFromFishingCollection = true,
                        ExcludeFromShippingCollection = true,
                        // 'cssm_tab_*' puts the implements in their own tab of Robin's shop with Compact Searchable Shop Menu (documented by that mod)
                        ContextTags = ["not_giftable", "not_placeable", "tractor_implement", "cssm_tab_tractor_implements"]
                    };
                }
            });
        }

        else if (e.NameWithoutLocale.IsEquivalentTo("Data/Shops"))
        {
            e.Edit(editor =>
            {
                var data = editor.AsDictionary<string, ShopData>().Data;
                var prices = this.GetPrices();
                foreach (ImplementDefinition implement in ImplementData.All)
                {
                    if (!data.TryGetValue(implement.Shop, out ShopData? shop) || !prices.TryGetValue(implement.Id, out ImplementPriceConfig? price))
                        continue;

                    ShopItemData entry = new()
                    {
                        Id = implement.Id,
                        ItemId = implement.QualifiedId,
                        Price = price.Price,
                        IgnoreShopPriceModifiers = true,
                        AvoidRepeat = true
                    };
                    if (price.MaterialId != null && price.MaterialAmount > 0)
                    {
                        entry.TradeItemId = price.MaterialId;
                        entry.TradeItemAmount = price.MaterialAmount;
                    }
                    if (implement.UpgradesFrom != null)
                    {
                        // the previous level must be in the inventory, and is exchanged for the new one
                        entry.Condition = $"PLAYER_HAS_ITEM Current (O){implement.UpgradesFrom}";
                        entry.ActionsOnPurchase = [$"RemoveItem (O){implement.UpgradesFrom} 1"];
                    }

                    shop.Items.RemoveAll(p => p.Id == entry.Id);
                    shop.Items.Add(entry);
                }
            });
        }
    }

    /// <summary>Get whether an item is a tractor implement.</summary>
    /// <param name="item">The item to check.</param>
    public static bool IsImplement(Item? item)
    {
        return item != null && ImplementData.Get(item.QualifiedItemId) != null;
    }

    /// <summary>Get the implement equipped on a tractor, if any.</summary>
    /// <param name="tractor">The tractor to check.</param>
    public static ImplementDefinition? GetEquipped(Horse? tractor)
    {
        return tractor != null && tractor.modData.TryGetValue(ImplementData.EquippedDataKey, out string? id)
            ? ImplementData.Get(id)
            : null;
    }

    /// <summary>Create an item instance for the implement equipped on a tractor, if any.</summary>
    /// <param name="tractor">The tractor to check.</param>
    public static Item? GetEquippedItem(Horse? tractor)
    {
        ImplementDefinition? implement = GetEquipped(tractor);
        return implement != null
            ? ItemRegistry.Create(implement.QualifiedId)
            : null;
    }

    /// <summary>Set the implement equipped on a tractor.</summary>
    /// <param name="tractor">The tractor to change.</param>
    /// <param name="item">The implement item, or <c>null</c> to remove it.</param>
    public static void SetEquipped(Horse tractor, Item? item)
    {
        if (item != null && IsImplement(item))
            tractor.modData[ImplementData.EquippedDataKey] = item.QualifiedItemId;
        else
            tractor.modData.Remove(ImplementData.EquippedDataKey);

        // a newly attached tank starts empty
        tractor.modData[ImplementData.WaterDataKey] = "0";
        Tanks.Remove(tractor.HorseId);
        TankOwners.Remove(tractor.HorseId);
    }

    /// <summary>Give an item to a player, or drop it at their feet if their inventory is full.</summary>
    /// <param name="player">The player.</param>
    /// <param name="item">The item.</param>
    public static void GiveOrDrop(Farmer player, Item item)
    {
        if (player.addItemToInventoryBool(item))
            return;

        Game1.createItemDebris(item, player.getStandingPosition(), player.FacingDirection, player.currentLocation);
        Game1.addHUDMessage(new HUDMessage(I18n.ImplementMenu_DroppedOnGround(item.DisplayName), HUDMessage.error_type));
    }

    /// <summary>Remove the implement from a tractor and give it (and the seeder hopper contents) to a player. Items that don't fit are dropped at their feet.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="player">The player who receives the items.</param>
    /// <returns>Returns the implement that was removed, if any.</returns>
    public static Item? UnequipTo(Horse tractor, Farmer player)
    {
        Item? implement = GetEquippedItem(tractor);
        foreach (Item item in SeederHopper.TakeAll(tractor))
            GiveOrDrop(player, item);

        if (implement == null)
            return null;

        SetEquipped(tractor, null);
        GiveOrDrop(player, implement);
        return implement;
    }

    /// <summary>Remove the implement from a tractor for the implement menu. See <see cref="UnequipTo"/>.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="player">The player who receives the items.</param>
    public static bool TryUnequipTo(Horse tractor, Farmer player)
    {
        UnequipTo(tractor, player);
        return true;
    }

    /// <summary>Get the water left in the tank of a tractor.</summary>
    /// <param name="tractor">The tractor to check.</param>
    public static int GetStoredWater(Horse tractor)
    {
        return tractor.modData.TryGetValue(ImplementData.WaterDataKey, out string? raw) && int.TryParse(raw, out int water)
            ? water
            : 0;
    }

    /// <summary>Save the water left in the tank of a tractor.</summary>
    /// <param name="tractor">The tractor to change.</param>
    /// <param name="water">The water left.</param>
    public static void SetStoredWater(Horse tractor, int water)
    {
        string value = water.ToString();
        if (!tractor.modData.TryGetValue(ImplementData.WaterDataKey, out string? prev) || prev != value)
            tractor.modData[ImplementData.WaterDataKey] = value;
    }

    /// <summary>Get the tank capacity for an implement level.</summary>
    /// <param name="tier">The implement level.</param>
    public int GetTankCapacity(int tier)
    {
        int[] capacities = this.GetTankCapacities();
        return capacities.Length >= tier && tier > 0 ? capacities[tier - 1] : 200;
    }

    /// <summary>Get the tool and item that the implement equipped on a tractor acts as.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="attachment">The tractor attachment which will use the tool or item.</param>
    public (Tool? Tool, Item? Item) GetImplementTool(Horse? tractor, Attachments.IAttachment attachment)
    {
        ImplementDefinition? implement = GetEquipped(tractor);
        if (implement == null || tractor == null)
            return (null, null);

        switch (implement.Kind)
        {
            case ImplementKind.Plow:
                return (this.Hoe, null);

            case ImplementKind.Pickaxe:
                return (implement.Tier >= 2 ? this.HeavyPickaxe : this.Pickaxe, null);

            case ImplementKind.WoodCutter:
                return (implement.Tier >= 2 ? this.HeavyAxe : this.Axe, null);

            case ImplementKind.WateringTank:
                {
                    TractorWaterTank tank = this.GetTank(tractor);
                    tank.Capacity = this.GetTankCapacity(implement.Tier);
                    tank.Infinite = this.GetInfiniteWater();
                    return (tank, null);
                }

            case ImplementKind.HarvesterProduce:
            case ImplementKind.HarvesterGrain:
            case ImplementKind.HarvesterBush:
            case ImplementKind.HarvesterFruitTree:
            case ImplementKind.Mower:
                return (this.GetScythe(implement.Kind), null);

            case ImplementKind.Seeder:
                // fertilize first, then plant
                return attachment switch
                {
                    Attachments.FertilizerAttachment => (null, SeederHopper.GetFirst(tractor, StardewValley.Object.fertilizerCategory)),
                    Attachments.SeedAttachment => (null, SeederHopper.GetFirst(tractor, StardewValley.Object.SeedsCategory)),
                    _ => (null, null)
                };

            default:
                return (null, null);
        }
    }

    /// <summary>Get the cached water tank for a tractor, loading its water level from the save data if needed.</summary>
    /// <param name="tractor">The tractor.</param>
    public TractorWaterTank GetTank(Horse tractor)
    {
        // the cached tank is the source of truth while the same tractor instance is used
        if (!Tanks.TryGetValue(tractor.HorseId, out TractorWaterTank? tank) || !object.ReferenceEquals(TankOwners.GetValueOrDefault(tractor.HorseId), tractor))
        {
            tank = new TractorWaterTank { Water = GetStoredWater(tractor) };
            Tanks[tractor.HorseId] = tank;
            TankOwners[tractor.HorseId] = tractor;
        }

        return tank;
    }

    /// <summary>Save the water level of each cached tank to its tractor.</summary>
    public void SaveTanks()
    {
        foreach ((System.Guid id, TractorWaterTank tank) in Tanks)
        {
            if (TankOwners.TryGetValue(id, out Horse? tractor))
                SetStoredWater(tractor, tank.Water);
        }
    }

    /// <summary>Clear cached data when returning to the title screen.</summary>
    public void Reset()
    {
        SeederHopper.Reset();
        Tanks.Clear();
        TankOwners.Clear();
    }


    /*********
    ** Private fields for tools
    *********/
    /// <summary>The hoe used by the plow.</summary>
    private readonly StardewValley.Tools.Hoe Hoe = new();

    /// <summary>The pickaxe used by the level 1 rock breaker (copper: stones and ore).</summary>
    private readonly StardewValley.Tools.Pickaxe Pickaxe = new() { UpgradeLevel = Tool.copper };

    /// <summary>The pickaxe used by the heavy rock breaker (gold: boulders and meteorites).</summary>
    private readonly StardewValley.Tools.Pickaxe HeavyPickaxe = new() { UpgradeLevel = Tool.gold };

    /// <summary>The axe used by the level 1 wood cutter (copper: trees and stumps).</summary>
    private readonly TractorAxe Axe = new(Tool.stone); // level 1: trees only, no large stumps or hollow logs

    /// <summary>The axe used by the heavy wood cutter (gold: large logs too).</summary>
    private readonly TractorAxe HeavyAxe = new(Tool.gold);

    /// <summary>The scythes used by harvesters and the mower, by implement kind.</summary>
    /// <remarks>Created on first use: a scythe loads item data, which isn't available yet when the mod starts.</remarks>
    private readonly Dictionary<ImplementKind, TractorScythe> ScytheCache = [];

    /// <summary>Get the scythe used by a harvester or the mower.</summary>
    /// <param name="kind">The implement kind.</param>
    private TractorScythe GetScythe(ImplementKind kind)
    {
        if (!this.ScytheCache.TryGetValue(kind, out TractorScythe? scythe))
            this.ScytheCache[kind] = scythe = new TractorScythe(kind);
        return scythe;
    }

    /// <summary>The cached water tanks, indexed by tractor ID.</summary>
    private static readonly Dictionary<System.Guid, TractorWaterTank> Tanks = [];

    /// <summary>The tractor instance which each cached tank belongs to, indexed by tractor ID.</summary>
    private static readonly Dictionary<System.Guid, Horse> TankOwners = [];
}
