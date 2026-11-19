using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>The result of trying to start a tank refill.</summary>
internal enum RefillStartResult
{
    /// <summary>The player isn't riding a tractor with a tank, or isn't facing water.</summary>
    NotApplicable,

    /// <summary>The refill started.</summary>
    Started,

    /// <summary>A refill is already in progress.</summary>
    AlreadyRefilling,

    /// <summary>The tank is already full.</summary>
    AlreadyFull
}

/// <summary>Refills a tractor's water tank: while riding with a tank equipped, the player faces water and presses the action button (right-click). A pipe drops into the water and the tank fills in a fixed time.</summary>
internal sealed class WaterTankRefill
{
    /*********
    ** Fields
    *********/
    /// <summary>How long a refill takes, regardless of tank size.</summary>
    private static readonly TimeSpan RefillTime = TimeSpan.FromSeconds(5);

    /// <summary>Manages implements.</summary>
    private readonly ImplementManager Implements;

    /// <summary>Simplifies access to input.</summary>
    private readonly IInputHelper Input;

    /// <summary>The refill in progress, if any.</summary>
    private RefillState? Active;

    /// <summary>The state of a refill in progress.</summary>
    /// <param name="Tractor">The tractor being refilled.</param>
    /// <param name="Tank">The tank being refilled.</param>
    /// <param name="WaterTile">The water tile the pipe drops into.</param>
    /// <param name="PlayerPosition">The player position when the refill started, to cancel if they move.</param>
    /// <param name="StartWater">The water in the tank when the refill started.</param>
    /// <param name="Elapsed">The time elapsed so far.</param>
    private sealed record RefillState(Horse Tractor, TractorWaterTank Tank, Vector2 WaterTile, Vector2 PlayerPosition, int StartWater)
    {
        /// <summary>Measures real time, so the refill takes the same time regardless of the game's update rate (e.g. with UltraSmooth).</summary>
        public System.Diagnostics.Stopwatch Timer { get; } = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>The elapsed real time in milliseconds.</summary>
        public double ElapsedMs => this.Timer.Elapsed.TotalMilliseconds;

        /// <summary>The elapsed time when the last sound played.</summary>
        public double LastSoundMs { get; set; }
    }


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="implements">Manages implements.</param>
    /// <param name="input">Simplifies access to input.</param>
    public WaterTankRefill(ImplementManager implements, IInputHelper input)
    {
        this.Implements = implements;
        this.Input = input;
    }

    /// <summary>Whether a refill is in progress.</summary>
    public bool IsRefilling => this.Active != null;

    /// <inheritdoc cref="IInputEvents.ButtonPressed" />
    /// <param name="e">The event data.</param>
    /// <param name="isRidingTractor">Whether the current player is riding a tractor.</param>
    public void OnButtonPressed(ButtonPressedEventArgs e, bool isRidingTractor)
    {
        if (!isRidingTractor || !Context.IsPlayerFree || !e.Button.IsActionButton())
            return;

        // replace the normal right-click action (which would dismount) with the refill
        if (this.TryStart(Game1.player) != RefillStartResult.NotApplicable)
            this.Input.Suppress(e.Button);
    }

    /// <summary>Start refilling the tank of the tractor ridden by a player, if they're facing water.</summary>
    /// <param name="player">The player riding the tractor.</param>
    public RefillStartResult TryStart(Farmer player)
    {
        Horse? tractor = player.mount;
        if (tractor == null || ImplementManager.GetEquipped(tractor) is not { Kind: ImplementKind.WateringTank } implement)
            return RefillStartResult.NotApplicable;

        // find water in front of the tractor (it's two tiles long, so check up to three tiles ahead)
        Vector2? waterTile = this.FindWaterAhead(player);
        if (waterTile == null)
            return RefillStartResult.NotApplicable;

        if (this.Active != null)
            return RefillStartResult.AlreadyRefilling;

        TractorWaterTank tank = this.Implements.GetTank(tractor);
        tank.Capacity = this.Implements.GetTankCapacity(implement.Tier);
        if (tank.Water >= tank.Capacity)
        {
            Game1.addHUDMessage(new HUDMessage(I18n.WaterTank_AlreadyFull(), HUDMessage.error_type));
            return RefillStartResult.AlreadyFull;
        }

        this.Active = new RefillState(tractor, tank, waterTile.Value, player.Position, tank.Water);
        Game1.playSound("dropItemInWater");
        return RefillStartResult.Started;
    }

    /// <summary>Update the refill in progress.</summary>
    /// <param name="elapsed">The game time elapsed since the last update.</param>
    public void Update(GameTime elapsed)
    {
        RefillState? state = this.Active;
        if (state == null)
            return;

        // cancel if the player moved, dismounted or opened a menu
        Farmer player = Game1.player;
        if (!object.ReferenceEquals(player.mount, state.Tractor) || Vector2.Distance(player.Position, state.PlayerPosition) > 4 || Game1.activeClickableMenu != null)
        {
            this.Active = null;
            return;
        }
        player.Halt();

        // fill
        double progress = Math.Min(1, state.ElapsedMs / RefillTime.TotalMilliseconds);
        state.Tank.Water = state.StartWater + (int)Math.Round((state.Tank.Capacity - state.StartWater) * progress);

        if (state.ElapsedMs - state.LastSoundMs >= 900)
        {
            state.LastSoundMs = state.ElapsedMs;
            Game1.playSound("slosh");
        }

        if (progress >= 1)
        {
            state.Tank.Water = state.Tank.Capacity;
            this.Implements.SaveTanks();
            this.Active = null;
            Game1.playSound("dwop");
        }
    }

