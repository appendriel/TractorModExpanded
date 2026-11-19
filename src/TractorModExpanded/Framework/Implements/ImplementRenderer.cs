using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>Draws the equipped implement attached to the tractor (TractorMod Expanded), behind or in front of it depending on the direction.</summary>
/// <remarks>
/// Hooked after <see cref="Horse.draw(SpriteBatch)"/>. The world is drawn with <see cref="SpriteSortMode.FrontToBack"/>, so the
/// implement is placed behind or in front of the tractor and rider purely through its layer depth.
/// Sprite coordinates are in tractor-frame pixels (the tractor sprite is a 32x32 frame drawn at 4x).
/// </remarks>
internal static class ImplementRenderer
{
    /*********
    ** Fields
    *********/
    /// <summary>Loads mod files.</summary>
    private static IModContentHelper ModContent = null!;

    /// <summary>Writes to the SMAPI log.</summary>
    private static IMonitor Monitor = null!;

    /// <summary>Whether the tractor check is available.</summary>
    private static Func<Horse?, bool> IsTractor = _ => false;

    /// <summary>Loaded implement textures by file name.</summary>
    private static readonly Dictionary<string, Texture2D?> Textures = [];

    /// <summary>Whether an error was already logged (to avoid flooding the log every frame).</summary>
    private static bool LoggedError;


    /*********
    ** Accessors
    *********/
    /// <summary>The offset in screen pixels from the tractor's local position to the top-left corner of its 32x32 sprite frame.</summary>
    /// <remarks>Calibrated from in-game screenshots (test mode 'implement-calibration').</remarks>
    public static Vector2 SpriteOffset { get; set; } = new(-16, -80);

    /// <summary>The extra horizontal offset in screen pixels when the tractor faces left (the game draws the flipped sprite 4 sprite pixels further right).</summary>
    public static float LeftFacingOffsetX { get; set; } = 16;

    /// <summary>Get the screen position of the tractor sprite's top-left corner.</summary>
    /// <param name="tractor">The tractor.</param>
    private static Vector2 GetSpriteOrigin(Horse tractor)
    {
        Vector2 origin = Game1.GlobalToLocal(Game1.viewport, tractor.Position) + SpriteOffset;
        if (tractor.FacingDirection == Game1.left)
            origin.X += LeftFacingOffsetX;
        return origin;
    }

    /// <summary>Get the water tank fill (0–1) of a tractor, or null if it has no tank or the water is infinite.</summary>
    public static Func<Horse, float?> GetWaterFraction { get; set; } = _ => null;

    /// <summary>Whether to draw a calibration marker at the tractor's position (test mode only).</summary>
    public static bool DrawCalibrationMarker { get; set; }


    /*********
    ** Public methods
    *********/
    /// <summary>Apply the Harmony patch.</summary>
    /// <param name="modId">The mod ID for Harmony.</param>
    /// <param name="modContent">Loads mod files.</param>
    /// <param name="monitor">Writes to the SMAPI log.</param>
    /// <param name="isTractor">Get whether a horse is a tractor.</param>
    public static void Apply(string modId, IModContentHelper modContent, IMonitor monitor, Func<Horse?, bool> isTractor)
    {
        ModContent = modContent;
        Monitor = monitor;
        IsTractor = isTractor;

        Harmony harmony = new(modId);
        harmony.Patch(
            original: AccessTools.Method(typeof(Horse), nameof(Horse.draw), [typeof(SpriteBatch)]),
            postfix: new HarmonyMethod(typeof(ImplementRenderer), nameof(After_HorseDraw))
        );
    }

    /// <summary>Get the sprite file name (without view suffix) for an implement.</summary>
    /// <param name="implement">The implement.</param>
    public static string GetSpriteName(ImplementDefinition implement)
    {
        return implement.Kind switch
        {
            ImplementKind.Plow => "arado",
            ImplementKind.Seeder => "semeadeira",
            ImplementKind.WateringTank => $"tanque_nivel{implement.Tier}",
            ImplementKind.Pickaxe => $"rompedor_nivel{implement.Tier}",
            ImplementKind.WoodCutter => $"cortador_nivel{implement.Tier}",
            ImplementKind.HarvesterProduce => "colheitadeira_legumes",
            ImplementKind.HarvesterGrain => "colheitadeira_graos",
            ImplementKind.HarvesterBush => "colheitadeira_arbustos",
            ImplementKind.HarvesterFruitTree => "colheitadeira_frutiferas",
            ImplementKind.Mower => "rocadeira",
            _ => "arado"
        };
    }

    /// <summary>Get whether an implement mounts behind the tractor (3-point hitch) rather than in front.</summary>
    /// <param name="kind">The implement kind.</param>
    public static bool IsRearMounted(ImplementKind kind)
    {
        return kind is ImplementKind.Plow or ImplementKind.Seeder or ImplementKind.WateringTank or ImplementKind.Mower;
    }

