using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Characters;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>Animates implements while they work (TractorMod Expanded), and empties the water tank visually.</summary>
/// <remarks>
/// Animation frames are generated from the 1x implement sprites by moving or swapping known palette colours, so no extra art
/// files are needed: blade and teeth rows scroll (saw, cutter bar, mower blade, rotor), belts and discs swap colours (conveyor,
/// seeder discs), shaker rods vibrate, and some implements move as a whole (rock breaker hammering, tree shaker shaking, plow
/// vibrating). The tank body loses its blue from the top down as the water runs out, showing the ground through it.
/// </remarks>
internal static class ImplementAnimator
{
    /*********
    ** Fields
    *********/
    /// <summary>How long an implement keeps animating after it last worked a tile.</summary>
    private static readonly TimeSpan WorkingWindow = TimeSpan.FromMilliseconds(650);

    /// <summary>How long each animation frame lasts.</summary>
    private const double FrameMs = 110;

    /// <summary>The number of animation frames.</summary>
    public const int FrameCount = 4;

    /// <summary>When each tractor last worked a tile, by tractor ID.</summary>
    private static readonly Dictionary<Guid, DateTime> LastWorked = [];

    /// <summary>Generated textures by (file, frame, water step).</summary>
    private static readonly Dictionary<(Texture2D Base, int Frame, int Water), Texture2D> Cache = [];

    /// <summary>The number of water levels the tank is drawn with.</summary>
    private const int WaterSteps = 12;

    // palette colours from art/render_implementos.ps1
    private static readonly Color Outline = Hex("2A1A0C"), Steel = Hex("AEB6C0"), SteelShadow = Hex("6B7480"), SteelLight = Hex("F2F6FA");
    private static readonly Color Tyre = Hex("24242A"), Belt = Hex("4A4A52"), Yellow = Hex("E8B21E"), Orange = Hex("F08A24"), OrangeShadow = Hex("B0560E");
    private static readonly Color Rod = Hex("FFC070"), RodDark = Hex("3A3F47"), Gold = Hex("F2C230");
    private static readonly HashSet<Color> WaterColors = [Hex("4F8FBF"), Hex("9FD0F0"), Hex("2E5C80"), Hex("6F7FD0"), Hex("B8C0F8"), Hex("45509A")];

    /// <summary>Get whether a pixel colour is tank water (tests).</summary>
    public static bool IsWaterColor(Color color)
    {
        return WaterColors.Contains(color);
    }

    /// <summary>Whether to treat every tractor as working (tests only).</summary>
    public static bool ForceWorking { get; set; }


    /*********
    ** Public methods
    *********/
    /// <summary>Record that a tractor's implement just worked a tile.</summary>
    /// <param name="tractor">The tractor.</param>
    public static void MarkWorking(Horse? tractor)
    {
        if (tractor != null)
            LastWorked[tractor.HorseId] = DateTime.UtcNow;
    }

    /// <summary>Get whether a tractor's implement is working right now.</summary>
    /// <param name="tractor">The tractor.</param>
    public static bool IsWorking(Horse tractor)
    {
        return ForceWorking || (LastWorked.TryGetValue(tractor.HorseId, out DateTime last) && DateTime.UtcNow - last < WorkingWindow);
    }

    /// <summary>Get the current animation frame for a tractor (0 when idle).</summary>
    /// <param name="tractor">The tractor.</param>
    public static int GetFrame(Horse tractor)
    {
        return IsWorking(tractor)
            ? (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / FrameMs) % FrameCount
            : 0;
    }

    /// <summary>Get the whole-sprite offset (in tractor-frame pixels) for implements that move as a whole while working.</summary>
    /// <param name="kind">The implement kind.</param>
    /// <param name="frame">The animation frame.</param>
    public static Vector2 GetOffset(ImplementKind kind, int frame)
    {
        return kind switch
        {
            ImplementKind.Pickaxe => new Vector2(0, frame switch { 1 => 1, 2 => 2, 3 => 1, _ => 0 }),   // hammering
            ImplementKind.HarvesterFruitTree => new Vector2(frame % 2 == 0 ? 0 : 1, 0),                 // shaking
            ImplementKind.Plow => new Vector2(0, frame % 2 == 0 ? 0 : 1),                              // vibrating in the soil
            _ => Vector2.Zero
        };
    }

