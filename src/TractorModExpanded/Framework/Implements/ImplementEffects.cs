using System;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.TractorMod.Framework.Attachments;
using StardewValley;
using StardewValley.Characters;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>Spawns small animated particles where an implement works a tile (TractorMod Expanded).</summary>
internal static class ImplementEffects
{
    /// <summary>The asset name for the particle sheet (assets/effects.png: 6 rows x 4 frames of 8x8).</summary>
    public const string TextureAssetName = "Mods/Pathoschild.TractorMod/Effects";

    /// <summary>Whether effects are shown (set from the config).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The number of effects spawned since the game started (for tests).</summary>
    public static int SpawnedCount { get; private set; }

    /// <summary>Spawn the effect for the implement on a tractor after it worked a tile.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="attachment">The attachment which worked the tile.</param>
    /// <param name="tile">The tile worked.</param>
    /// <param name="location">The location.</param>
    public static void Spawn(Horse? tractor, IAttachment attachment, Vector2 tile, GameLocation location)
    {
        if (!Enabled || location == null || ImplementManager.GetEquipped(tractor) is not { } implement)
            return;

        int? row = implement.Kind switch
        {
            ImplementKind.Plow => 0,
            ImplementKind.WoodCutter => 1,
            ImplementKind.Pickaxe => 2,
            ImplementKind.Seeder => 3,
            ImplementKind.WateringTank => 4,
            ImplementKind.HarvesterProduce or ImplementKind.HarvesterGrain or ImplementKind.HarvesterBush or ImplementKind.HarvesterFruitTree or ImplementKind.Mower => 5,
            _ => null
        };
        if (row == null)
            return;

        Random random = Game1.random;
        int count = row is 1 or 2 ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            Vector2 position = tile * Game1.tileSize + new Vector2(random.Next(4, 36), random.Next(0, 28));
            location.temporarySprites.Add(new TemporaryAnimatedSprite(
                textureName: TextureAssetName,
                sourceRect: new Rectangle(0, row.Value * 8, 8, 8),
                animationInterval: 90f + random.Next(0, 30),
                animationLength: 4,
                numberOfLoops: 0,
                position: position,
                flicker: false,
                flipped: random.NextDouble() < 0.5,
                layerDepth: (tile.Y * Game1.tileSize + Game1.tileSize) / 10000f + 0.002f,
                alphaFade: 0f,
                color: Color.White,
                scale: 4f,
                scaleChange: 0f,
                rotation: 0f,
                rotationChange: 0f
            ));
            SpawnedCount++;
        }
    }
}