    /// <summary>Get the screen area covered by the equipped implement for a tractor, for effects (e.g. dust behind the plow).</summary>
    /// <param name="tractor">The tractor.</param>
    public static Rectangle? GetImplementScreenArea(Horse tractor)
    {
        if (ImplementManager.GetEquipped(tractor) is not { } implement)
            return null;

        (Texture2D? texture, Vector2 framePos, _, _) = GetPlacement(tractor, implement);
        if (texture == null)
            return null;
        Vector2 origin = GetSpriteOrigin(tractor);
        return new Rectangle((int)(origin.X + framePos.X * 4), (int)(origin.Y + framePos.Y * 4), texture.Width * 4, texture.Height * 4);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Draw the implement after the tractor is drawn.</summary>
    private static void After_HorseDraw(Horse __instance, SpriteBatch b)
    {
        try
        {
            if (!IsTractor(__instance))
                return;

            Vector2 origin = GetSpriteOrigin(__instance);
            float baseDepth = Math.Max(0f, __instance.StandingPixel.Y / 10000f);

            if (DrawCalibrationMarker)
                b.Draw(Game1.staminaRect, new Rectangle((int)origin.X - 2, (int)origin.Y - 2, 4, 4), null, Color.Red, 0, Vector2.Zero, SpriteEffects.None, 1f);

            if (ImplementManager.GetEquipped(__instance) is not { } implement)
                return;

            (Texture2D? texture, Vector2 framePos, bool inFront, bool flip) = GetPlacement(__instance, implement);
            if (texture == null)
                return;

            // the tractor body bobs one pixel on odd animation frames; the implement follows it
            if (__instance.Sprite.currentFrame % 2 == 1)
                framePos.Y -= 1;

            // working animation and tank level
            int frame = ImplementAnimator.GetFrame(__instance);
            Vector2 offset = ImplementAnimator.GetOffset(implement.Kind, frame);
            framePos += flip ? new Vector2(-offset.X, offset.Y) : offset;
            float? water = implement.Kind == ImplementKind.WateringTank ? GetWaterFraction(__instance) : null;
            texture = ImplementAnimator.GetTexture(texture, implement.Kind, frame, water);

            bool dim = !inFront && __instance.FacingDirection is Game1.up or Game1.down;
            float depth = inFront ? baseDepth + 0.0021f : Math.Max(0f, baseDepth - 0.0005f);
            b.Draw(
                texture,
                origin + framePos * 4,
                null,
                dim ? new Color(180, 180, 180) : Color.White,
                0f,
                Vector2.Zero,
                4f,
                flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                depth
            );
        }
        catch (Exception ex)
        {
            if (!LoggedError)
            {
                LoggedError = true;
                Monitor.Log($"Failed drawing the tractor implement: {ex}", LogLevel.Error);
            }
        }
    }

    /// <summary>Get the implement sprite and where to draw it relative to the tractor frame.</summary>
    /// <param name="tractor">The tractor.</param>
    /// <param name="implement">The equipped implement.</param>
    /// <returns>The texture, its top-left position in tractor-frame pixels, whether it's in front of the tractor, and whether to flip it.</returns>
    private static (Texture2D? Texture, Vector2 FramePos, bool InFront, bool Flip) GetPlacement(Horse tractor, ImplementDefinition implement)
    {
        bool rear = IsRearMounted(implement.Kind);
        string name = GetSpriteName(implement);

        switch (tractor.FacingDirection)
        {
            case Game1.right:
            case Game1.left:
                {
                    Texture2D? side = GetTexture($"{name}_lado");
                    if (side == null)
                        return (null, Vector2.Zero, false, false);

                    float x = rear ? 5 - side.Width : 27;
                    float y = 31 - side.Height;
                    bool flip = tractor.FacingDirection == Game1.left;
                    if (flip)
                        x = 32 - (x + side.Width);
                    return (side, new Vector2(x, y), !rear, flip);
                }

            case Game1.down:
                {
                    Texture2D? front = GetTexture($"{name}_frontal");
                    if (front == null)
                        return (null, Vector2.Zero, false, false);
                    float x = 16 - front.Width / 2;
                    return rear
                        ? (front, new Vector2(x, 13 - front.Height), false, false)
                        : (front, new Vector2(x, 24), true, false);
                }

            default: // up
                {
                    Texture2D? front = GetTexture($"{name}_frontal");
                    if (front == null)
                        return (null, Vector2.Zero, false, false);
                    float x = 16 - front.Width / 2;
                    return rear
                        ? (front, new Vector2(x, 22), true, false)
                        : (front, new Vector2(x, 9 - front.Height), false, false);
                }
        }
    }

    /// <summary>Get an implement sprite by file name (tests only).</summary>
    /// <param name="file">The file name without extension, like <c>arado_lado</c>.</param>
    public static Texture2D? GetTextureForTest(string file)
    {
        return GetTexture(file);
    }

    /// <summary>Get the side-view sprite for an implement (used by the implement menu).</summary>
    /// <param name="implement">The implement.</param>
    public static Texture2D? GetSideTexture(ImplementDefinition implement)
    {
        return GetTexture($"{GetSpriteName(implement)}_lado");
    }

    /// <summary>Load an implement texture from the mod's assets/implements folder.</summary>
    /// <param name="file">The file name without extension.</param>
    private static Texture2D? GetTexture(string file)
    {
        if (!Textures.TryGetValue(file, out Texture2D? texture))
        {
            try
            {
                texture = ModContent.Load<Texture2D>($"assets/implements/{file}.png");
            }
            catch (Exception ex)
            {
                Monitor.Log($"Couldn't load implement sprite '{file}': {ex.Message}", LogLevel.Warn);
                texture = null;
            }
            Textures[file] = texture;
        }
        return texture;
    }
}
