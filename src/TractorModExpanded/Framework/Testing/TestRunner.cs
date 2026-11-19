using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.TractorMod.Framework.Implements;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.TractorMod.Framework.Testing;

/// <summary>The settings in <c>test-mode.json</c>. The test mode only runs when that file exists.</summary>
internal class TestModeSettings
{
    /// <summary>The save folder name to load. The save is never written.</summary>
    public string Save { get; set; } = "";

    /// <summary>The test names to run, or empty for all.</summary>
    public string[] Only { get; set; } = [];
}

/// <summary>What the test runner needs from the mod.</summary>
/// <param name="Implements">Manages implements.</param>
/// <param name="Refill">Manages water tank refills.</param>
/// <param name="DismissTractor">Send a tractor home.</param>
/// <param name="ApplyTractorInfo">Mark a horse as a tractor and apply its textures.</param>
/// <param name="IsRidingTractor">Whether the current player is riding a tractor.</param>
/// <param name="GetConfig">Get the mod settings.</param>
/// <param name="SetSkin">Change the tractor colour as if the player changed it in the config.</param>
internal record TestContext(ImplementManager Implements, WaterTankRefill Refill, Action<Horse> DismissTractor, Action<Horse> ApplyTractorInfo, Func<bool> IsRidingTractor, Func<ModConfig> GetConfig, Action<TractorSkin> SetSkin);

/// <summary>Loads the test save, runs automated tractor tests, writes the results and closes the game without saving.</summary>
/// <remarks>
/// Safety: the game only writes a save when the day ends or the player saves; the tests never sleep or save, and the
/// game is closed with <see cref="Game.Exit"/>. All world changes happen in memory only.
/// </remarks>
internal sealed class TestRunner
{
    /*********
    ** Fields
    *********/
    private readonly TestModeSettings Settings;
    private readonly TestContext Context;
    private readonly IMonitor Monitor;
    private readonly string OutDir;
    private readonly List<string> Lines = [];
    private IEnumerator<int>? Routine;
    private int WaitTicks;
    private int TitleTicks;
    private bool RequestedLoad;
    private bool Finished;
    private bool? OriginalPauseWhenOutOfFocus;
    private string? PendingScreenshot;
    private string? PendingMenuCapture;
    private int Passed;
    private int Failed;

    /// <summary>The test area center on the farm.</summary>
    private Vector2 Center;

    /// <summary>The tractor used by the tests.</summary>
    private Horse Tractor = null!;


    /*********
    ** Public methods
    *********/
    /// <summary>Load the test settings if the test mode file exists.</summary>
    /// <param name="modDir">The mod folder.</param>
    /// <param name="monitor">Writes to the SMAPI log.</param>
    /// <param name="context">What the test runner needs from the mod.</param>
    public static TestRunner? TryCreate(string modDir, IMonitor monitor, TestContext context)
    {
        string path = Path.Combine(modDir, "test-mode.json");
        if (!File.Exists(path))
            return null;

        // a leftover file (e.g. the test script was interrupted) must never hijack a normal game launch
        if (DateTime.Now - File.GetLastWriteTime(path) > TimeSpan.FromMinutes(10))
        {
            File.Delete(path);
            monitor.Log("Removed a stale test-mode.json (older than 10 minutes); test mode stays off.", LogLevel.Warn);
            return null;
        }

        TestModeSettings? settings = Newtonsoft.Json.JsonConvert.DeserializeObject<TestModeSettings>(File.ReadAllText(path));
        if (settings == null || string.IsNullOrWhiteSpace(settings.Save))
        {
            monitor.Log("Test mode file found, but it has no save name. Test mode disabled.", LogLevel.Warn);
            return null;
        }

        monitor.Log($"TEST MODE: will load save '{settings.Save}' (never saved) and run the automated tests.", LogLevel.Alert);
        return new TestRunner(settings, context, monitor, Path.Combine(modDir, "test-results"));
    }

    /// <summary>Update the test state. Call on every update tick.</summary>
    public void OnUpdateTicked()
    {
        if (this.Finished)
            return;

        // load the test save from the title screen
        if (!StardewModdingAPI.Context.IsWorldReady)
        {
            if (!this.RequestedLoad && Game1.activeClickableMenu is TitleMenu && ++this.TitleTicks > 60)
            {
                this.RequestedLoad = true;
                this.Log($"Loading save {this.Settings.Save}...");
                Game1.activeClickableMenu = null;
                SaveGame.Load(this.Settings.Save);
            }
            return;
        }

        // keep running while the window isn't focused
        if (this.OriginalPauseWhenOutOfFocus == null)
        {
            this.OriginalPauseWhenOutOfFocus = Game1.options.pauseWhenOutOfFocus;
            Game1.options.pauseWhenOutOfFocus = false;
        }

        // run tests
        this.Routine ??= this.RunAll().GetEnumerator();
        if (this.WaitTicks > 0)
        {
            this.WaitTicks--;
            return;
        }

        bool hasNext;
        try
        {
            hasNext = this.Routine.MoveNext();
        }
        catch (Exception ex)
        {
            this.Fail("test runner", $"crashed: {ex}");
            hasNext = false;
        }

        if (hasNext)
            this.WaitTicks = this.Routine.Current;
        else
            this.Finish();
    }

    /// <summary>Capture a screenshot if one was requested. Call after the game renders.</summary>
    public void OnRendered()
    {
        if (this.PendingMenuCapture != null)
            this.CaptureMenu();

        if (this.PendingScreenshot == null)
            return;

        string name = this.PendingScreenshot;
        this.PendingScreenshot = null;
        try
        {
            GraphicsDevice device = Game1.graphics.GraphicsDevice;
            int width = device.PresentationParameters.BackBufferWidth;
            int height = device.PresentationParameters.BackBufferHeight;
            Color[] data = new Color[width * height];
            device.GetBackBufferData(data);
            for (int i = 0; i < data.Length; i++)
                data[i].A = 255;

            // OpenGL returns the back buffer bottom-up: flip it so the image isn't upside down
            Color[] row = new Color[width];
            for (int y = 0; y < height / 2; y++)
            {
                Array.Copy(data, y * width, row, 0, width);
                Array.Copy(data, (height - 1 - y) * width, data, y * width, width);
                Array.Copy(row, 0, data, (height - 1 - y) * width, width);
            }

            using Texture2D texture = new(device, width, height);
            texture.SetData(data);
            using FileStream stream = File.Create(Path.Combine(this.OutDir, name + ".png"));
            texture.SaveAsPng(stream, width, height);
            this.Log($"Screenshot saved: {name}.png");
        }
        catch (Exception ex)
        {
            this.Log($"Screenshot {name} failed: {ex.Message}");
        }
    }