    /// <summary>Draw the pipe and tank gauge.</summary>
    /// <param name="b">The sprite batch.</param>
    /// <param name="isRidingTractor">Whether the current player is riding a tractor.</param>
    public void Draw(SpriteBatch b, bool isRidingTractor)
    {
        if (!isRidingTractor)
            return;

        Farmer player = Game1.player;
        if (ImplementManager.GetEquipped(player.mount) is not { Kind: ImplementKind.WateringTank } implement)
            return;

        TractorWaterTank tank = this.Implements.GetTank(player.mount);
        tank.Capacity = this.Implements.GetTankCapacity(implement.Tier);

        // pipe from the tank into the water
        if (this.Active is { } state)
        {
            Vector2 start = Game1.GlobalToLocal(Game1.viewport, player.Position + new Vector2(32, -48));
            Vector2 end = Game1.GlobalToLocal(Game1.viewport, state.WaterTile * Game1.tileSize + new Vector2(32, 40));

            // the pipe extends during the first half second
            float extend = (float)Math.Min(1, state.ElapsedMs / 500);
            Vector2 tip = Vector2.Lerp(start, end, extend);
            this.DrawThickLine(b, start, tip, 12, new Color(40, 40, 46));
            this.DrawThickLine(b, start, tip, 8, new Color(120, 124, 132));
            this.DrawThickLine(b, start + new Vector2(0, -2), tip + new Vector2(0, -2), 2, new Color(190, 194, 200));

            // ripples where the pipe meets the water
            if (extend >= 1)
            {
                float pulse = (float)(state.ElapsedMs % 600 / 600);
                int size = 12 + (int)(pulse * 28);
                b.Draw(Game1.staminaRect, new Rectangle((int)end.X - size / 2, (int)end.Y - 2, size, 4), Color.White * (0.7f * (1 - pulse)));
            }
        }

        // gauge above the tractor
        if (!tank.Infinite)
        {
            Vector2 pos = Game1.GlobalToLocal(Game1.viewport, player.Position + new Vector2(-4, -150));
            Rectangle outer = new((int)pos.X, (int)pos.Y, 96, 16);
            Color outline = new(42, 26, 12);

            // water drop icon (pixel art, 3px per pixel)
            string[] drop = ["..O..", ".OCO.", "OCWCO", "OCCCO", "OBCBO", ".OOO."];
            for (int y = 0; y < drop.Length; y++)
            {
                for (int x = 0; x < drop[y].Length; x++)
                {
                    Color? c = drop[y][x] switch { 'O' => outline, 'C' => new Color(110, 180, 240), 'W' => Color.White, 'B' => new Color(60, 120, 200), _ => null };
                    if (c != null)
                        b.Draw(Game1.staminaRect, new Rectangle(outer.X - 20 + x * 3, outer.Y - 1 + y * 3, 3, 3), c.Value);
                }
            }

            // bar: outline, dark background, water, quarter marks, highlight
            b.Draw(Game1.staminaRect, outer, outline);
            b.Draw(Game1.staminaRect, new Rectangle(outer.X + 2, outer.Y + 2, outer.Width - 4, outer.Height - 4), new Color(30, 30, 36));
            float fill = tank.Capacity > 0 ? MathHelper.Clamp(tank.Water / (float)tank.Capacity, 0, 1) : 0;
            int fillWidth = (int)((outer.Width - 4) * fill);
            b.Draw(Game1.staminaRect, new Rectangle(outer.X + 2, outer.Y + 2, fillWidth, outer.Height - 4), new Color(70, 150, 230));
            b.Draw(Game1.staminaRect, new Rectangle(outer.X + 2, outer.Y + 2, fillWidth, 3), new Color(150, 210, 255));
            for (int q = 1; q < 4; q++)
                b.Draw(Game1.staminaRect, new Rectangle(outer.X + 2 + (outer.Width - 4) * q / 4, outer.Y + 2, 1, outer.Height - 4), outline * 0.6f);
            if (fill < 0.15f && this.Active == null)
                b.Draw(Game1.staminaRect, outer, Color.Red * (0.25f + 0.2f * (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 200)));
        }
    }

    /// <summary>Cancel any refill in progress.</summary>
    public void Cancel()
    {
        this.Active = null;
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Find a water tile in front of the player, if any.</summary>
    /// <param name="player">The player riding the tractor.</param>
    private Vector2? FindWaterAhead(Farmer player)
    {
        GameLocation location = player.currentLocation;
        Vector2 direction = player.FacingDirection switch
        {
            Game1.up => new Vector2(0, -1),
            Game1.down => new Vector2(0, 1),
            Game1.left => new Vector2(-1, 0),
            _ => new Vector2(1, 0)
        };

        Vector2 origin = player.Tile;
        for (int distance = 1; distance <= 3; distance++)
        {
            Vector2 tile = origin + direction * distance;
            if (location.CanRefillWateringCanOnTile((int)tile.X, (int)tile.Y))
                return tile;
        }

        return null;
    }

    /// <summary>Draw a thick line.</summary>
    /// <param name="b">The sprite batch.</param>
    /// <param name="start">The start position in screen pixels.</param>
    /// <param name="end">The end position in screen pixels.</param>
    /// <param name="thickness">The line thickness.</param>
    /// <param name="color">The line color.</param>
    private void DrawThickLine(SpriteBatch b, Vector2 start, Vector2 end, int thickness, Color color)
    {
        Vector2 delta = end - start;
        float length = delta.Length();
        if (length < 1)
            return;

        float angle = (float)Math.Atan2(delta.Y, delta.X);
        b.Draw(Game1.staminaRect, start, null, color, angle, new Vector2(0, 0.5f), new Vector2(length, thickness), SpriteEffects.None, 1f);
    }
}
