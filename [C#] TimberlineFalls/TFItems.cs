using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.GameData.Shops;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TimberlineFalls;

public static class TFItems
{
    public const string ShopId = "TimberlineFalls.BuycoShop";

    public enum BombType { Flashbomb, Rainmaker, MeadowMine, GrassBomb, DrillBomb, DwarfStar }

    private class ActiveBomb
    {
        public int SpriteID;
        public GameLocation Location = default!;
        public Vector2 TilePosition;
        public Farmer Owner = default!;
        public int Timer;
        public BombType Type;
    }

    private class ActiveDrillHole
    {
        public GameLocation Location = default!;
        public Vector2 TilePosition;
        public int LevelsToDrop;
    }

    private static readonly List<ActiveBomb> ActiveBombs = new();
    private static readonly List<ActiveDrillHole> ActiveDrillHoles = new();
    private const string TexturePath = "Maps/Huskyn1nja.TimberFalls_Timberline_Items";
    private static ActiveDrillHole? PendingHole = null;

    private static readonly Dictionary<string, (BombType Type, int SpriteIndex)> BombDataMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Flashbomb", (BombType.Flashbomb, 44) },
        { "Rainmaker", (BombType.Rainmaker, 47) },
        { "MeadowMine", (BombType.MeadowMine, 45) },
        { "GrassBomb", (BombType.GrassBomb, 46) },
        { "DrillBomb", (BombType.DrillBomb, 48) },
        { "DwarfStar", (BombType.DwarfStar, 49) }
    };

    public static void Initialize(IModHelper helper, Harmony harmony)
    {
        helper.Events.Display.MenuChanged += OnMenuChanged;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Input.ButtonPressed += OnButtonPressed;

        harmony.Patch(
            original: AccessTools.Method(typeof(Farmer), nameof(Farmer.addItemToInventoryBool), new[] { typeof(Item), typeof(bool) }),
            prefix: new HarmonyMethod(typeof(TFItems), nameof(Prefix_AddItem))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(StardewValley.Object), nameof(StardewValley.Object.placementAction)),
            prefix: new HarmonyMethod(typeof(TFItems), nameof(Prefix_PlacementAction))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(StardewValley.Object), nameof(StardewValley.Object.isPlaceable)),
            postfix: new HarmonyMethod(typeof(TFItems), nameof(Postfix_IsPlaceable))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.answerDialogueAction)),
            prefix: new HarmonyMethod(typeof(TFItems), nameof(Prefix_AnswerDialogueAction))
        );
    }

    private static void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (e.NewMenu is ShopMenu shop && shop.ShopId == ShopId)
        {
            var shopDataDict = Game1.content.Load<Dictionary<string, ShopData>>("Data/Shops");
            if (shopDataDict != null && shopDataDict.TryGetValue(ShopId, out var shopData))
            {
                foreach (var item in shop.forSale)
                {
                    if (shop.itemPriceAndStock.TryGetValue(item, out var stockInfo))
                    {
                        int minStack = GetMinStackForItem(item, shopData);
                        if (minStack > 1)
                        {
                            item.Stack = minStack;
                            stockInfo.Price *= minStack;
                        }
                    }
                }
            }
        }
    }

    public static void Prefix_AddItem(Item? item)
    {
        if (Game1.activeClickableMenu is ShopMenu shop && shop.ShopId == ShopId && item != null)
        {
            var shopDataDict = Game1.content.Load<Dictionary<string, ShopData>>("Data/Shops");
            if (shopDataDict != null && shopDataDict.TryGetValue(ShopId, out var shopData))
            {
                int minStack = GetMinStackForItem(item, shopData);
                if (minStack > 1)
                {
                    item.Stack *= minStack;
                }
            }
        }
    }

    private static int GetMinStackForItem(ISalable item, ShopData? shopData)
    {
        if (shopData == null || item is not Item stardewItem) return 1;

        foreach (var shopItem in shopData.Items.Where(si => si.ItemId == stardewItem.QualifiedItemId))
        {
            if (shopItem.CustomFields != null && shopItem.CustomFields.TryGetValue("Buyco.MinStack", out string? minStackStr) && int.TryParse(minStackStr, out int minStack))
            {
                return minStack;
            }
        }
        return 1;
    }

    private static bool TryGetBombData(string qualifiedItemId, out BombType type, out int spriteIndex)
    {
        type = default;
        spriteIndex = default;
        if (string.IsNullOrEmpty(qualifiedItemId)) return false;

        foreach (var kvp in BombDataMap)
        {
            if (qualifiedItemId.EndsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                type = kvp.Value.Type;
                spriteIndex = kvp.Value.SpriteIndex;
                return true;
            }
        }
        return false;
    }

    public static void Postfix_IsPlaceable(StardewValley.Object __instance, ref bool __result)
    {
        if (TryGetBombData(__instance.QualifiedItemId, out _, out _))
        {
            __result = true;
        }
    }

    public static bool Prefix_PlacementAction(StardewValley.Object __instance, GameLocation location, int x, int y, Farmer who, ref bool __result)
    {
        if (TryGetBombData(__instance.QualifiedItemId, out BombType bombType, out int spriteIndex))
        {
            Vector2 tile = new(x / 64, y / 64);
            Texture2D bombTexture = ModEntry.ModHelper.GameContent.Load<Texture2D>(TexturePath);
            Rectangle sourceRect = Game1.getSourceRectForStandardTileSheet(bombTexture, spriteIndex, 16, 16);

            location.playSound("thudStep");
            int uniqueID = Game1.random.Next(1000000, 9999999);

            var bombSprite = new TemporaryAnimatedSprite(
                textureName: TexturePath,
                sourceRect: sourceRect,
                animationInterval: 100f,
                animationLength: 1,
                numberOfLoops: 99,
                position: tile * 64f,
                flicker: false,
                flipped: false
            )
            {
                layerDepth = (tile.Y * 64f + 32f) / 10000f,
                scale = 4f,
                shakeIntensity = 0.5f,
                owner = who
            };

            ModEntry.ModHelper.Reflection.GetField<int>(bombSprite, "id").SetValue(uniqueID);
            location.temporarySprites.Add(bombSprite);

            ActiveBombs.Add(new ActiveBomb
            {
                SpriteID = uniqueID,
                Location = location,
                TilePosition = tile,
                Owner = who,
                Timer = 75,
                Type = bombType
            });

            __result = true;
            return false;
        }
        return true;
    }

    private static void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsPlayerFree || Game1.currentLocation == null) return;

        if (e.Button.IsActionButton())
        {
            Vector2 tile = e.Cursor.GrabTile;
            var hole = ActiveDrillHoles.FirstOrDefault(h => h.Location == Game1.currentLocation && h.TilePosition == tile);

            if (hole != null)
            {
                ModEntry.ModHelper.Input.Suppress(e.Button);
                PendingHole = hole;
                Game1.currentLocation.createQuestionDialogue(
                    string.Empty,
                    Game1.currentLocation.createYesNoResponses(),
                    "Timberline_DrillHole"
                );
            }
        }
    }

    public static bool Prefix_AnswerDialogueAction(GameLocation __instance, string questionAndAnswer, string[] questionParams, ref bool __result)
    {
        if (questionAndAnswer == "Timberline_DrillHole_Yes")
        {
            if (PendingHole != null && __instance is StardewValley.Locations.MineShaft mine && Game1.player != null)
            {
                int levelsDropped = PendingHole.LevelsToDrop;
                int targetLevel = mine.mineLevel + levelsDropped;

                if (mine.mineLevel < 120 && targetLevel > 120)
                {
                    targetLevel = 120;
                    levelsDropped = targetLevel - mine.mineLevel;
                }

                int damage = levelsDropped * 3;
                Game1.player.health = Math.Max(1, Game1.player.health - damage);

                Game1.globalFadeToBlack(() =>
                {
                    Game1.enterMine(targetLevel);
                    Game1.player.faceDirection(2);
                    Game1.player.showFrame(5);
                }, 0.045f);

                Game1.playSound("fallDown");

                ActiveDrillHoles.Remove(PendingHole);
                PendingHole = null;
            }
            __result = true;
            return false;
        }

        if (questionAndAnswer == "Timberline_DrillHole_No")
        {
            PendingHole = null;
            __result = true;
            return false;
        }

        if (questionAndAnswer == "Timberline_DionMakeover_Yes")
        {
            if (Game1.player.Money >= 500)
            {
                Game1.player.Money -= 500;
                Game1.activeClickableMenu = new CharacterCustomization(CharacterCustomization.Source.Wizard);
            }
            else
            {
                Game1.drawObjectDialogue(Game1.content.LoadString("Strings\\UI:NotEnoughMoney1"));
            }
            __result = true;
            return false;
        }

        return true;
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        ActiveDrillHoles.RemoveAll(h => h.Location != Game1.currentLocation);

        if (ActiveBombs.Count == 0) return;

        for (int i = ActiveBombs.Count - 1; i >= 0; i--)
        {
            var bomb = ActiveBombs[i];
            bomb.Timer--;

            var sprite = bomb.Location.temporarySprites.FirstOrDefault(s => ModEntry.ModHelper.Reflection.GetField<int>(s, "id").GetValue() == bomb.SpriteID);

            if (sprite != null)
            {
                bool isNearDetonation = bomb.Timer <= 25;
                bool isFlickerFrame = bomb.Timer % 8 < 4;
                bool isSlowFlickerFrame = bomb.Timer % 20 < 10;

                sprite.color = bomb.Type switch
                {
                    BombType.Rainmaker => isNearDetonation ? (isFlickerFrame ? Color.Cyan : Color.White) : (isSlowFlickerFrame ? Color.Blue : Color.White),
                    BombType.MeadowMine => isNearDetonation ? (isFlickerFrame ? Color.Green : Color.White) : (isSlowFlickerFrame ? Color.LimeGreen : Color.White),
                    BombType.GrassBomb => isNearDetonation ? (isFlickerFrame ? Color.DarkGreen : Color.White) : (isSlowFlickerFrame ? Color.YellowGreen : Color.White),
                    BombType.DrillBomb => isNearDetonation ? (isFlickerFrame ? Color.Yellow : Color.White) : (isSlowFlickerFrame ? Color.Goldenrod : Color.White),
                    BombType.DwarfStar => isNearDetonation ? (isFlickerFrame ? Color.DarkRed : Color.White) : (isSlowFlickerFrame ? Color.OrangeRed : Color.White),
                    _ => isNearDetonation ? (isFlickerFrame ? Color.Red : Color.White) : (isSlowFlickerFrame ? Color.Orange : Color.White)
                };

                if (isNearDetonation)
                {
                    sprite.shakeIntensity = 2f;
                }
            }

            if (bomb.Timer <= 0)
            {
                switch (bomb.Type)
                {
                    case BombType.Rainmaker: ApplyWaterExplosion(bomb.Location, bomb.TilePosition); break;
                    case BombType.MeadowMine: ApplyMeadowMineExplosion(bomb.Location, bomb.TilePosition, bomb.Owner); break;
                    case BombType.GrassBomb: ApplyGrassExplosion(bomb.Location, bomb.TilePosition); break;
                    case BombType.DrillBomb: ApplyDrillBombExplosion(bomb.Location, bomb.TilePosition, bomb.Owner); break;
                    case BombType.DwarfStar: ApplyDwarfStarExplosion(bomb.Location, bomb.TilePosition, bomb.Owner); break;
                    default: ApplyFlashExplosion(bomb.Location, bomb.TilePosition * 64f, bomb.Owner); break;
                }

                if (sprite != null) bomb.Location.temporarySprites.Remove(sprite);
                ActiveBombs.RemoveAt(i);
            }
        }
    }

    private static void ApplyFlashExplosion(GameLocation loc, Vector2 position, Farmer owner)
    {
        int radiusPixels = 5 * 64;
        loc.playSound("serpentDie");
        loc.playSound("breakingGlass");

        var explosionSprite = new TemporaryAnimatedSprite(362, 100f, 1, 1, position, false, false)
        {
            scale = 5f,
            color = Color.White,
            alpha = 0.8f,
            delayBeforeAnimationStart = 0
        };

        ModEntry.ModHelper.Reflection.GetField<int>(explosionSprite, "id").SetValue(88991);
        loc.temporarySprites.Add(explosionSprite);

        foreach (var character in loc.characters)
        {
            if (character is Monster monster && Vector2.Distance(monster.Position, position) <= radiusPixels)
            {
                loc.damageMonster(monster.GetBoundingBox(), 0, 0, true, 0f, 0, 0f, 0f, false, owner);
                monster.stunTime.Value = 4000;
                monster.setTrajectory(0, 0);
                monster.shake(100);
            }
        }
    }

    private static void ApplyWaterExplosion(GameLocation loc, Vector2 centerTile)
    {
        loc.playSound("waterSlosh");
        loc.playSound("wateringCan");
        int radius = 2;

        for (int x = (int)centerTile.X - radius; x <= (int)centerTile.X + radius; x++)
        {
            for (int y = (int)centerTile.Y - radius; y <= (int)centerTile.Y + radius; y++)
            {
                Vector2 targetTile = new(x, y);

                if (loc.terrainFeatures.TryGetValue(targetTile, out var feature) && feature is HoeDirt dirt)
                {
                    dirt.state.Value = 1;
                }

                if (loc.objects.TryGetValue(targetTile, out var obj) && obj is IndoorPot pot && pot.hoeDirt.Value is not null)
                {
                    pot.hoeDirt.Value.state.Value = 1;
                    pot.showNextIndex.Value = true;
                }

                loc.temporarySprites.Add(new TemporaryAnimatedSprite(13, targetTile * 64f, Color.White, 10, Game1.random.NextDouble() < 0.5, 70f, 0, -1, -1f, -1, 0)
                {
                    delayBeforeAnimationStart = Game1.random.Next(0, 100)
                });
            }
        }
    }

    private static void ApplyMeadowMineExplosion(GameLocation loc, Vector2 centerTile, Farmer owner)
    {
        loc.playSound("dirtyHit");
        loc.playSound("throw");
        int radius = 2;

        string seedId = Game1.currentSeason switch
        {
            "spring" => "495",
            "summer" => "496",
            "fall" => "497",
            "winter" => "498",
            _ => "495"
        };

        string[] springForage = { "16", "18", "20", "22" };
        string[] summerForage = { "396", "398", "402" };
        string[] fallForage = { "404", "406", "408", "410" };
        string[] winterForage = { "412", "414", "416", "418" };

        for (int x = (int)centerTile.X - radius; x <= (int)centerTile.X + radius; x++)
        {
            for (int y = (int)centerTile.Y - radius; y <= (int)centerTile.Y + radius; y++)
            {
                Vector2 targetTile = new(x, y);

                string harvestId = Game1.currentSeason switch
                {
                    "summer" => summerForage[Game1.random.Next(summerForage.Length)],
                    "fall" => fallForage[Game1.random.Next(fallForage.Length)],
                    "winter" => winterForage[Game1.random.Next(winterForage.Length)],
                    _ => springForage[Game1.random.Next(springForage.Length)]
                };

                if (loc.terrainFeatures.TryGetValue(targetTile, out var feature) && feature is HoeDirt dirt && dirt.crop == null)
                {
                    if (dirt.plant(seedId, owner, false) && dirt.crop != null)
                    {
                        dirt.crop.indexOfHarvest.Value = harvestId;
                    }
                }

                if (loc.objects.TryGetValue(targetTile, out var obj) && obj is IndoorPot pot && pot.hoeDirt.Value is not null && pot.hoeDirt.Value.crop == null)
                {
                    if (pot.hoeDirt.Value.plant(seedId, owner, false) && pot.hoeDirt.Value.crop != null)
                    {
                        pot.hoeDirt.Value.crop.indexOfHarvest.Value = harvestId;
                    }
                }

                loc.temporarySprites.Add(new TemporaryAnimatedSprite(
                    textureName: "LooseSprites\\Cursors",
                    sourceRect: new Rectangle(170, 349, 16, 16),
                    animationInterval: 100f,
                    animationLength: 4,
                    numberOfLoops: 1,
                    position: targetTile * 64f,
                    flicker: false,
                    flipped: false
                )
                {
                    scale = 4f,
                    layerDepth = (targetTile.Y * 64f + 32f) / 10000f
                });
            }
        }
    }

    private static void ApplyGrassExplosion(GameLocation loc, Vector2 centerTile)
    {
        loc.playSound("leafrustle");
        loc.playSound("dirtyHit");
        int radius = 3;

        for (int x = (int)centerTile.X - radius; x <= (int)centerTile.X + radius; x++)
        {
            for (int y = (int)centerTile.Y - radius; y <= (int)centerTile.Y + radius; y++)
            {
                Vector2 targetTile = new(x, y);

                if (Vector2.Distance(centerTile, targetTile) <= radius && !loc.terrainFeatures.ContainsKey(targetTile) && !loc.objects.ContainsKey(targetTile))
                {
                    if (loc.IsOutdoors || loc.doesTileHaveProperty((int)targetTile.X, (int)targetTile.Y, "Diggable", "Back") != null)
                    {
                        loc.terrainFeatures.TryAdd(targetTile, new Grass(1, Game1.random.Next(2, 5)));
                    }
                }
            }
        }
    }

    private static void ApplyDrillBombExplosion(GameLocation loc, Vector2 centerTile, Farmer owner)
    {
        loc.playSound("explosion");

        loc.temporarySprites.Add(new TemporaryAnimatedSprite(362, 100f, 1, 1, centerTile * 64f, false, false)
        {
            scale = 4f,
            color = Color.Yellow,
            alpha = 0.8f
        });

        if (loc is StardewValley.Locations.MineShaft mine)
        {
            for (int x = (int)centerTile.X - 1; x <= (int)centerTile.X + 1; x++)
            {
                for (int y = (int)centerTile.Y - 1; y <= (int)centerTile.Y + 1; y++)
                {
                    Vector2 targetTile = new(x, y);
                    if (mine.objects.TryGetValue(targetTile, out var obj) && obj.IsBreakableStone())
                    {
                        mine.destroyObject(targetTile, owner);
                    }
                }
            }

            if (mine.isTileClearForMineObjects(centerTile) && mine.mineLevel != 120 && mine.mineLevel != 77377)
            {
                mine.setTileProperty((int)centerTile.X, (int)centerTile.Y, "Back", "Passable", "F");

                mine.temporarySprites.Add(new TemporaryAnimatedSprite(
                    textureName: "Maps\\Mines\\mine_desert",
                    sourceRect: new Rectangle(224, 160, 16, 16),
                    animationInterval: 99999f,
                    animationLength: 1,
                    numberOfLoops: 99999,
                    position: centerTile * 64f,
                    flicker: false,
                    flipped: false,
                    layerDepth: (centerTile.Y * 64f) / 10000f,
                    alphaFade: 0f,
                    color: Color.White,
                    scale: 4f,
                    scaleChange: 0f,
                    rotation: 0f,
                    rotationChange: 0f
                )
                {
                    holdLastFrame = true
                });

                ActiveDrillHoles.Add(new ActiveDrillHole
                {
                    Location = mine,
                    TilePosition = centerTile,
                    LevelsToDrop = Game1.random.Next(1, 5)
                });
            }
        }
    }

    private static void ApplyDwarfStarExplosion(GameLocation loc, Vector2 centerTile, Farmer owner)
    {
        int blastRadius = 12;
        loc.explode(centerTile, blastRadius, owner, true);

        int damageRadiusPixels = blastRadius * 64;

        foreach (var character in loc.characters)
        {
            if (character is Monster monster && Vector2.Distance(monster.Position, centerTile * 64f) <= damageRadiusPixels)
            {
                loc.damageMonster(monster.GetBoundingBox(), 400, 600, true, 2f, 0, 0f, 0f, false, owner);
            }
        }

        if (Vector2.Distance(owner.Tile, centerTile) <= blastRadius)
        {
            owner.takeDamage(50, true, null);
        }
    }
}
