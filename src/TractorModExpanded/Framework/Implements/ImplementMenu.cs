using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Characters;
using StardewValley.Menus;

namespace Pathoschild.Stardew.TractorMod.Framework.Implements;

/// <summary>The tractor hitch menu: a blueprint of the tractor seen from the side, where the player clicks or drags an implement onto the hitch. Also fills the seeder hopper.</summary>
internal sealed class ImplementMenu : IClickableMenu
{
    /*********
    ** Fields
    *********/
    /// <summary>The tractor being edited.</summary>
    private readonly Horse Tractor;

    /// <summary>The player's inventory.</summary>
    private InventoryMenu Inventory = null!; // set in Relayout

    /// <summary>The blueprint panel.</summary>
    private Rectangle Blueprint;

    /// <summary>Where the tractor sprite is drawn (128x128, side view).</summary>
    private Rectangle TractorArea;

    /// <summary>The rear hitch zone (plow, seeder, tank, mower).</summary>
    private Rectangle RearZone;

    /// <summary>The front mount zone (wood cutter, rock breaker, harvesters).</summary>
    private Rectangle FrontZone;

    /// <summary>The seeder hopper slots (only shown while the seeder is equipped).</summary>
    private readonly List<ClickableComponent> HopperSlots = [];

    /// <summary>Whether the current layout includes the seeder hopper row.</summary>
    private bool LayoutHasHopper;

    /// <summary>The menu title.</summary>
    private readonly string Title;

    /// <summary>The text shown when no implement is equipped.</summary>
    private readonly string EmptyText;

    /// <summary>The item being hovered, if any.</summary>
    private Item? HoveredItem;

    /// <summary>The inventory index of the implement being dragged, if any.</summary>
    private int? DragIndex;

    /// <summary>Where the drag started.</summary>
    private Point DragStart;

    /// <summary>The current cursor position.</summary>
    private Point Cursor;

    /// <summary>The attach animation in progress, if any: the implement slides from <see cref="AnimFrom"/> to its hitch position.</summary>
    private ImplementDefinition? AnimImplement;

    /// <summary>The screen position the attach animation starts from.</summary>
    private Vector2 AnimFrom;

    /// <summary>The attach animation timer.</summary>
    private readonly System.Diagnostics.Stopwatch AnimTimer = new();

    /// <summary>How long the attach animation takes.</summary>
    private const double AnimMs = 380;

    /// <summary>The metal spark particles shown when an implement locks in place.</summary>
    private readonly List<(Vector2 Pos, Vector2 Vel, float Life)> Sparks = [];

    /// <summary>The last update time for the spark particles.</summary>
    private double LastSparkUpdate;


    /*********
    ** Accessors
    *********/
    /// <summary>The screen area of the attached implement (click to remove). For tests.</summary>
    public Rectangle SlotBounds => this.GetAttachedArea() ?? this.RearZone;


    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="tractor">The tractor being edited.</param>
    /// <param name="title">The menu title.</param>
    /// <param name="emptyText">The text shown when no implement is equipped.</param>
    public ImplementMenu(Horse tractor, string title, string emptyText)
        : base(0, 0, 800 + IClickableMenu.borderWidth * 2, 690 + (ImplementMenu.GetRows() - 3) * 68, showUpperRightCloseButton: true)
    {
        this.Tractor = tractor;
        this.Title = title;
        this.EmptyText = emptyText;

        this.Relayout();
    }

