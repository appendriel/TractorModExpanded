using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Characters;
using StardewValley.GameData.WildTrees;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>The seeder's hopper: a few slots of seeds and fertilizer stored on the tractor.</summary>
/// <remarks>The contents are kept in memory while used, and saved in the tractor's mod data as <c>qualifiedId*stack|...</c>.</remarks>
internal static class SeederHopper
{
    /*********
    ** Fields
    *********/
    /// <summary>The number of hopper slots.</summary>
    public const int Slots = 9;

    /// <summary>The cached hopper contents, indexed by tractor ID.</summary>
    private static readonly Dictionary<Guid, (Horse Tractor, Item?[] Items)> Cache = [];


    /*********
    ** Public methods
    *********/
    /// <summary>Get whether an item can go in the seeder hopper: crop seeds and fertilizer (not saplings or tree seeds).</summary>
    /// <param name="item">The item to check.</param>
    public static bool CanHold(Item? item)
    {
        if (item is not SObject obj || obj.bigCraftable.Value)
            return false;

        if (obj.Category == SObject.fertilizerCategory)
            return true;

        if (obj.Category != SObject.SeedsCategory)
            return false;

        // only seeds, not saplings or tree seeds
        string id = obj.ItemId;
        if (Game1.fruitTreeData.ContainsKey(id) || obj.QualifiedItemId == "(O)251" /* tea sapling */)
            return false;
        foreach (WildTreeData tree in Tree.GetWildTreeDataDictionary().Values)
        {
            if (tree.SeedItemId != null && ItemRegistry.QualifyItemId(tree.SeedItemId) == obj.QualifiedItemId)
                return false;
        }
        return true;
    }

    /// <summary>Get the hopper contents for a tractor.</summary>
    /// <param name="tractor">The tractor.</param>
    public static Item?[] Get(Horse tractor)
    {
        if (Cache.TryGetValue(tractor.HorseId, out var entry) && object.ReferenceEquals(entry.Tractor, tractor))
            return entry.Items;

        Item?[] items = Parse(tractor.modData.TryGetValue(ImplementData.HopperDataKey, out string? raw) ? raw : null);
        Cache[tractor.HorseId] = (tractor, items);
        return items;
    }

    /// <summary>Get the first item in the hopper matching a category with items left.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="category">The item category.</param>
    public static Item? GetFirst(Horse tractor, int category)
    {
        return Get(tractor).FirstOrDefault(p => p is { Stack: > 0 } && p.Category == category);
    }

    /// <summary>Add an item stack to the hopper, merging with matching stacks first.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="item">The item to add.</param>
    /// <returns>Returns the items that didn't fit, or <c>null</c> if all were added.</returns>
    public static Item? Add(Horse tractor, Item item)
    {
        Item?[] items = Get(tractor);

        // merge
        foreach (Item? slot in items)
        {
            if (slot != null && slot.canStackWith(item))
            {
                int remaining = slot.addToStack(item);
                if (remaining <= 0)
                {
                    Save(tractor);
                    return null;
                }
                item.Stack = remaining;
            }
        }

        // empty slot
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is null or { Stack: <= 0 })
            {
                items[i] = item;
                Save(tractor);
                return null;
            }
        }

        Save(tractor);
        return item;
    }

    /// <summary>Remove the item in a hopper slot.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="index">The slot index.</param>
    public static Item? Take(Horse tractor, int index)
    {
        Item?[] items = Get(tractor);
        Item? item = items[index];
        items[index] = null;
        Save(tractor);
        return item is { Stack: > 0 } ? item : null;
    }

    /// <summary>Remove all items from the hopper.</summary>
    /// <param name="tractor">The tractor.</param>
    public static List<Item> TakeAll(Horse tractor)
    {
        List<Item> taken = [];
        for (int i = 0; i < Slots; i++)
        {
            Item? item = Take(tractor, i);
            if (item != null)
                taken.Add(item);
        }
        return taken;
    }

    /// <summary>Save the cached hopper contents to each tractor's mod data, removing empty stacks.</summary>
    public static void SaveAll()
    {
        foreach ((Horse tractor, Item?[] _) in Cache.Values.ToArray())
            Save(tractor);
    }

    /// <summary>Save a tractor's hopper contents to its mod data.</summary>
    /// <param name="tractor">The tractor.</param>
    public static void Save(Horse tractor)
    {
        Item?[] items = Get(tractor);
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] is { Stack: <= 0 })
                items[i] = null;
        }

        string value = string.Join("|", items.Select(p => p != null ? $"{p.QualifiedItemId}*{p.Stack}" : ""));
        if (items.All(p => p == null))
            tractor.modData.Remove(ImplementData.HopperDataKey);
        else if (!tractor.modData.TryGetValue(ImplementData.HopperDataKey, out string? prev) || prev != value)
            tractor.modData[ImplementData.HopperDataKey] = value;
    }

    /// <summary>Clear cached data when returning to the title screen.</summary>
    public static void Reset()
    {
        Cache.Clear();
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Parse the saved hopper contents.</summary>
    /// <param name="raw">The saved value.</param>
    private static Item?[] Parse(string? raw)
    {
        Item?[] items = new Item?[Slots];
        if (string.IsNullOrWhiteSpace(raw))
            return items;

        string[] entries = raw.Split('|');
        for (int i = 0; i < Math.Min(entries.Length, Slots); i++)
        {
            string[] parts = entries[i].Split('*');
            if (parts.Length == 2 && int.TryParse(parts[1], out int stack) && stack > 0)
                items[i] = ItemRegistry.Create(parts[0], stack, allowNull: true);
        }
        return items;
    }
}