    /*********
    ** Private methods
    *********/
    private TestRunner(TestModeSettings settings, TestContext context, IMonitor monitor, string outDir)
    {
        this.Settings = settings;
        this.Context = context;
        this.Monitor = monitor;
        this.OutDir = outDir;

        Directory.CreateDirectory(outDir);
        foreach (string file in Directory.GetFiles(outDir))
            File.Delete(file);
        this.Log($"Test run started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>Run all tests. Each yielded value is the number of ticks to wait.</summary>
    private IEnumerable<int> RunAll()
    {
        yield return 60;
        foreach (int wait in this.Setup())
            yield return wait;

        (string Name, Func<IEnumerable<int>> Run)[] tests =
        [
            ("dump-crops", this.DumpCrops),
            ("ie-discovery", this.DiscoverItemExtensions),
            ("item-extensions-bridge", this.TestItemExtensionsBridge),
            ("held-tool-experiment", this.HeldToolExperiment),
            ("tractor-skin", this.TestTractorSkin),
            ("shops", this.TestShops),
            ("robin-shop-tab", this.TestRobinShopTab),
            ("rider-fit", this.TestRiderFit),
            ("implement-draw", this.TestImplementDraw),
            ("implement-animation", this.TestImplementAnimation),
            ("garage-skin", this.TestGarageSkin),
            ("held-tool-does-nothing", this.TestHeldToolDoesNothing),
            ("plow", this.TestPlow),
            ("rock-breaker-1", () => this.TestRockBreaker(1)),
            ("rock-breaker-2", () => this.TestRockBreaker(2)),
            ("wood-cutter-1", () => this.TestWoodCutter(1)),
            ("wood-cutter-2", () => this.TestWoodCutter(2)),
            ("water-tank", this.TestWaterTank),
            ("water-tank-refill", this.TestRefill),
            ("implement-menu", this.TestMenu),
            ("dismiss-returns-implement", this.TestDismiss),
            ("dismount-returns-implement", this.TestDismount),
            ("dismiss-full-inventory-drops", this.TestDismissFullInventory),
            ("harvester-produce", () => this.TestCropHarvester(grain: false)),
            ("harvester-grain", () => this.TestCropHarvester(grain: true)),
            ("harvester-bush", this.TestBushHarvester),
            ("harvester-fruit-tree", this.TestFruitTreeHarvester),
            ("mower", this.TestMower),
            ("seeder", this.TestSeeder),
            ("seeder-menu", this.TestSeederMenu),
            ("dismiss-returns-hopper", this.TestDismissReturnsHopper)
        ];

        foreach ((string name, Func<IEnumerable<int>> run) in tests)
        {
            if (this.Settings.Only.Length > 0 && !this.Settings.Only.Contains(name))
                continue;

            this.Log($"--- {name}");
            IEnumerator<int> steps;
            try
            {
                steps = run().GetEnumerator();
            }
            catch (Exception ex)
            {
                this.Fail(name, $"crashed: {ex}");
                continue;
            }

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = steps.MoveNext();
                }
                catch (Exception ex)
                {
                    this.Fail(name, $"crashed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    break;
                }

                if (!hasNext)
                    break;
                yield return steps.Current;
            }

            // reset between tests
            foreach (int wait in this.ResetArea())
                yield return wait;
        }
    }

    /// <summary>Find an empty test area on the farm, warp there and mount a new tractor.</summary>
    private IEnumerable<int> Setup()
    {
        foreach (int wait in this.ClearBlockers())
            yield return wait;

        Farm farm = Game1.getFarm();
        Vector2? center = this.FindEmptyArea(farm, radius: 3);
        if (center == null)
            throw new InvalidOperationException("No empty 7x7 diggable area found on the farm.");
        this.Center = center.Value;
        this.Log($"Test area center: {this.Center}");
        foreach (int wait in this.ResetArea())
            yield return wait; // the first-day farm is covered in debris; clear it in memory

        Game1.warpFarmer("Farm", (int)this.Center.X, (int)this.Center.Y, Game1.down);
        yield return 90;
        foreach (int wait in this.ClearBlockers())
            yield return wait;

        this.Tractor = new Horse(Guid.NewGuid(), (int)this.Center.X, (int)this.Center.Y);
        this.Context.ApplyTractorInfo(this.Tractor);
        farm.characters.Add(this.Tractor);
        this.Tractor.currentLocation = farm;
        yield return 10;

        foreach (int wait in this.Mount())
            yield return wait;

        this.Check("setup: riding tractor", this.Context.IsRidingTractor(), $"mount={Game1.player.mount?.Name}");
        yield return 30;
        this.Log($"debug: free={StardewModdingAPI.Context.IsPlayerFree} canMove={Game1.player.CanMove} paused={Game1.paused} menu={Game1.activeClickableMenu?.GetType().Name} dialogue={Game1.dialogueUp} event={Game1.eventUp} usingTool={Game1.player.UsingTool} freeze={Game1.player.freezePause} buffs=[{string.Join(",", Game1.player.buffs.AppliedBuffs.Keys)}] location={Game1.currentLocation?.Name} time={Game1.timeOfDay} season={Game1.currentSeason} day={Game1.dayOfMonth} hud={Game1.displayHUD} fade={Game1.globalFade}");
    }

    /// <summary>Close menus, letters, dialogues and skip events (e.g. an NPC visit when warping to the farm), so the tractor can work. In memory only.</summary>
    private IEnumerable<int> ClearBlockers()
    {
        for (int i = 0; i < 15 && (Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.eventUp || !StardewModdingAPI.Context.IsPlayerFree); i++)
        {
            this.Log($"debug: clearing {Game1.activeClickableMenu?.GetType().Name ?? "-"} dialogueUp={Game1.dialogueUp} event={Game1.CurrentEvent?.id ?? "-"} free={StardewModdingAPI.Context.IsPlayerFree}");
            if (Game1.CurrentEvent != null)
                Game1.CurrentEvent.skipEvent();
            if (Game1.activeClickableMenu != null)
                Game1.exitActiveMenu();
            Game1.dialogueUp = false;
            yield return 30;
        }
    }

    /// <summary>Mount the test tractor at the test area center.</summary>
    private IEnumerable<int> Mount()
    {
        Farmer player = Game1.player;
        if (player.mount != null)
        {
            player.mount.dismount();
            yield return 30;
        }

        foreach (int wait in this.ClearBlockers())
            yield return wait;
        Farm farm = Game1.getFarm();
        TractorManager.SetLocation(this.Tractor, farm, this.Center);
        player.Position = this.Center * Game1.tileSize;
        yield return 5;

        // mount directly (the click-to-mount path needs real input)
        this.Tractor.mounting.Value = false;
        this.Tractor.rider = player;
        player.mount = this.Tractor;
        player.Position = this.Center * Game1.tileSize;
        yield return 60;
        player.Halt();
        if (player.mount == null)
            this.Log($"mount failed at {this.Center}");
    }

    /// <summary>Write every loaded crop (including mod crops) to crops.tsv, to classify them for the harvesters.</summary>
    private IEnumerable<int> DumpCrops()
    {
        List<string> rows = ["seedId\tharvestId\tharvestName\tcategory\tharvestMethod\tseasons\tisRaised\tcontextTags"];
        foreach ((string seedId, StardewValley.GameData.Crops.CropData crop) in Game1.cropData)
        {
            var harvest = ItemRegistry.GetData(crop.HarvestItemId);
            string tags = harvest?.RawData is StardewValley.GameData.Objects.ObjectData obj && obj.ContextTags != null ? string.Join(",", obj.ContextTags) : "";
            rows.Add($"{seedId}\t{crop.HarvestItemId}\t{harvest?.InternalName}\t{harvest?.Category}\t{crop.HarvestMethod}\t{string.Join(",", crop.Seasons)}\t{crop.IsRaised}\t{tags}");
        }
        File.WriteAllLines(Path.Combine(this.OutDir, "crops.tsv"), rows);
        this.Log($"Dumped {rows.Count - 1} crops to crops.tsv");
        yield break;
    }

    private IEnumerable<int> TestShops()
    {
        var shops = DataLoader.Shops(Game1.content);
        this.Check("Clint's upgrade menu exists", shops.ContainsKey("ClintUpgrade"), $"shops={shops.Count}");
        if (shops.TryGetValue("ClintUpgrade", out var upgrade))
        {
            string[] upgrades = ["WateringTank2", "WateringTank3", "Pickaxe2", "WoodCutter2"];
            int found = upgrades.Count(id => upgrade.Items.Any(p => p.ItemId == ImplementData.QualifiedIdFor(id)));
            this.Check("implement upgrades are in Clint's upgrade menu", found == upgrades.Length, $"found={found}/{upgrades.Length}");
        }
        if (shops.TryGetValue("Blacksmith", out var blacksmith))
        {
            int inRegular = blacksmith.Items.Count(p => p.ItemId?.Contains(ImplementData.IdPrefix) == true);
            this.Check("no implement upgrades in Clint's regular shop", inRegular == 0, $"found={inRegular}");
        }
        if (shops.TryGetValue("Carpenter", out var robin))
        {
            int baseImplements = robin.Items.Count(p => p.ItemId?.Contains(ImplementData.IdPrefix) == true);
            this.Check("base implements are in Robin's shop", baseImplements == 10, $"found={baseImplements}/10");
        }
        yield break;
    }

    /// <summary>Open Robin's shop and photograph it, to check the implements tab (Compact Searchable Shop Menu).</summary>
    private IEnumerable<int> TestRobinShopTab()
    {
        bool hasTag = ItemContextTagManager.HasBaseTag(ImplementData.QualifiedIdFor("Plow"), "cssm_tab_tractor_implements");
        this.Check("implements carry the shop tab tag", hasTag, "");
        Utility.TryOpenShopMenu("Carpenter", "Robin");
        yield return 40;
        if (Game1.activeClickableMenu is ShopMenu shop)
        {
            this.Log($"debug: Robin shop tabs={shop.tabButtons?.Count ?? 0}: {string.Join(", ", shop.tabButtons?.Select(t => t.name) ?? [])}");
            this.MenuScreenshot("robin-shop");
            yield return 3;

            // click each tab and photograph the one that shows only implements
            for (int i = 0; i < (shop.tabButtons?.Count ?? 0); i++)
            {
                var tab = shop.tabButtons![i];
                shop.receiveLeftClick(tab.bounds.Center.X, tab.bounds.Center.Y);
                yield return 10;
                int shown = shop.forSale.Count;
                int implements = shop.forSale.Count(p => p is Item it && ImplementManager.IsImplement(it));
                this.Log($"debug: tab {i} '{tab.name}' shows {shown} items, {implements} implements");
                if (shown > 0 && implements == shown)
                {
                    this.Check("Robin's shop has a tab with only the implements", true, $"tab {i} '{tab.name}', {implements} implements");
                    this.MenuScreenshot("robin-shop-implements-tab");
                    yield return 3;
                    break;
                }
            }
        }
        else
            this.Fail("robin-shop-tab", $"shop didn't open: {Game1.activeClickableMenu?.GetType().Name}");
        Game1.exitActiveMenu();
        yield return 10;
    }

    private IEnumerable<int> TestTractorSkin()
    {
        int speed = Game1.player.buffs.AppliedBuffs.TryGetValue("Pathoschild.TractorMod", out Buff? buff) ? (int)buff.effects.Speed.Value : int.MinValue;
        this.Check("tractor speed is -4", speed == -4 && this.Context.GetConfig().TractorSpeed == -4, $"buffSpeed={speed} config={this.Context.GetConfig().TractorSpeed}");

        TractorSkin original = this.Context.GetConfig().TractorSkin;
        (TractorSkin Skin, Color Expected)[] cases =
        [
            (TractorSkin.Green, new Color(0x3E, 0x9A, 0x3A)),
            (TractorSkin.Red, new Color(0xC8, 0x38, 0x2A)),
            (TractorSkin.Blue, new Color(0x5A, 0xA9, 0xE6))
        ];

        foreach ((TractorSkin skin, Color expected) in cases)
        {
            this.Context.SetSkin(skin);
            yield return 180; // the mod reapplies textures every 30 ticks; make sure the colour sticks
            yield return 20;
            Color actual = this.GetTractorPixel(17, 32 + 17); // side view, hood body colour
            this.Check($"tractor skin {skin} is applied", actual == expected, $"pixel={actual} expected={expected} texture={this.Tractor.Sprite.textureName.Value}");
            this.Screenshot($"tractor-skin-{skin}");
            yield return 2;
        }

        this.Context.SetSkin(TractorSkin.Original);
        yield return 20;
        Color originalPixel = this.GetTractorPixel(17, 32 + 17);
        this.Check("tractor skin Original shows the old sprite", cases.All(c => c.Expected != originalPixel), $"pixel={originalPixel}");

        this.Context.SetSkin(original);
        yield return 10;
    }

    /// <summary>Photograph the rider on the tractor in each direction, with the original and the new sprite, to check the seat fit.</summary>
    private IEnumerable<int> TestRiderFit()
    {
        TractorSkin original = this.Context.GetConfig().TractorSkin;
        float originalZoom = Game1.options.desiredBaseZoomLevel;
        Game1.options.desiredBaseZoomLevel = 2f; // zoom in for the photos (in memory only, restored below)
        foreach (TractorSkin skin in new[] { TractorSkin.Original, TractorSkin.Green })
        {
            this.Context.SetSkin(skin);
            foreach ((int dir, string name) in new[] { (Game1.down, "down"), (Game1.right, "right"), (Game1.up, "up"), (Game1.left, "left") })
            {
                Game1.player.FacingDirection = dir;
                this.Tractor.FacingDirection = dir;
                yield return 40;
                this.Screenshot($"rider-{skin}-{name}");
                yield return 3;
            }
        }
        this.Context.SetSkin(original);
        Game1.options.desiredBaseZoomLevel = originalZoom;
        yield return 10;
    }

    /// <summary>Photograph implements attached to the tractor in each direction (with a calibration marker at the tractor position).</summary>
    private IEnumerable<int> TestImplementDraw()
    {
        float originalZoom = Game1.options.desiredBaseZoomLevel;
        Game1.options.desiredBaseZoomLevel = 2f; // in memory only, restored below
        ImplementRenderer.DrawCalibrationMarker = true;
        foreach (string implement in new[] { "Plow", "Seeder", "WateringTank1", "Mower", "WoodCutter1", "Pickaxe1", "HarvesterGrain", "HarvesterProduce", "HarvesterBush", "HarvesterFruitTree" })
        {
            this.Equip(implement);
            foreach ((int dir, string name) in new[] { (Game1.down, "down"), (Game1.right, "right"), (Game1.up, "up"), (Game1.left, "left") })
            {
                Game1.player.FacingDirection = dir;
                this.Tractor.FacingDirection = dir;
                yield return 30;
                this.Screenshot($"implement-{implement}-{name}");
                yield return 3;
            }
        }
        ImplementRenderer.DrawCalibrationMarker = false;
        Game1.options.desiredBaseZoomLevel = originalZoom;
        yield return 10;
    }

    /// <summary>Build a garage in memory (never saved), switch the colour and check its texture.</summary>
    private IEnumerable<int> TestGarageSkin()
    {
        Farm farm = Game1.getFarm();
        TractorSkin original = this.Context.GetConfig().TractorSkin;
        StardewValley.Buildings.Building garage = StardewValley.Buildings.Building.CreateInstanceFromId("Pathoschild.TractorMod_Stable", this.Center + new Vector2(-2, -6));
        garage.daysOfConstructionLeft.Value = 0;
        farm.buildings.Add(garage);
        yield return 20;

        (TractorSkin Skin, Color Expected)[] cases =
        [
            (TractorSkin.Green, new Color(0x3E, 0x9A, 0x3A)),
            (TractorSkin.Red, new Color(0xC8, 0x38, 0x2A)),
            (TractorSkin.Blue, new Color(0x5A, 0xA9, 0xE6))
        ];
        foreach ((TractorSkin skin, Color expected) in cases)
        {
            this.Context.SetSkin(skin);
            yield return 30;
            Texture2D texture = garage.texture.Value;
            Color[] data = new Color[1];
            texture.GetData(0, new Rectangle(31, 40, 1, 1), data, 0, 1); // tractor on the sign
            this.Check($"garage skin {skin} is applied", data[0] == expected, $"pixel={data[0]} expected={expected}");
            this.Screenshot($"garage-{skin}");
            yield return 2;
        }

        farm.buildings.Remove(garage);
        this.Context.SetSkin(original);
        yield return 10;
    }

    /// <summary>Check the working animation frames and the tank drain, and save them as pictures.</summary>
    private IEnumerable<int> TestImplementAnimation()
    {
        string dir = Path.Combine(this.OutDir, "animacao");
        Directory.CreateDirectory(dir);
        foreach (ImplementDefinition implement in ImplementData.All)
        {
            foreach (string view in new[] { "lado", "frontal" })
            {
                Texture2D? baseTexture = ImplementRenderer.GetTextureForTest($"{ImplementRenderer.GetSpriteName(implement)}_{view}");
                if (baseTexture == null)
                {
                    this.Fail("implement-animation", $"missing sprite for {implement.Id} {view}");
                    continue;
                }
                bool animates = implement.Kind is not (ImplementKind.Pickaxe or ImplementKind.Plow or ImplementKind.HarvesterFruitTree); // these move as a whole instead
                int changed = 0;
                for (int frame = 0; frame < ImplementAnimator.FrameCount; frame++)
                {
                    Texture2D texture = ImplementAnimator.GetTexture(baseTexture, implement.Kind, frame, null);
                    using (FileStream stream = File.Create(Path.Combine(dir, $"{implement.Id.Substring(ImplementData.IdPrefix.Length)}_{view}_q{frame}.png")))
                        texture.SaveAsPng(stream, texture.Width, texture.Height);
                    if (frame > 0 && !object.ReferenceEquals(texture, baseTexture) && this.CountDiff(texture, baseTexture) > 0)
                        changed++;
                }
                bool moves = ImplementAnimator.GetOffset(implement.Kind, 1) != Vector2.Zero || ImplementAnimator.GetOffset(implement.Kind, 2) != Vector2.Zero;
                if (animates)
                    this.Check($"{implement.Id} {view} animates while working", changed >= 1, $"changedFrames={changed}");
                else
                    this.Check($"{implement.Id} {view} moves while working", moves, "");

                // tank drains from the top down
                if (implement.Kind == ImplementKind.WateringTank)
                {
                    int previous = int.MaxValue;
                    bool decreasing = true;
                    foreach (float level in new[] { 1f, 0.66f, 0.33f, 0f })
                    {
                        Texture2D texture = ImplementAnimator.GetTexture(baseTexture, implement.Kind, 0, level);
                        int blue = this.CountBlue(texture);
                        using (FileStream stream = File.Create(Path.Combine(dir, $"{implement.Id.Substring(ImplementData.IdPrefix.Length)}_{view}_agua{(int)(level * 100)}.png")))
                            texture.SaveAsPng(stream, texture.Width, texture.Height);
                        decreasing &= blue < previous || (level == 1f);
                        previous = blue;
                    }
                    this.Check($"{implement.Id} {view} tank empties visually", decreasing && previous <= 2, $"blueAtEmpty={previous}");
                }
            }
            yield return 1;
        }

        // photo of everything working at once (in-world)
        ImplementAnimator.ForceWorking = true;
        foreach (string implement in new[] { "WoodCutter1", "Mower", "HarvesterGrain" })
        {
            this.Equip(implement);
            Game1.player.FacingDirection = Game1.right;
            this.Tractor.FacingDirection = Game1.right;
            for (int f = 0; f < 2; f++)
            {
                yield return 7;
                this.Screenshot($"animacao-{implement}-{f}");
                yield return 2;
            }
        }
        ImplementAnimator.ForceWorking = false;
    }

    private int CountDiff(Texture2D a, Texture2D b)
    {
        Color[] da = new Color[a.Width * a.Height], db = new Color[b.Width * b.Height];
        a.GetData(da);
        b.GetData(db);
        int diff = 0;
        for (int i = 0; i < Math.Min(da.Length, db.Length); i++)
        {
            if (da[i] != db[i])
                diff++;
        }
        return diff;
    }

    private int CountBlue(Texture2D texture)
    {
        Color[] data = new Color[texture.Width * texture.Height];
        texture.GetData(data);
        return data.Count(ImplementAnimator.IsWaterColor);
    }

    /// <summary>Read one pixel of the tractor's current texture.</summary>
    private Color GetTractorPixel(int x, int y)
    {
        Texture2D texture = this.Tractor.Sprite.Texture;
        Color[] data = new Color[1];
        texture.GetData(0, new Rectangle(x, y, 1, 1), data, 0, 1);
        return data[0];
    }

    /// <summary>Find out whether a tool held in the hand still acts while riding (it shouldn't: only implements work).</summary>
    private IEnumerable<int> HeldToolExperiment()
    {
        Farm farm = Game1.getFarm();
        Farmer player = Game1.player;
        ImplementManager.SetEquipped(this.Tractor, null);
        foreach (string toolName in new[] { "WateringCan", "Hoe", "Pickaxe", "Axe", "Scythe" })
        {
            int index = player.Items.ToList().FindIndex(p => p is Tool t && (t.GetType().Name == toolName || (toolName == "Scythe" && p is MeleeWeapon w && w.isScythe())));
            if (index < 0 || index >= 12)
            {
                this.Log($"experiment: {toolName} not in toolbar");
                continue;
            }
            foreach (Vector2 tile in this.Ring())
                farm.terrainFeatures[tile] = new HoeDirt(HoeDirt.dry, farm);
            farm.objects[this.Center + new Vector2(1, 0)] = ItemRegistry.Create<SObject>("(O)343");
            player.CurrentToolIndex = index;
            yield return 120;
            int watered = this.Ring().Count(tile => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? f) && f is HoeDirt { state.Value: HoeDirt.watered });
            bool stoneLeft = farm.objects.ContainsKey(this.Center + new Vector2(1, 0));
            int dirtLeft = this.Ring().Count(tile => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? f) && f is HoeDirt);
            this.Log($"experiment: holding {toolName} (slot {index}) without implement -> watered={watered}, stoneLeft={stoneLeft}, dirtLeft={dirtLeft}");
            foreach (int wait in this.ResetArea())
                yield return wait;
        }
    }

    /// <summary>List Item Extensions' tractor-related types and methods (to call its tractor patches with our own mod ID).</summary>
    private IEnumerable<int> DiscoverItemExtensions()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.GetName().Name!.Contains("ItemExtensions", StringComparison.OrdinalIgnoreCase))
                continue;
            this.Log($"debug: IE assembly {assembly.GetName().Name} {assembly.GetName().Version}");
            Type[] types;
            try { types = assembly.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (Type type in types)
            {
                bool tractorType = type.FullName!.Contains("Tractor", StringComparison.OrdinalIgnoreCase);
                foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (tractorType || method.Name.Contains("Tractor", StringComparison.OrdinalIgnoreCase))
                        this.Log($"debug: IE {type.FullName}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) static={method.IsStatic}");
                }
            }
        }
        yield break;
    }

    private IEnumerable<int> TestHeldToolDoesNothing()
    {
        ImplementManager.SetEquipped(this.Tractor, null);
        Farmer player = Game1.player;
        int hoeIndex = player.Items.ToList().FindIndex(p => p is Hoe);
        if (hoeIndex < 0 || hoeIndex >= 12)
        {
            this.Log("SKIP held-tool-does-nothing: no hoe in the toolbar.");
            yield break;
        }

        player.CurrentToolIndex = hoeIndex;
        yield return 120;

        int tilled = this.Ring().Count(tile => Game1.getFarm().terrainFeatures.ContainsKey(tile));
        this.Check("held hoe without implement tills nothing", tilled == 0, $"tilled={tilled}");
    }

    private IEnumerable<int> TestPlow()
    {
        Farm farm = Game1.getFarm();
        Vector2 weedTile = this.Center + new Vector2(1, 0);
        farm.objects.Add(weedTile, ItemRegistry.Create<SObject>("(O)674"));

        int effectsBefore = ImplementEffects.SpawnedCount;
        this.Equip("Plow");
        yield return 180;
        this.Check("plow shows work effects", ImplementEffects.SpawnedCount > effectsBefore, $"spawned={ImplementEffects.SpawnedCount - effectsBefore}");

        int tilled = this.Ring().Count(tile => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? f) && f is HoeDirt);
        this.Check("plow tills the soil around the tractor", tilled >= 6, $"tilled={tilled}/8");
        this.Check("plow clears weeds", !farm.objects.ContainsKey(weedTile), $"weedsLeft={farm.objects.ContainsKey(weedTile)}");
        this.Screenshot("plow");
        yield return 2;
    }

    private IEnumerable<int> TestRockBreaker(int tier)
    {
        Farm farm = Game1.getFarm();
        Vector2[] stoneTiles = [this.Center + new Vector2(-1, 0), this.Center + new Vector2(0, -1), this.Center + new Vector2(-1, -1)];
        foreach (Vector2 tile in stoneTiles)
            farm.objects.Add(tile, ItemRegistry.Create<SObject>("(O)343"));
        ResourceClump boulder = new(ResourceClump.boulderIndex, 2, 2, this.Center + new Vector2(1, 1));
        farm.resourceClumps.Add(boulder);

        this.Equip(tier >= 2 ? "Pickaxe2" : "Pickaxe1");
        yield return 360;

        int stonesLeft = stoneTiles.Count(tile => farm.objects.ContainsKey(tile));
        bool boulderLeft = farm.resourceClumps.Contains(boulder);
        this.Check($"rock breaker {tier} breaks stones", stonesLeft == 0, $"stonesLeft={stonesLeft}/3");
        if (tier >= 2)
            this.Check("heavy rock breaker breaks boulders", !boulderLeft, $"boulderLeft={boulderLeft}");
        else
            this.Check("rock breaker 1 does not break boulders", boulderLeft, $"boulderLeft={boulderLeft}");
    }

    private IEnumerable<int> TestWoodCutter(int tier)
    {
        Farm farm = Game1.getFarm();
        // wild trees at every stage, a twig, a grown fruit tree, and level-gated clumps
        Vector2 twigTile = this.Center + new Vector2(-1, 0);
        farm.objects.Add(twigTile, ItemRegistry.Create<SObject>("(O)294"));
        Vector2 grownTile = this.Center + new Vector2(0, -1);
        farm.terrainFeatures.Add(grownTile, new Tree("1", Tree.treeStage));
        (Vector2 Tile, int Stage)[] young = [(this.Center + new Vector2(-1, -1), Tree.seedStage), (this.Center + new Vector2(1, -1), 2), (this.Center + new Vector2(1, 0), 4)];
        foreach ((Vector2 tile, int stage) in young)
            farm.terrainFeatures.Add(tile, new Tree("1", stage));
        Vector2 fruitTile = this.Center + new Vector2(0, 1);
        FruitTree fruitTree = new("628", FruitTree.treeStage);
        farm.terrainFeatures.Add(fruitTile, fruitTree);
        int fruitHealth = (int)fruitTree.health.Value;
        ResourceClump stump = new(ResourceClump.stumpIndex, 2, 2, this.Center + new Vector2(1, 1));
        ResourceClump log = new(ResourceClump.hollowLogIndex, 2, 2, this.Center + new Vector2(-2, 1));
        farm.resourceClumps.Add(stump);
        farm.resourceClumps.Add(log);

        this.Equip(tier >= 2 ? "WoodCutter2" : "WoodCutter1");
        yield return 600;

        bool twigLeft = farm.objects.ContainsKey(twigTile);
        bool grownCut = !farm.terrainFeatures.TryGetValue(grownTile, out TerrainFeature? feature) || feature is not Tree t || t.stump.Value || t.health.Value < Tree.startingHealth;
        int youngLeft = young.Count(p => farm.terrainFeatures.TryGetValue(p.Tile, out TerrainFeature? f) && f is Tree);
        bool fruitIntact = farm.terrainFeatures.TryGetValue(fruitTile, out TerrainFeature? ft) && ft is FruitTree fruit && (int)fruit.health.Value == fruitHealth;
        bool stumpLeft = farm.resourceClumps.Contains(stump), logLeft = farm.resourceClumps.Contains(log);

        this.Check($"wood cutter {tier} clears twigs", !twigLeft, $"twigLeft={twigLeft}");
        this.Check($"wood cutter {tier} cuts grown trees", grownCut, $"tree={(feature as Tree)?.health.Value.ToString() ?? "gone"}");
        this.Check($"wood cutter {tier} cuts seeds, saplings and young trees", youngLeft == 0, $"left={youngLeft}/3");
        this.Check($"wood cutter {tier} never cuts fruit trees", fruitIntact, $"fruitTree={(ft is FruitTree ? "present" : "gone")}");
        if (tier >= 2)
            this.Check("heavy wood cutter clears large stumps and hollow logs", !stumpLeft && !logLeft, $"stumpLeft={stumpLeft} logLeft={logLeft}");
        else
            this.Check("wood cutter 1 leaves large stumps and hollow logs", stumpLeft && logLeft, $"stumpLeft={stumpLeft} logLeft={logLeft}");
        this.Screenshot($"wood-cutter-{tier}");
        yield return 2;
    }

    private IEnumerable<int> TestWaterTank()
    {
        Farm farm = Game1.getFarm();
        Vector2 originalCenter = this.Center;
        int CountWatered() => this.Ring().Count(tile => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? f) && f is HoeDirt { state.Value: HoeDirt.watered });
        void ResetSoil() { foreach (Vector2 tile in this.Ring()) farm.terrainFeatures[tile] = new HoeDirt(HoeDirt.dry, farm); }

        // find an area nothing else waters (the save may have sprinklers from other mods): an empty tank must water nothing
        int baseline = -1;
        TractorWaterTank tank = null!;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                Vector2 previous = this.Center;
                Vector2? next = this.FindEmptyArea(farm, 3, c => Vector2.Distance(c, previous) > 12 && Vector2.Distance(c, originalCenter) > 12);
                if (next == null)
                    break;
                this.Center = next.Value;
                foreach (int wait in this.ResetArea())
                    yield return wait;
                foreach (int wait in this.Mount())
                    yield return wait;
            }
            ResetSoil();
            this.Equip("WateringTank1");
            tank = this.Context.Implements.GetTank(this.Tractor);
            tank.Water = 0;
            yield return 90;
            baseline = CountWatered();
            if (baseline == 0)
                break;
            this.Log($"note: area {this.Center} is watered by something else ({baseline} tiles, e.g. a sprinkler from another mod); trying another area");
        }
        if (baseline > 0)
        {
            // something waters every hoed tile (e.g. CJB Cheats 'AutoWater'): the tank can't be tested in this save
            this.Log($"SKIP water-tank: another mod waters all soil on this farm (e.g. CJB Cheats AutoWater is on), so the tank's water limit can't be checked here.");
        }
        else
        {
        ResetSoil();
        tank.Water = 3;
        yield return 120;
        int watered = CountWatered();
        this.Check("tank waters only while it has water", watered == 3, $"watered={watered} (expected 3)");
        this.Check("tank is empty after watering", tank.Water == 0, $"water={tank.Water}");
        this.Screenshot("water-tank-empty");
        yield return 2;
        }

        // back to the main test area
        if (this.Center != originalCenter)
        {
            foreach (int wait in this.ResetArea())
                yield return wait;
            this.Center = originalCenter;
            foreach (int wait in this.Mount())
                yield return wait;
        }
    }

    private IEnumerable<int> TestRefill()
    {
        Farm farm = Game1.getFarm();
        (Vector2 Stand, int Facing)? spot = this.FindWaterSpot(farm);
        if (spot == null)
        {
            this.Fail("water-tank-refill", "no reachable water found on the farm");
            yield break;
        }

        // move the tractor next to the water
        Vector2 previousCenter = this.Center;
        this.Center = spot.Value.Stand;
        this.Log($"Water spot: stand at {this.Center}, facing {spot.Value.Facing}");
        foreach (int wait in this.ResetArea())
            yield return wait;
        foreach (int wait in this.Mount())
            yield return wait;
        Game1.player.FacingDirection = spot.Value.Facing;
        this.Equip("WateringTank1");
        TractorWaterTank tank = this.Context.Implements.GetTank(this.Tractor);
        tank.Water = 0;

        RefillStartResult result = this.Context.Refill.TryStart(Game1.player);
        this.Check("refill starts when facing water", result == RefillStartResult.Started, $"result={result}");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int midWater = -1;
        while (this.Context.Refill.IsRefilling && timer.Elapsed.TotalSeconds < 15)
        {
            if (midWater < 0 && timer.Elapsed.TotalSeconds >= 2.5)
            {
                midWater = tank.Water;
                this.Screenshot("refill-in-progress");
            }
            yield return 1;
        }
        double seconds = timer.Elapsed.TotalSeconds;
        this.Check("refill fills the tank", tank.Water == tank.Capacity, $"water={tank.Water}/{tank.Capacity}");
        this.Check("refill takes about 5 seconds", seconds is >= 4.5 and <= 6, $"took {seconds:0.0}s");
        this.Check("refill is gradual", midWater > 0 && midWater < tank.Capacity, $"at 2.5s={midWater}");

        // back to the test area
        this.Center = previousCenter;
        foreach (int wait in this.Mount())
            yield return wait;
    }

    private IEnumerable<int> TestMenu()
    {
        Farmer player = Game1.player;
        ImplementManager.SetEquipped(this.Tractor, null);
        Item plow = ItemRegistry.Create(ImplementData.QualifiedIdFor("Plow"));
        if (!player.addItemToInventoryBool(plow))
        {
            this.Fail("implement-menu", "inventory full, can't add a plow");
            yield break;
        }
        int plowsBefore = player.Items.Where(p => p?.QualifiedItemId == plow.QualifiedItemId).Sum(p => p.Stack);
        int index = player.Items.ToList().FindIndex(p => p?.QualifiedItemId == plow.QualifiedItemId); // may stack with one the player already has
        this.Log($"debug: plow index={index}, items={player.Items.Count}, max={player.MaxItems}, inventory=[{this.DescribeInventory()}]");

        ImplementMenu menu = new(this.Tractor, I18n.ImplementMenu_Title(), I18n.ImplementMenu_Empty());
        Game1.activeClickableMenu = menu;
        yield return 30;
        this.MenuScreenshot("menu-empty");
        yield return 2;

        Rectangle slot = menu.GetInventorySlotBounds(index);
        menu.receiveLeftClick(slot.Center.X, slot.Center.Y);
        menu.releaseLeftClick(slot.Center.X, slot.Center.Y);
        yield return 40; // slide-in animation + lock
        this.Check("menu: clicking an implement equips it", ImplementManager.GetEquipped(this.Tractor)?.Id == ImplementData.IdPrefix + "Plow", $"equipped={ImplementManager.GetEquipped(this.Tractor)?.Id}");
        this.Check("menu: equipped implement leaves the inventory", player.Items.Where(p => p?.QualifiedItemId == plow.QualifiedItemId).Sum(p => p.Stack) == plowsBefore - 1, $"plows={player.Items.Where(p => p?.QualifiedItemId == plow.QualifiedItemId).Sum(p => p.Stack)} before={plowsBefore}");
        this.MenuScreenshot("menu-equipped");
        yield return 2;

        menu.receiveLeftClick(menu.SlotBounds.Center.X, menu.SlotBounds.Center.Y);
        yield return 10;
        this.Check("menu: clicking the slot unequips", ImplementManager.GetEquipped(this.Tractor) == null && player.Items.Where(p => p?.QualifiedItemId == plow.QualifiedItemId).Sum(p => p.Stack) == plowsBefore, $"equipped={ImplementManager.GetEquipped(this.Tractor)?.Id}");

        // drag and drop onto the rear hitch
        index = player.Items.ToList().FindIndex(p => p?.QualifiedItemId == plow.QualifiedItemId);
        slot = menu.GetInventorySlotBounds(index);
        Rectangle hitch = menu.GetRearZone();
        menu.receiveLeftClick(slot.Center.X, slot.Center.Y);
        menu.leftClickHeld((slot.Center.X + hitch.Center.X) / 2, (slot.Center.Y + hitch.Center.Y) / 2);
        yield return 5;
        this.MenuScreenshot("menu-dragging");
        yield return 2;
        menu.leftClickHeld(hitch.Center.X, hitch.Center.Y);
        menu.releaseLeftClick(hitch.Center.X, hitch.Center.Y);
        yield return 40;
        this.Check("menu: dragging an implement onto the hitch equips it", ImplementManager.GetEquipped(this.Tractor)?.Id == ImplementData.IdPrefix + "Plow", $"equipped={ImplementManager.GetEquipped(this.Tractor)?.Id}");
        menu.receiveLeftClick(menu.SlotBounds.Center.X, menu.SlotBounds.Center.Y);
        yield return 10;

        // dropping outside the blueprint does nothing
        index = player.Items.ToList().FindIndex(p => p?.QualifiedItemId == plow.QualifiedItemId);
        slot = menu.GetInventorySlotBounds(index);
        menu.receiveLeftClick(slot.Center.X, slot.Center.Y);
        menu.releaseLeftClick(slot.Center.X + 300, slot.Center.Y + 5);
        yield return 10;
        this.Check("menu: dropping outside the tractor does nothing", ImplementManager.GetEquipped(this.Tractor) == null, $"equipped={ImplementManager.GetEquipped(this.Tractor)?.Id}");

        Game1.exitActiveMenu();
        player.removeFirstOfThisItemFromInventory(plow.ItemId);
        yield return 10;
    }

    private IEnumerable<int> TestDismiss()
    {
        Farmer player = Game1.player;
        this.Equip("WoodCutter1");
        string id = ImplementData.QualifiedIdFor("WoodCutter1");

        // make sure there's a free slot (in memory only)
        if (!player.Items.Any(p => p == null) && player.Items.Count >= player.MaxItems)
        {
            int slot = player.Items.ToList().FindLastIndex(p => p is not Tool);
            this.Log($"debug: freeing slot {slot} ({player.Items[slot]?.QualifiedItemId}) for the dismiss test");
            player.Items[slot] = null;
        }
        int before = player.Items.Count(p => p?.QualifiedItemId == id);

        this.Context.DismissTractor(this.Tractor);
        yield return 30;

        this.Check("dismiss: implement removed from tractor", ImplementManager.GetEquipped(this.Tractor) == null, "");
        int after = player.Items.Count(p => p?.QualifiedItemId == id);
        this.Log($"debug: after dismiss inventory=[{this.DescribeInventory()}], cursor={Game1.player.CursorSlotItem?.QualifiedItemId}, debris={string.Join(",", Game1.getFarm().debris.Select(d => d.item?.QualifiedItemId))}");
        this.Check("dismiss: implement back in inventory", after == before + 1, $"count={after} before={before}");
        player.removeFirstOfThisItemFromInventory(id.Substring(3));

        foreach (int wait in this.Mount())
            yield return wait;
    }

    private IEnumerable<int> TestDismount()
    {
        Farmer player = Game1.player;
        int Count(string id) => player.Items.Where(p => p?.QualifiedItemId == id).Sum(p => p!.Stack);
        bool OnGround(string id) => Game1.getFarm().debris.Any(d => d.item?.QualifiedItemId == id);
        void RemoveOne(string id) { for (int i = 0; i < player.Items.Count; i++) if (player.Items[i]?.QualifiedItemId == id) { if (player.Items[i]!.Stack > 1) player.Items[i]!.Stack--; else player.Items[i] = null; break; } }

        // every implement (the water tank included) goes back to the inventory when getting off
        foreach (string implement in new[] { "WateringTank1", "Seeder", "Plow" })
        {
            this.EnsureFreeSlot();
            string id = ImplementData.QualifiedIdFor(implement);
            int before = Count(id);
            this.Equip(implement);
            if (implement == "WateringTank1")
                this.Context.Implements.GetTank(this.Tractor).Water = 57;
            if (implement == "Seeder")
                SeederHopper.Add(this.Tractor, ItemRegistry.Create("(O)472", 3));
            int seedsBefore = Count("(O)472");
            yield return 20;
            this.Tractor.dismount();
            yield return 20;
            this.Check($"dismount returns the {implement}", ImplementManager.GetEquipped(this.Tractor) == null && (Count(id) == before + 1 || OnGround(id)), $"count={Count(id)} before={before} onGround={OnGround(id)}");
            if (implement == "Seeder")
                this.Check("dismount returns the seeder hopper", Count("(O)472") == seedsBefore + 3 || OnGround("(O)472"), $"seeds={Count("(O)472")} before={seedsBefore}");
            if (Count(id) > before)
                RemoveOne(id);
            for (int i = 0; i < player.Items.Count; i++)
                if (player.Items[i]?.QualifiedItemId == "(O)472") player.Items[i] = null;
            foreach (int wait in this.Mount())
                yield return wait;
        }

        // getting off while a menu is open (like a cutscene or passing out) still gives it back
        {
            this.EnsureFreeSlot();
            string id = ImplementData.QualifiedIdFor("Mower");
            int before = Count(id);
            this.Equip("Mower");
            yield return 10;
            Game1.activeClickableMenu = new StardewValley.Menus.DialogueBox("test");
            this.Tractor.dismount();
            yield return 20;
            this.Check("dismount during a menu returns the implement", ImplementManager.GetEquipped(this.Tractor) == null && (Count(id) == before + 1 || OnGround(id)), $"count={Count(id)} before={before}");
            Game1.exitActiveMenu();
            if (Count(id) > before)
                RemoveOne(id);
            foreach (int wait in this.Mount())
                yield return wait;
        }

        // sending the tractor to its garage also gives the implement back (garage built in memory only)
        Farm farm = Game1.getFarm();
        StardewValley.Buildings.Building building = StardewValley.Buildings.Building.CreateInstanceFromId("Pathoschild.TractorMod_Stable", this.Center + new Vector2(-2, -6));
        building.daysOfConstructionLeft.Value = 0;
        if (building is StardewValley.Buildings.Stable garage)
        {
            garage.HorseId = this.Tractor.HorseId;
            farm.buildings.Add(garage);
            this.EnsureFreeSlot();
            string id = ImplementData.QualifiedIdFor("WateringTank1");
            int before = Count(id);
            this.Equip("WateringTank1");
            yield return 10;
            this.Context.DismissTractor(this.Tractor);
            yield return 30;
            this.Check("sending to the garage returns the implement", ImplementManager.GetEquipped(this.Tractor) == null && (Count(id) == before + 1 || OnGround(id)), $"count={Count(id)} before={before}");
            if (Count(id) > before)
                RemoveOne(id);
            farm.buildings.Remove(garage);
        }
        else
            this.Fail("dismount-returns-implement", "couldn't create a garage");

        foreach (int wait in this.Mount())
            yield return wait;
    }

    private IEnumerable<int> TestItemExtensionsBridge()
    {
        bool loaded = Game1.content != null && AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ItemExtensions");
        if (!loaded)
        {
            this.Log("SKIP item-extensions-bridge: Item Extensions isn't installed.");
            yield break;
        }
        var seedApply = HarmonyLib.AccessTools.Method("Pathoschild.Stardew.TractorMod.Framework.Attachments.SeedAttachment:Apply");
        var update = HarmonyLib.AccessTools.Method("Pathoschild.Stardew.TractorMod.Framework.TractorManager:UpdateAttachmentEffects");
        var seedInfo = seedApply != null ? HarmonyLib.Harmony.GetPatchInfo(seedApply) : null;
        var updateInfo = update != null ? HarmonyLib.Harmony.GetPatchInfo(update) : null;
        this.Check("Item Extensions seed integration is applied", seedInfo?.Transpilers.Any() == true, $"owners={string.Join(",", seedInfo?.Owners ?? (IEnumerable<string>)Array.Empty<string>())}");
        this.Check("Item Extensions resource integration is applied", updateInfo?.Postfixes.Any() == true, $"owners={string.Join(",", updateInfo?.Owners ?? (IEnumerable<string>)Array.Empty<string>())}");
        this.Check("bridge applied it with the new mod ID", Framework.ItemExtensionsBridge.Applied, "");
    }

    private IEnumerable<int> TestDismissFullInventory()
    {
        Farmer player = Game1.player;
        Farm farm = Game1.getFarm();

        // use an implement and make sure the player doesn't already have one (it would stack instead of dropping)
        string id = ImplementData.QualifiedIdFor("HarvesterGrain");
        for (int i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i]?.QualifiedItemId == id)
                player.Items[i] = null;
        }

        // then fill every empty slot temporarily (in memory only)
        List<int> filled = [];
        while (player.Items.Count < player.MaxItems)
            player.Items.Add(null);
        for (int i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i] == null)
            {
                player.Items[i] = ItemRegistry.Create("(O)390", 999);
                filled.Add(i);
            }
        }

        this.Equip("HarvesterGrain");
        this.Context.DismissTractor(this.Tractor);
        yield return 30;

        bool onGround = farm.debris.Any(d => d.item?.QualifiedItemId == id);
        this.Log($"debug: inventory=[{this.DescribeInventory()}] debris=[{string.Join(",", farm.debris.Select(d => d.item?.QualifiedItemId ?? d.debrisType.Value.ToString()))}] playerLocation={Game1.player.currentLocation?.Name}");
        this.Check("dismiss with full inventory drops the implement", onGround, $"debrisWithImplement={onGround}");

        // clean up
        foreach (int i in filled)
            player.Items[i] = null;
        farm.debris.RemoveWhere(d => d.item?.QualifiedItemId == id);
        foreach (int wait in this.Mount())
            yield return wait;
    }

    /*********
    ** Phase 4 tests
    *********/
    /// <summary>Plant a fully grown crop on a tile.</summary>
    private HoeDirt PlantMature(Vector2 tile, string seedId)
    {
        Farm farm = Game1.getFarm();
        HoeDirt dirt = new(HoeDirt.watered, farm);
        farm.terrainFeatures[tile] = dirt;
        dirt.crop = new Crop(seedId, (int)tile.X, (int)tile.Y, farm);
        dirt.crop.growCompletely();
        return dirt;
    }

    private IEnumerable<int> TestCropHarvester(bool grain)
    {
        Farm farm = Game1.getFarm();
        HoeDirt parsnip = this.PlantMature(this.Center + new Vector2(-1, 0), "472"); // parsnip: produce
        HoeDirt wheat = this.PlantMature(this.Center + new Vector2(1, 0), "483"); // wheat: grain
        HoeDirt corn = this.PlantMature(this.Center + new Vector2(0, -1), "487"); // corn: grain
        HoeDirt potato = this.PlantMature(this.Center + new Vector2(0, 1), "475"); // potato: produce
        Vector2 treeTile = this.Center + new Vector2(-1, -1);
        FruitTree tree = new("628", FruitTree.treeStage);
        tree.fruit.Add(ItemRegistry.Create("(O)638"));
        farm.terrainFeatures[treeTile] = tree;

        this.Log($"debug: grain? parsnip={ImplementData.IsGrainCrop(parsnip.crop)} wheat={ImplementData.IsGrainCrop(wheat.crop)} corn={ImplementData.IsGrainCrop(corn.crop)} potato={ImplementData.IsGrainCrop(potato.crop)}");
        this.Equip(grain ? "HarvesterGrain" : "HarvesterProduce");
        yield return 240;

        bool parsnipLeft = parsnip.crop != null, potatoLeft = potato.crop != null;
        bool wheatLeft = wheat.crop != null && wheat.crop.fullyGrown.Value && wheat.crop.currentPhase.Value >= wheat.crop.phaseDays.Count - 1;
        bool cornLeft = corn.crop != null && corn.crop.currentPhase.Value >= corn.crop.phaseDays.Count - 1 && corn.crop.dayOfCurrentPhase.Value <= 0 && !corn.crop.fullyGrown.Value || corn.crop?.fullyGrown.Value == true && corn.crop.dayOfCurrentPhase.Value == 0;
        string name = grain ? "grain harvester" : "produce harvester";
        string detail = $"parsnip={parsnipLeft}, potato={potatoLeft}, wheat={wheat.crop != null}, cornCrop={corn.crop != null}/phaseDay={corn.crop?.dayOfCurrentPhase.Value}, treeFruit={tree.fruit.Count}";
        if (grain)
        {
            this.Check($"{name} harvests wheat", wheat.crop == null, detail);
            this.Check($"{name} harvests corn (regrows)", corn.crop == null || corn.crop.dayOfCurrentPhase.Value > 0, detail);
            this.Check($"{name} leaves parsnip and potato", parsnipLeft && potatoLeft, detail);
        }
        else
        {
            this.Check($"{name} harvests parsnip and potato", !parsnipLeft && !potatoLeft, detail);
            this.Check($"{name} leaves wheat and corn", wheat.crop != null && corn.crop != null && corn.crop.dayOfCurrentPhase.Value <= 0, detail);
        }
        this.Check($"{name} leaves fruit trees", tree.fruit.Count == 1, detail);
        this.Screenshot(grain ? "harvester-grain" : "harvester-produce");
        yield return 2;
    }

    private IEnumerable<int> TestBushHarvester()
    {
        Farm farm = Game1.getFarm();
        Vector2 bushTile = this.Center + new Vector2(1, 0);
        Bush tea = new(bushTile, Bush.greenTeaBush, farm);
        tea.tileSheetOffset.Value = 1;
        tea.datePlanted.Value = Game1.Date.TotalDays - 120; // tea bushes only produce after 20 days
        farm.terrainFeatures[bushTile] = tea;
        HoeDirt parsnip = this.PlantMature(this.Center + new Vector2(-1, 0), "472");

        int realDay = Game1.dayOfMonth;
        Game1.dayOfMonth = 23; // tea bushes only produce leaves from day 22 (in memory only, never saved)
        tea.tileSheetOffset.Value = 1;
        tea.datePlanted.Value = Game1.Date.TotalDays - 120; // tea bushes only produce after 20 days
        this.Log($"debug: tea bush tile={tea.Tile} inBloom={tea.inBloom()} age={tea.getAge()} size={tea.size.Value} offset={tea.tileSheetOffset.Value} day={Game1.dayOfMonth}");
        this.Equip("HarvesterBush");
        yield return 180;
        int offsetAfterTractor = tea.tileSheetOffset.Value;

        // control: harvest by hand the same way the game does, to tell a test setup problem from a mod problem
        tea.tileSheetOffset.Value = 1;
        tea.performUseAction(tea.Tile);
        yield return 30;
        this.Log($"debug: tea bush offset after tractor={offsetAfterTractor}, after manual use={tea.tileSheetOffset.Value}");
        Game1.dayOfMonth = realDay;

        this.Check("bush harvester harvests tea bushes", offsetAfterTractor == 0, $"teaOffset={offsetAfterTractor}");
        this.Check("bush harvester leaves crops", parsnip.crop != null, $"parsnip={parsnip.crop != null}");
    }

    private IEnumerable<int> TestFruitTreeHarvester()
    {
        Farm farm = Game1.getFarm();
        Vector2 treeTile = this.Center + new Vector2(1, 0);
        FruitTree tree = new("628", FruitTree.treeStage);
        tree.fruit.Add(ItemRegistry.Create("(O)638"));
        tree.fruit.Add(ItemRegistry.Create("(O)638"));
        farm.terrainFeatures[treeTile] = tree;
        HoeDirt parsnip = this.PlantMature(this.Center + new Vector2(-1, 0), "472");

        this.Equip("HarvesterFruitTree");
        yield return 180;

        this.Check("fruit tree harvester harvests fruit", tree.fruit.Count == 0, $"fruit={tree.fruit.Count}");
        this.Check("fruit tree harvester leaves crops", parsnip.crop != null, $"parsnip={parsnip.crop != null}");
    }

    private IEnumerable<int> TestMower()
    {
        Farm farm = Game1.getFarm();
        Vector2 weedTile = this.Center + new Vector2(1, 0);
        farm.objects[weedTile] = ItemRegistry.Create<SObject>("(O)674");
        Vector2 grassTile = this.Center + new Vector2(0, -1);
        Grass grass = new(Grass.springGrass, 4);
        farm.terrainFeatures[grassTile] = grass;
        HoeDirt parsnip = this.PlantMature(this.Center + new Vector2(-1, 0), "472");

        this.Equip("Mower");
        yield return 180;

        bool grassLeft = farm.terrainFeatures.TryGetValue(grassTile, out TerrainFeature? f) && f is Grass g && g.numberOfWeeds.Value >= 4;
        this.Check("mower clears weeds", !farm.objects.ContainsKey(weedTile), $"weedsLeft={farm.objects.ContainsKey(weedTile)}");
        this.Check("mower cuts grass", !grassLeft, $"grass={(farm.terrainFeatures.TryGetValue(grassTile, out f) && f is Grass g2 ? g2.numberOfWeeds.Value.ToString() : "gone")}");
        this.Check("mower leaves crops", parsnip.crop != null, $"parsnip={parsnip.crop != null}");
    }

    private IEnumerable<int> TestSeeder()
    {
        Farm farm = Game1.getFarm();
        foreach (Vector2 tile in this.Ring())
            farm.terrainFeatures[tile] = new HoeDirt(HoeDirt.dry, farm);

        this.Equip("Seeder");
        SeederHopper.Add(this.Tractor, ItemRegistry.Create("(O)472", 5)); // parsnip seeds (spring)
        SeederHopper.Add(this.Tractor, ItemRegistry.Create("(O)368", 3)); // basic fertilizer
        yield return 420;

        int planted = this.Ring().Count(tile => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? t) && t is HoeDirt { crop: not null });
        int fertilized = this.Ring().Count(tile => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? t) && t is HoeDirt d && !string.IsNullOrEmpty(d.fertilizer.Value));
        int left = SeederHopper.Get(this.Tractor).Where(p => p != null).Sum(p => p!.Stack);
        this.Check("seeder plants seeds from the hopper", planted == 5, $"planted={planted} (expected 5)");
        this.Check("seeder applies fertilizer from the hopper", fertilized == 3, $"fertilized={fertilized} (expected 3)");
        this.Check("seeder uses up the hopper", left == 0, $"left={left}");
        this.Screenshot("seeder");
        yield return 2;
    }

    private IEnumerable<int> TestSeederMenu()
    {
        Farmer player = Game1.player;
        this.Equip("Seeder");
        Item seeds = ItemRegistry.Create("(O)472", 7);
        this.EnsureFreeSlot();
        if (!player.addItemToInventoryBool(seeds))
        {
            this.Fail("seeder-menu", "inventory full");
            yield break;
        }
        int index = player.Items.ToList().FindIndex(p => p?.QualifiedItemId == "(O)472");
        int seedsBefore = player.Items.Where(p => p?.QualifiedItemId == "(O)472").Sum(p => p!.Stack);

        ImplementMenu menu = new(this.Tractor, I18n.ImplementMenu_Title(), I18n.ImplementMenu_Empty());
        Game1.activeClickableMenu = menu;
        yield return 20;

        Rectangle slot = menu.GetInventorySlotBounds(index);
        menu.receiveLeftClick(slot.Center.X, slot.Center.Y);
        yield return 10;
        this.MenuScreenshot("menu-seeder");
        yield return 2;
        int inHopper = SeederHopper.Get(this.Tractor).Where(p => p?.QualifiedItemId == "(O)472").Sum(p => p!.Stack);
        this.Check("seeder menu: clicking seeds loads the hopper", inHopper == seedsBefore && !player.Items.Any(p => p?.QualifiedItemId == "(O)472"), $"inHopper={inHopper} before={seedsBefore}");

        Item implement = ItemRegistry.Create(ImplementData.QualifiedIdFor("Plow"));
        int implementIndex = player.Items.ToList().FindIndex(p => p?.QualifiedItemId == implement.QualifiedItemId);
        Rectangle hopperSlot = menu.GetHopperSlotBounds(0);
        menu.receiveLeftClick(hopperSlot.Center.X, hopperSlot.Center.Y);
        yield return 10;
        int back = player.Items.Where(p => p?.QualifiedItemId == "(O)472").Sum(p => p!.Stack);
        this.Check("seeder menu: clicking a hopper slot returns the seeds", back == seedsBefore, $"back={back}");

        Game1.exitActiveMenu();
        for (int i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i]?.QualifiedItemId == "(O)472")
                player.Items[i] = null;
        }
        yield return 10;
    }

    private IEnumerable<int> TestDismissReturnsHopper()
    {
        Farmer player = Game1.player;
        this.Equip("Seeder");
        SeederHopper.Add(this.Tractor, ItemRegistry.Create("(O)472", 4));
        int seedsBefore = player.Items.Where(p => p?.QualifiedItemId == "(O)472").Sum(p => p!.Stack);

        this.Context.DismissTractor(this.Tractor);
        yield return 30;

        int seedsAfter = player.Items.Where(p => p?.QualifiedItemId == "(O)472").Sum(p => p!.Stack);
        bool onGround = Game1.getFarm().debris.Any(d => d.item?.QualifiedItemId == "(O)472");
        this.Check("dismiss returns the hopper contents", seedsAfter == seedsBefore + 4 || onGround, $"seeds={seedsAfter} before={seedsBefore} onGround={onGround}");
        this.Check("dismiss empties the hopper", SeederHopper.Get(this.Tractor).All(p => p == null), "");

        for (int i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i]?.QualifiedItemId is "(O)472" or "(O)TractorModFull_Seeder")
                player.Items[i] = null;
        }
        foreach (int wait in this.Mount())
            yield return wait;
    }

    /// <summary>Remove everything placed in the test area.</summary>
    private IEnumerable<int> ResetArea()
    {
        Farm farm = Game1.getFarm();
        Rectangle area = new((int)this.Center.X - 3, (int)this.Center.Y - 3, 7, 7);
        foreach (Vector2 tile in this.Tiles(area))
        {
            farm.objects.Remove(tile);
            farm.terrainFeatures.Remove(tile);
        }
        farm.resourceClumps.RemoveWhere(c => area.Intersects(new Rectangle((int)c.Tile.X, (int)c.Tile.Y, c.width.Value, c.height.Value)));
        farm.debris.Clear();
        if (this.Tractor != null)
        {
            SeederHopper.TakeAll(this.Tractor);
            ImplementManager.SetEquipped(this.Tractor, null);
        }
        yield return 10;
    }

    private void Equip(string idSuffix)
    {
        ImplementManager.SetEquipped(this.Tractor, ItemRegistry.Create(ImplementData.QualifiedIdFor(idSuffix)));

        // hold nothing (an empty toolbar slot), so held-tool attachments don't interfere
        Farmer player = Game1.player;
        int empty = Enumerable.Range(0, Math.Min(12, player.Items.Count)).FirstOrDefault(i => player.Items[i] == null, -1);
        if (empty >= 0)
            player.CurrentToolIndex = empty;
    }

    private IEnumerable<Vector2> Ring()
    {
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                if (x != 0 || y != 0)
                    yield return this.Center + new Vector2(x, y);
            }
        }
    }

    private IEnumerable<Vector2> Tiles(Rectangle area)
    {
        for (int x = area.Left; x < area.Right; x++)
        {
            for (int y = area.Top; y < area.Bottom; y++)
                yield return new Vector2(x, y);
        }
    }

    private Vector2? FindEmptyArea(Farm farm, int radius, Func<Vector2, bool>? accept = null)
    {
        int width = farm.map.Layers[0].LayerWidth;
        int height = farm.map.Layers[0].LayerHeight;
        for (int y = radius + 2; y < height - radius - 2; y++)
        {
            for (int x = radius + 2; x < width - radius - 2; x++)
            {
                bool ok = true;
                for (int dx = -radius; dx <= radius && ok; dx++)
                {
                    for (int dy = -radius; dy <= radius && ok; dy++)
                        ok = this.IsEmptyDiggable(farm, new Vector2(x + dx, y + dy));
                }
                if (ok && (accept == null || accept(new Vector2(x, y))))
                    return new Vector2(x, y);
            }
        }
        return null;
    }

    private bool IsEmptyDiggable(Farm farm, Vector2 tile)
    {
        int x = (int)tile.X, y = (int)tile.Y;
        // objects, terrain features and resource clumps are ignored: the area is cleared in memory before testing
        return
            farm.doesTileHaveProperty(x, y, "Diggable", "Back") != null
            && farm.getTileIndexAt(x, y, "Buildings") < 0
            && farm.getBuildingAt(tile) == null
            && !farm.isWaterTile(x, y);
    }

    private (Vector2 Stand, int Facing)? FindWaterSpot(Farm farm)
    {
        int width = farm.map.Layers[0].LayerWidth;
        int height = farm.map.Layers[0].LayerHeight;
        (Vector2 Offset, int Facing)[] directions = [(new Vector2(0, -1), Game1.up), (new Vector2(0, 1), Game1.down), (new Vector2(-1, 0), Game1.left), (new Vector2(1, 0), Game1.right)];
        for (int y = 3; y < height - 3; y++)
        {
            for (int x = 3; x < width - 3; x++)
            {
                Vector2 tile = new(x, y);
                if (farm.getTileIndexAt(x, y, "Buildings") >= 0 || farm.getBuildingAt(tile) != null || farm.isWaterTile(x, y) || farm.doesTileHaveProperty(x, y, "Diggable", "Back") == null)
                    continue;

                foreach ((Vector2 offset, int facing) in directions)
                {
                    Vector2 water = tile + offset;
                    if (farm.CanRefillWateringCanOnTile((int)water.X, (int)water.Y))
                        return (tile, facing);
                }
            }
        }
        return null;
    }

    /// <summary>Make sure the player has a free inventory slot, removing a non-tool item if needed (in memory only).</summary>
    private void EnsureFreeSlot()
    {
        Farmer player = Game1.player;
        if (player.Items.Any(p => p == null) || player.Items.Count < player.MaxItems)
            return;

        int slot = player.Items.ToList().FindLastIndex(p => p is not Tool);
        this.Log($"debug: freeing slot {slot} ({player.Items[slot]?.QualifiedItemId})");
        player.Items[slot] = null;
    }

    private string DescribeInventory()
    {
        return string.Join(", ", Game1.player.Items.Select((item, i) => item == null ? null : $"{i}:{item.QualifiedItemId}").Where(p => p != null));
    }

    /// <summary>Request a picture of the open menu (the normal screenshot doesn't include the UI layer).</summary>
    private void MenuScreenshot(string name)
    {
        this.PendingMenuCapture = name;
    }

    /// <summary>Draw the open menu into its own render target and save it.</summary>
    private void CaptureMenu()
    {
        string name = this.PendingMenuCapture!;
        this.PendingMenuCapture = null;
        if (Game1.activeClickableMenu is not { } menu)
            return;
        try
        {
            GraphicsDevice device = Game1.graphics.GraphicsDevice;
            int width = Game1.uiViewport.Width, height = Game1.uiViewport.Height;
            using RenderTarget2D target = new(device, width, height);
            RenderTargetBinding[] previous = device.GetRenderTargets();
            device.SetRenderTarget(target);
            device.Clear(new Color(60, 90, 60));
            SpriteBatch b = new(device);
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            menu.draw(b);
            b.End();
            device.SetRenderTargets(previous);
            using FileStream stream = File.Create(Path.Combine(this.OutDir, name + ".png"));
            target.SaveAsPng(stream, width, height);
            this.Log($"Menu picture saved: {name}.png");
        }
        catch (Exception ex)
        {
            this.Log($"Menu picture {name} failed: {ex.Message}");
        }
    }

    private void Screenshot(string name)
    {
        this.PendingScreenshot = name;
    }

    private void Check(string name, bool passed, string detail)
    {
        if (passed)
        {
            this.Passed++;
            this.Log($"PASS {name}{(detail.Length > 0 ? $" ({detail})" : "")}");
        }
        else
            this.Fail(name, detail);
    }

    private void Fail(string name, string detail)
    {
        this.Failed++;
        this.Log($"FAIL {name}: {detail}");
    }

    private void Log(string message)
    {
        this.Lines.Add(message);
        this.Monitor.Log($"[TEST] {message}", message.StartsWith("FAIL") ? LogLevel.Error : LogLevel.Info);
        File.WriteAllLines(Path.Combine(this.OutDir, "results.txt"), this.Lines);
    }

    private void Finish()
    {
        this.Finished = true;
        this.Log($"SUMMARY passed={this.Passed} failed={this.Failed}");
        if (this.OriginalPauseWhenOutOfFocus.HasValue)
            Game1.options.pauseWhenOutOfFocus = this.OriginalPauseWhenOutOfFocus.Value;
        this.Log("DONE (closing the game without saving)");
        Game1.game1.Exit();
    }
}