    /// <summary>Compute every position in the menu. Called on creation and whenever the seeder hopper row appears or disappears.</summary>
    /// <remarks>
    /// Layout, top to bottom: title scroll (above the box) / blueprint panel / wrapped description / hopper row (seeder only) / inventory.
    /// </remarks>
    private void Relayout()
    {
        this.LayoutHasHopper = this.IsSeederEquipped();
        int rows = ImplementMenu.GetRows();

        // pick the largest layout that fits the screen (the title scroll needs ~72px above the box)
        const int padTop = 52, hopperHeight = 124, padBottom = 44, titleSpace = 76;
        int blueprintHeight = 200, descriptionHeight = 104;
        foreach ((int bp, int desc) in new[] { (200, 104), (180, 84), (160, 64), (150, 56) })
        {
            blueprintHeight = bp;
            descriptionHeight = desc;
            int total = padTop + bp + desc + (this.LayoutHasHopper ? hopperHeight : 0) + rows * 68 + padBottom;
            if (total + titleSpace <= Game1.uiViewport.Height)
                break;
        }
        this.width = 800 + IClickableMenu.borderWidth * 2;
        this.height = padTop + blueprintHeight + descriptionHeight + (this.LayoutHasHopper ? hopperHeight : 0) + rows * 68 + padBottom;
        Vector2 pos = Utility.getTopLeftPositionForCenteringOnScreen(this.width, this.height);
        this.xPositionOnScreen = (int)pos.X;
        this.yPositionOnScreen = Math.Max(titleSpace, (int)pos.Y + titleSpace / 2); // room for the title scroll above the box
        this.initializeUpperRightCloseButton();

        // blueprint: tractor in the middle, rear hitch zone on the left, front mount zone on the right
        this.Blueprint = new Rectangle(this.xPositionOnScreen + 64, this.yPositionOnScreen + padTop, this.width - 128, blueprintHeight);
        this.TractorArea = new Rectangle(this.Blueprint.Center.X - 64, this.Blueprint.Bottom - 144, 128, 128);
        this.RearZone = new Rectangle(this.TractorArea.X - 26 * 4, this.TractorArea.Y + 31 * 4 - 24 * 4, 26 * 4, 24 * 4);
        this.FrontZone = new Rectangle(this.TractorArea.X + 27 * 4, this.TractorArea.Y + 31 * 4 - 24 * 4, 24 * 4, 24 * 4);

        int y = this.Blueprint.Bottom + descriptionHeight;
        this.HopperSlots.Clear();
        if (this.LayoutHasHopper)
        {
            int hopperLeft = this.xPositionOnScreen + (this.width - SeederHopper.Slots * 68) / 2;
            for (int i = 0; i < SeederHopper.Slots; i++)
                this.HopperSlots.Add(new ClickableComponent(new Rectangle(hopperLeft + i * 68, y + 36, 64, 64), $"hopper{i}")); // label above, inventory numbers below need ~24px
            y += hopperHeight;
        }

        this.Inventory = new InventoryMenu(
            this.xPositionOnScreen + IClickableMenu.borderWidth + IClickableMenu.spaceToClearSideBorder,
            y,
            playerInventory: true,
            highlightMethod: this.CanClick,
            capacity: rows * 12, // show the whole backpack (e.g. 48 slots with Bigger Backpack)
            rows: rows
        );
    }

    /// <summary>Get the screen area of an inventory slot.</summary>
    /// <param name="index">The inventory index.</param>
    public Rectangle GetInventorySlotBounds(int index)
    {
        return this.Inventory.inventory[index].bounds;
    }

    /// <summary>Get the screen area of a hopper slot.</summary>
    /// <param name="index">The hopper slot index.</param>
    public Rectangle GetHopperSlotBounds(int index)
    {
        return this.HopperSlots[index].bounds;
    }

    /// <summary>Get the screen area of the rear hitch zone. For tests.</summary>
    public Rectangle GetRearZone()
    {
        return this.RearZone;
    }

    /// <inheritdoc />
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        Farmer player = Game1.player;
        this.Cursor = new Point(x, y);

        // remove the attached implement
        if (this.GetAttachedArea() is { } attached && attached.Contains(x, y))
        {
            ImplementManager.TryUnequipTo(this.Tractor, player);
            this.AnimImplement = null;
            Game1.playSound("dwop");
            return;
        }