    /// <summary>Get the texture for an implement sprite at an animation frame and water level.</summary>
    /// <param name="baseTexture">The 1x sprite.</param>
    /// <param name="kind">The implement kind.</param>
    /// <param name="frame">The animation frame.</param>
    /// <param name="waterFraction">The water tank fill (0–1), or null to draw it full.</param>
    public static Texture2D GetTexture(Texture2D baseTexture, ImplementKind kind, int frame, float? waterFraction)
    {
        int waterStep = waterFraction.HasValue ? (int)Math.Ceiling(MathHelper.Clamp(waterFraction.Value, 0, 1) * WaterSteps) : WaterSteps;
        if (frame == 0 && waterStep == WaterSteps)
            return baseTexture;

        if (!Cache.TryGetValue((baseTexture, frame, waterStep), out Texture2D? texture))
        {
            int w = baseTexture.Width, h = baseTexture.Height;
            Color[] data = new Color[w * h];
            baseTexture.GetData(data);

            if (frame > 0)
                Animate(data, w, h, kind, frame);
            if (kind == ImplementKind.WateringTank && waterStep < WaterSteps)
                Drain(data, w, h, waterStep / (float)WaterSteps);

            texture = new Texture2D(Game1.graphics.GraphicsDevice, w, h);
            texture.SetData(data);
            Cache[(baseTexture, frame, waterStep)] = texture;
        }
        return texture;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Apply the working animation of an implement to sprite pixels.</summary>
    private static void Animate(Color[] data, int w, int h, ImplementKind kind, int frame)
    {
        switch (kind)
        {
            case ImplementKind.WoodCutter:
            case ImplementKind.Mower:
                // saw disc / mower blade: the teeth rows run (scroll) and flicker, like a spinning blade
                ScrollRowsWithColors(data, w, h, [Steel, SteelShadow, SteelLight, Gold], frame, onlyRowsWithBoth: (Steel, SteelShadow)); // gold details of the heavy cutter spin too
                break;

            case ImplementKind.HarvesterGrain:
                // reel turning (yellow/dark bars) and cutter bar teeth running
                ScrollRowsWithColors(data, w, h, [Steel, SteelShadow, Outline], frame, onlyRowsWithBoth: (Steel, Outline));
                SwapInRows(data, w, h, Yellow, Outline, frame % 2 == 1, onlyRowsWithTransparency: true);
                break;

            case ImplementKind.HarvesterProduce:
                // conveyor belt moving and rotor teeth turning
                if (frame % 2 == 1)
                    Swap(data, Tyre, Belt);
                ScrollRowsWithColors(data, w, h, [Steel, Tyre, Outline], frame, onlyRowsWithBoth: (Steel, Tyre));
                break;

            case ImplementKind.HarvesterBush:
                // shaker rods vibrating
                if (frame % 2 == 1)
                    Swap(data, Rod, Steel);
                if (frame >= 2)
                    Swap(data, RodDark, Hex("2A2E34"));
                break;

            case ImplementKind.Seeder:
                // seed discs turning
                if (frame % 2 == 1)
                    SwapInBottomRows(data, w, h, 6, Orange, OrangeShadow); // only the disc rows, not the hoppers
                break;

            case ImplementKind.WateringTank:
                // spray drops falling from the boom: the bottom rows scroll
                ScrollBottomRows(data, w, h, 2, frame);
                break;
        }
    }

    /// <summary>Remove the tank's water from the top down: blue pixels above the water line become transparent.</summary>
    private static void Drain(Color[] data, int w, int h, float fraction)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (WaterColors.Contains(data[y * w + x]))
                {
                    if (top < 0)
                        top = y;
                    bottom = y;
                }
            }
        }
        if (top < 0)
            return;

        int bodyRows = bottom - top + 1;
        int waterLine = top + (int)Math.Round(bodyRows * (1 - fraction)); // rows above this are empty
        for (int y = top; y < waterLine; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (WaterColors.Contains(data[y * w + x]))
                    data[y * w + x] = Color.Transparent;
            }
        }

        // surface highlight on the water line
        if (waterLine <= bottom)
        {
            for (int x = 0; x < w; x++)
            {
                if (WaterColors.Contains(data[waterLine * w + x]))
                    data[waterLine * w + x] = Hex("9FD0F0");
            }
        }
    }

    /// <summary>Scroll horizontally the rows that contain both of two colours (blade/teeth rows), rotating only the pixels in the given colours.</summary>
    private static void ScrollRowsWithColors(Color[] data, int w, int h, Color[] colors, int frame, (Color A, Color B) onlyRowsWithBoth)
    {
        HashSet<Color> set = [.. colors];
        for (int y = 0; y < h; y++)
        {
            bool hasA = false, hasB = false;
            for (int x = 0; x < w; x++)
            {
                hasA |= data[y * w + x] == onlyRowsWithBoth.A;
                hasB |= data[y * w + x] == onlyRowsWithBoth.B;
            }
            if (!hasA || !hasB)
                continue;

            // collect the blade pixels inside the part (the first and last pixel of the row stay fixed, so the outline doesn't move)
            int first = -1, last = -1;
            for (int x = 0; x < w; x++)
            {
                if (data[y * w + x].A > 0)
                {
                    if (first < 0)
                        first = x;
                    last = x;
                }
            }
            List<int> xs = [];
            for (int x = first + 1; x < last; x++)
            {
                if (set.Contains(data[y * w + x]))
                    xs.Add(x);
            }
            if (xs.Count < 3)
                continue;
            Color[] row = new Color[xs.Count];
            for (int i = 0; i < xs.Count; i++)
                row[i] = data[y * w + xs[(i + frame) % xs.Count]];
            for (int i = 0; i < xs.Count; i++)
                data[y * w + xs[i]] = row[i];
        }
    }

    /// <summary>Scroll the bottom rows (e.g. spray drops) horizontally by the frame, wrapping within the sprite width.</summary>
    private static void ScrollBottomRows(Color[] data, int w, int h, int rows, int frame)
    {
        for (int y = Math.Max(0, h - rows); y < h; y++)
        {
            Color[] row = new Color[w];
            for (int x = 0; x < w; x++)
                row[x] = data[y * w + (x - frame + w * 4) % w];
            Array.Copy(row, 0, data, y * w, w);
        }
    }

    /// <summary>Swap two colours in rows that have transparent gaps (e.g. reel bars), when enabled.</summary>
    private static void SwapInRows(Color[] data, int w, int h, Color a, Color b, bool enabled, bool onlyRowsWithTransparency)
    {
        if (!enabled)
            return;
        for (int y = 0; y < h; y++)
        {
            // only rows with gaps *inside* the part (e.g. reel bars), so outlines of solid rows are untouched
            int first = -1, last = -1;
            for (int x = 0; x < w; x++)
            {
                if (data[y * w + x].A > 0)
                {
                    if (first < 0)
                        first = x;
                    last = x;
                }
            }
            bool hasGap = false, hasA = false;
            for (int x = Math.Max(first, 0); x <= last; x++)
            {
                hasGap |= data[y * w + x].A == 0;
                hasA |= data[y * w + x] == a;
            }
            if (!hasA || (onlyRowsWithTransparency && !hasGap))
                continue;
            for (int x = 0; x < w; x++)
            {
                if (data[y * w + x] == a)
                    data[y * w + x] = b;
                else if (data[y * w + x] == b)
                    data[y * w + x] = a;
            }
        }
    }

    /// <summary>Swap two colours in the bottom rows of the sprite only.</summary>
    private static void SwapInBottomRows(Color[] data, int w, int h, int rows, Color a, Color b)
    {
        for (int i = Math.Max(0, h - rows) * w; i < data.Length; i++)
        {
            if (data[i] == a)
                data[i] = b;
            else if (data[i] == b)
                data[i] = a;
        }
    }

    /// <summary>Swap two colours everywhere in the sprite.</summary>
    private static void Swap(Color[] data, Color a, Color b)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == a)
                data[i] = b;
            else if (data[i] == b)
                data[i] = a;
        }
    }

    /// <summary>Parse a hex colour.</summary>
    private static Color Hex(string hex)
    {
        return new Color(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }
}