        // take from hopper
        if (this.IsSeederEquipped())
        {
            for (int i = 0; i < this.HopperSlots.Count; i++)
            {
                if (!this.HopperSlots[i].containsPoint(x, y))
                    continue;

                Item? item = SeederHopper.Get(this.Tractor)[i];
                if (item == null)
                    return;
                if (player.couldInventoryAcceptThisItem(item))
                {
                    SeederHopper.Take(this.Tractor, i);
                    player.addItemToInventoryBool(item);
                    Game1.playSound("dwop");
                }
                else
                    Game1.showRedMessage(I18n.ImplementMenu_InventoryFull());
                return;
            }
        }

        // inventory
        for (int index = 0; index < this.Inventory.inventory.Count; index++)
        {
            if (!this.Inventory.inventory[index].containsPoint(x, y))
                continue;

            Item? item = index < player.Items.Count ? player.Items[index] : null;
            if (item == null)
                return;

            // put seeds or fertilizer in the hopper
            if (this.IsSeederEquipped() && SeederHopper.CanHold(item))
            {
                player.Items[index] = SeederHopper.Add(this.Tractor, item);
                Game1.playSound("Ship");
                return;
            }

            // start dragging an implement (a click without moving attaches it on release)
            if (ImplementManager.IsImplement(item))
            {
                this.DragIndex = index;
                this.DragStart = new Point(x, y);
                Game1.playSound("pickUpItem");
            }
            return;
        }
    }

    /// <inheritdoc />
    public override void leftClickHeld(int x, int y)
    {
        base.leftClickHeld(x, y);
        this.Cursor = new Point(x, y);
    }

    /// <inheritdoc />
    public override void releaseLeftClick(int x, int y)
    {
        base.releaseLeftClick(x, y);
        if (this.DragIndex is not { } index)
            return;
        this.DragIndex = null;

        bool isClick = Math.Abs(x - this.DragStart.X) < 10 && Math.Abs(y - this.DragStart.Y) < 10;
        bool overBlueprint = this.Blueprint.Contains(x, y);
        if (!isClick && !overBlueprint)
            return; // dropped elsewhere: nothing happens

        Vector2 from = isClick
            ? new Vector2(this.Inventory.inventory[index].bounds.X, this.Inventory.inventory[index].bounds.Y)
            : new Vector2(x - 32, y - 32);
        this.Attach(index, from);
    }

    /// <inheritdoc />
    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        this.receiveLeftClick(x, y, playSound);
        this.releaseLeftClick(x, y);
    }

    /// <inheritdoc />
    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.Cursor = new Point(x, y);

        this.HoveredItem = null;
        if (this.GetAttachedArea() is { } attached && attached.Contains(x, y))
            this.HoveredItem = ImplementManager.GetEquippedItem(this.Tractor);
        else if (this.IsSeederEquipped())
        {
            for (int i = 0; i < this.HopperSlots.Count; i++)
            {
                if (this.HopperSlots[i].containsPoint(x, y))
                    this.HoveredItem = SeederHopper.Get(this.Tractor)[i];
            }
        }
        this.HoveredItem ??= this.Inventory.hover(x, y, null);
    }

    /// <inheritdoc />
    public override void draw(SpriteBatch b)
    {
        if (this.IsSeederEquipped() != this.LayoutHasHopper && this.DragIndex == null)
            this.Relayout();

        // background + frame
        if (!Game1.options.showClearBackgrounds)
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.6f);
        Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, speaker: false, drawOnlyBox: true);
        SpriteText.drawStringWithScrollCenteredAt(b, this.Title, this.xPositionOnScreen + this.width / 2, this.yPositionOnScreen - 64);

        this.DrawBlueprint(b);

        // implement name / hint under the blueprint
        ImplementDefinition? equipped = ImplementManager.GetEquipped(this.Tractor);
        string label = equipped != null ? $"{ImplementManager.GetEquippedItem(this.Tractor)!.DisplayName}  ({I18n.ImplementMenu_ClickToRemove()})" : this.EmptyText;
        string wrapped = Game1.parseText(label, Game1.smallFont, this.Blueprint.Width);
        float lineY = this.Blueprint.Bottom + 12;
        foreach (string textLine in wrapped.Split('\n'))
        {
            Vector2 lineSize = Game1.smallFont.MeasureString(textLine);
            Utility.drawTextWithShadow(b, textLine, Game1.smallFont, new Vector2(this.xPositionOnScreen + (this.width - lineSize.X) / 2, lineY), Game1.textColor);
            lineY += lineSize.Y;
        }

        // seeder hopper
        if (this.IsSeederEquipped())
            this.DrawHopper(b);

        // inventory
        this.Inventory.draw(b);

        // dragged implement follows the cursor
        if (this.DragIndex is { } index && index < Game1.player.Items.Count && Game1.player.Items[index] is { } dragged)
            dragged.drawInMenu(b, new Vector2(this.Cursor.X - 32, this.Cursor.Y - 32), 1.1f);

        // tooltip + cursor
        base.draw(b);
        if (this.HoveredItem != null && this.DragIndex == null)
            IClickableMenu.drawToolTip(b, this.HoveredItem.getDescription(), this.HoveredItem.DisplayName, this.HoveredItem);
        this.drawMouse(b);
    }


    /*********
    ** Private methods
    *********/
    /// <summary>Attach the implement in an inventory slot, with the slide-in animation.</summary>
    /// <param name="index">The inventory index.</param>
    /// <param name="from">The screen position the animation starts from.</param>
    private void Attach(int index, Vector2 from)
    {
        Farmer player = Game1.player;
        Item? item = index < player.Items.Count ? player.Items[index] : null;
        if (!ImplementManager.IsImplement(item))
            return;

        Item single = item!.getOne();
        if (item.Stack > 1)
            item.Stack--;
        else
            player.Items[index] = null;

        if (ImplementManager.GetEquipped(this.Tractor) != null)
            ImplementManager.TryUnequipTo(this.Tractor, player);

        ImplementManager.SetEquipped(this.Tractor, single);
        this.AnimImplement = ImplementManager.GetEquipped(this.Tractor);
        this.AnimFrom = from;
        this.AnimTimer.Restart();
        Game1.playSound("pickUpItem");
    }

    /// <summary>Draw the blueprint panel with the tractor, the hitch zones and the attached implement.</summary>
    /// <param name="b">The sprite batch.</param>
    private void DrawBlueprint(SpriteBatch b)
    {
        Color paper = new(34, 64, 110), grid = new(48, 84, 136), line = new(160, 210, 245);
        Rectangle bp = this.Blueprint;
        b.Draw(Game1.staminaRect, bp, paper);
        for (int x = bp.X; x < bp.Right; x += 16)
            b.Draw(Game1.staminaRect, new Rectangle(x, bp.Y, 1, bp.Height), grid);
        for (int y = bp.Y; y < bp.Bottom; y += 16)
            b.Draw(Game1.staminaRect, new Rectangle(bp.X, y, bp.Width, 1), grid);
        this.DrawFrame(b, bp, line, 2);

        // ground line
        int groundY = this.TractorArea.Y + 31 * 4;
        this.DrawDashedLine(b, new Vector2(bp.X + 12, groundY), new Vector2(bp.Right - 12, groundY), line * 0.6f);

        ImplementDefinition? equipped = ImplementManager.GetEquipped(this.Tractor);

        // rear implements are behind the tractor, front ones in front of it
        if (equipped != null && ImplementRenderer.IsRearMounted(equipped.Kind))
            this.DrawAttached(b, equipped);

        // tractor (current colour, side view)
        Texture2D tractorTexture = this.Tractor.Sprite.Texture;
        b.Draw(tractorTexture, this.TractorArea, new Rectangle(0, 32, 32, 32), Color.White);

        if (equipped != null && !ImplementRenderer.IsRearMounted(equipped.Kind))
            this.DrawAttached(b, equipped);
        ImplementDefinition? dragged = this.DragIndex is { } i && i < Game1.player.Items.Count ? ImplementData.Get(Game1.player.Items[i]?.QualifiedItemId) : null;
        float pulse = 0.5f + 0.5f * (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 180);

        // hitch zones: dashed silhouettes + "attach here" arrow while empty
        foreach ((Rectangle zone, bool rear) in new[] { (this.RearZone, true), (this.FrontZone, false) })
        {
            bool zoneMatchesDrag = dragged != null && ImplementRenderer.IsRearMounted(dragged.Kind) == rear;
            bool zoneOccupied = equipped != null && ImplementRenderer.IsRearMounted(equipped.Kind) == rear;
            if (zoneOccupied)
                continue;

            Color zoneColor = zoneMatchesDrag ? Color.Lerp(line, Color.Yellow, pulse) : line;
            this.DrawDashedRect(b, zone, zoneColor);
            // dashed hitch link from the zone to the tractor
            int hitchY = this.TractorArea.Y + 22 * 4;
            if (rear)
                this.DrawDashedLine(b, new Vector2(zone.Right, hitchY), new Vector2(this.TractorArea.X + 2 * 4, hitchY), zoneColor);
            else
                this.DrawDashedLine(b, new Vector2(this.TractorArea.X + 30 * 4, hitchY), new Vector2(zone.X, hitchY), zoneColor);

            string caption = rear ? I18n.ImplementMenu_RearHitch() : I18n.ImplementMenu_FrontMount();
            const float captionScale = 0.5f;
            Vector2 capSize = Game1.tinyFont.MeasureString(caption) * captionScale;
            float capX = rear ? zone.Right - capSize.X : zone.X; // each caption grows away from the tractor, so they never overlap
            b.DrawString(Game1.tinyFont, caption, new Vector2(capX, zone.Y - capSize.Y - 6), line, 0f, Vector2.Zero, captionScale, SpriteEffects.None, 1f);
        }

        // pulsing arrow + "attach here" while nothing is attached
        if (equipped == null)
        {
            Rectangle target = dragged != null && !ImplementRenderer.IsRearMounted(dragged.Kind) ? this.FrontZone : this.RearZone;
            int bob = (int)(pulse * 6);
            Vector2 tip = new(target.Center.X, target.Y + 50 + bob);
            this.DrawArrowDown(b, tip, Color.Lerp(Color.White, Color.Yellow, pulse));
            string hint = I18n.ImplementMenu_AttachHere();
            float hintScale = Math.Min(1f, (target.Width - 8) / Game1.smallFont.MeasureString(hint).X);
            Vector2 hintSize = Game1.smallFont.MeasureString(hint) * hintScale;
            Utility.drawTextWithShadow(b, hint, Game1.smallFont, new Vector2(target.Center.X - hintSize.X / 2, target.Bottom - hintSize.Y - 6), Color.White, hintScale);
        }

        this.DrawSparks(b);
    }

    /// <summary>Draw the attached implement, sliding in while the attach animation runs.</summary>
    private void DrawAttached(SpriteBatch b, ImplementDefinition equipped)
    {
        if (ImplementRenderer.GetSideTexture(equipped) is { } texture)
        {
            Vector2 target = this.GetAttachPosition(equipped, texture);
            Vector2 drawPos = target;
            if (this.AnimImplement == equipped && this.AnimTimer.IsRunning)
            {
                double t = Math.Min(1, this.AnimTimer.Elapsed.TotalMilliseconds / AnimMs);
                double eased = 1 - Math.Pow(1 - t, 3);
                drawPos = Vector2.Lerp(this.AnimFrom, target, (float)eased);
                if (t >= 1)
                {
                    this.AnimTimer.Reset();
                    this.OnLocked(target, texture, equipped);
                }
            }
            b.Draw(texture, drawPos, null, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
        }
    }

    /// <summary>Lock effect: metal clank and sparks at the hitch point.</summary>
    private void OnLocked(Vector2 target, Texture2D texture, ImplementDefinition implement)
    {
        Game1.playSound("parry");
        Game1.playSound("hammer");
        Vector2 hitch = ImplementRenderer.IsRearMounted(implement.Kind)
            ? new Vector2(target.X + texture.Width * 4, this.TractorArea.Y + 22 * 4)
            : new Vector2(target.X, this.TractorArea.Y + 22 * 4);
        Random random = Game1.random;
        for (int i = 0; i < 14; i++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            float speed = 1.5f + (float)random.NextDouble() * 3.5f;
            this.Sparks.Add((hitch, new Vector2((float)Math.Cos(angle) * speed, (float)Math.Sin(angle) * speed - 1.5f), 1f));
        }
        this.LastSparkUpdate = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
    }

    /// <summary>Update and draw the spark particles.</summary>
    private void DrawSparks(SpriteBatch b)
    {
        if (this.Sparks.Count == 0)
            return;

        double now = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
        float dt = (float)Math.Min(50, now - this.LastSparkUpdate) / 16f;
        this.LastSparkUpdate = now;
        for (int i = this.Sparks.Count - 1; i >= 0; i--)
        {
            (Vector2 pos, Vector2 vel, float life) = this.Sparks[i];
            pos += vel * dt;
            vel.Y += 0.25f * dt;
            life -= 0.035f * dt;
            if (life <= 0)
            {
                this.Sparks.RemoveAt(i);
                continue;
            }
            this.Sparks[i] = (pos, vel, life);
            Color c = Color.Lerp(new Color(255, 160, 40), Color.White, life);
            b.Draw(Game1.staminaRect, new Rectangle((int)pos.X, (int)pos.Y, 4, 4), c * life);
        }
    }

    /// <summary>Get where an implement's side sprite is drawn when attached.</summary>
    private Vector2 GetAttachPosition(ImplementDefinition implement, Texture2D texture)
    {
        float x = ImplementRenderer.IsRearMounted(implement.Kind) ? 5 - texture.Width : 27;
        float y = 31 - texture.Height;
        return new Vector2(this.TractorArea.X + x * 4, this.TractorArea.Y + y * 4);
    }

    /// <summary>Get the screen area of the attached implement, if any.</summary>
    private Rectangle? GetAttachedArea()
    {
        if (ImplementManager.GetEquipped(this.Tractor) is not { } implement || ImplementRenderer.GetSideTexture(implement) is not { } texture)
            return null;
        Vector2 pos = this.GetAttachPosition(implement, texture);
        return new Rectangle((int)pos.X, (int)pos.Y, texture.Width * 4, texture.Height * 4);
    }

    /// <summary>Draw the seeder hopper row.</summary>
    private void DrawHopper(SpriteBatch b)
    {
        string hopperLabel = I18n.ImplementMenu_Hopper();
        Vector2 hopperSize = Game1.smallFont.MeasureString(hopperLabel);
        Vector2 labelPos = new(this.xPositionOnScreen + (this.width - hopperSize.X) / 2, this.HopperSlots[0].bounds.Y - 36);
        Utility.drawTextWithShadow(b, hopperLabel, Game1.smallFont, labelPos, Game1.textColor);

        // seed sack icon before the label (pixel art, 3px per pixel)
        string[] sack = [".O.O.", "OOOOO", "OSSSO", "OSKSO", "OSSSO", ".OOO."];
        for (int y = 0; y < sack.Length; y++)
        {
            for (int x = 0; x < sack[y].Length; x++)
            {
                Color? c = sack[y][x] switch { 'O' => new Color(42, 26, 12), 'S' => new Color(220, 190, 130), 'K' => new Color(122, 90, 32), _ => null };
                if (c != null)
                    b.Draw(Game1.staminaRect, new Rectangle((int)labelPos.X - 22 + x * 3, (int)labelPos.Y + 4 + y * 3, 3, 3), c.Value);
            }
        }

        Item?[] items = SeederHopper.Get(this.Tractor);
        for (int i = 0; i < this.HopperSlots.Count; i++)
        {
            Rectangle bounds = this.HopperSlots[i].bounds;
            b.Draw(Game1.menuTexture, bounds, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);
            if (items[i] is { Stack: > 0 } item)
                item.drawInMenu(b, new Vector2(bounds.X, bounds.Y), 1f);
        }
    }

    /// <summary>Draw a rectangle outline.</summary>
    private void DrawFrame(SpriteBatch b, Rectangle r, Color color, int thickness)
    {
        b.Draw(Game1.staminaRect, new Rectangle(r.X, r.Y, r.Width, thickness), color);
        b.Draw(Game1.staminaRect, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), color);
        b.Draw(Game1.staminaRect, new Rectangle(r.X, r.Y, thickness, r.Height), color);
        b.Draw(Game1.staminaRect, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), color);
    }

    /// <summary>Draw a dashed rectangle outline (blueprint style).</summary>
    private void DrawDashedRect(SpriteBatch b, Rectangle r, Color color)
    {
        this.DrawDashedLine(b, new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), color);
        this.DrawDashedLine(b, new Vector2(r.X, r.Bottom), new Vector2(r.Right, r.Bottom), color);
        this.DrawDashedLine(b, new Vector2(r.X, r.Y), new Vector2(r.X, r.Bottom), color);
        this.DrawDashedLine(b, new Vector2(r.Right, r.Y), new Vector2(r.Right, r.Bottom), color);
    }

    /// <summary>Draw a dashed horizontal or vertical line.</summary>
    private void DrawDashedLine(SpriteBatch b, Vector2 from, Vector2 to, Color color)
    {
        Vector2 delta = to - from;
        float length = delta.Length();
        if (length < 1)
            return;
        Vector2 dir = delta / length;
        for (float t = 0; t < length; t += 12)
        {
            Vector2 p = from + dir * t;
            float seg = Math.Min(6, length - t);
            if (Math.Abs(dir.X) >= Math.Abs(dir.Y))
                b.Draw(Game1.staminaRect, new Rectangle((int)(dir.X >= 0 ? p.X : p.X - seg), (int)p.Y - 1, (int)seg, 2), color);
            else
                b.Draw(Game1.staminaRect, new Rectangle((int)p.X - 1, (int)(dir.Y >= 0 ? p.Y : p.Y - seg), 2, (int)seg), color);
        }
    }

    /// <summary>Draw a downward arrow whose tip is at the given point.</summary>
    private void DrawArrowDown(SpriteBatch b, Vector2 tip, Color color)
    {
        Color outline = new(20, 30, 50);
        for (int row = 0; row < 7; row++)
        {
            int half = row + 1;
            b.Draw(Game1.staminaRect, new Rectangle((int)tip.X - half * 2 - 2, (int)tip.Y - 14 - (6 - row) * 3 - 2, half * 4 + 4, 5), outline);
        }
        b.Draw(Game1.staminaRect, new Rectangle((int)tip.X - 5, (int)tip.Y - 46, 10, 16), outline);
        for (int row = 0; row < 7; row++)
        {
            int half = row + 1;
            b.Draw(Game1.staminaRect, new Rectangle((int)tip.X - half * 2, (int)tip.Y - 14 - (6 - row) * 3, half * 4, 3), color);
        }
        b.Draw(Game1.staminaRect, new Rectangle((int)tip.X - 3, (int)tip.Y - 44, 6, 14), color);
    }

    /// <summary>Get the number of inventory rows to show for the player's backpack size.</summary>
    private static int GetRows()
    {
        return Math.Max(3, (Game1.player.MaxItems + 11) / 12);
    }

    /// <summary>Get whether the seeder is equipped.</summary>
    private bool IsSeederEquipped()
    {
        return ImplementManager.GetEquipped(this.Tractor)?.Kind == ImplementKind.Seeder;
    }

    /// <summary>Get whether an inventory item can be clicked in this menu.</summary>
    private bool CanClick(Item item)
    {
        return ImplementManager.IsImplement(item) || (this.IsSeederEquipped() && SeederHopper.CanHold(item));
    }
}
