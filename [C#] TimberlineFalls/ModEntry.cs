using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Delegates;
using StardewValley.GameData.SpecialOrders;
using StardewValley.Menus;
using StardewValley.SpecialOrders;
using StardewValley.Triggers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TimberlineFalls;

public static class TFWorld
{
    public static bool IsProcessingAction = false;
    public static bool HasViewedBoardToday = false;

    public static void Initialize(IModHelper helper, string uniqueId)
    {
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.Player.Warped += OnPlayerWarped;

        TriggerActionManager.RegisterAction($"{uniqueId}_RecycleColas", RecycleColas);
    }

    public static void GenerateTimberlineOrders()
    {
        var team = Game1.player.team;

        team.acceptedSpecialOrderTypes.Remove("Timberline");
        for (int i = 0; i < team.availableSpecialOrders.Count; i++)
        {
            if (team.availableSpecialOrders[i].orderType.Value == "Timberline")
            {
                team.availableSpecialOrders.RemoveAt(i);
                i--;
            }
        }

        var allOrders = Game1.content.Load<Dictionary<string, SpecialOrderData>>("Data\\SpecialOrders");
        List<string> validKeys = new();

        foreach (var kvp in allOrders.Where(kvp => kvp.Value.OrderType == "Timberline"))
        {
            bool invalid = (!kvp.Value.Repeatable && team.completedSpecialOrders.Contains(kvp.Key)) ||
                           !SpecialOrder.CheckTags(kvp.Value.RequiredTags) ||
                           team.specialOrders.Any(activeOrder => activeOrder.questKey.Value == kvp.Key);

            if (!invalid) validKeys.Add(kvp.Key);
        }

        Random r = new((int)Game1.uniqueIDForThisGame + (int)(Game1.stats.DaysPlayed * 1.3f));
        List<string> uncompletedKeys = validKeys.Where(k => !team.completedSpecialOrders.Contains(k)).ToList();

        for (int i = 0; i < 2; i++)
        {
            if (validKeys.Count == 0) break;

            List<string> pool = uncompletedKeys.Count > 0 ? uncompletedKeys : validKeys;
            if (pool.Count == 0) pool = validKeys;

            int index = r.Next(pool.Count);
            string chosenKey = pool[index];

            var newOrder = SpecialOrder.GetSpecialOrder(chosenKey, r.Next());
            if (newOrder != null) team.availableSpecialOrders.Add(newOrder);

            validKeys.Remove(chosenKey);
            uncompletedKeys.Remove(chosenKey);
        }
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        HasViewedBoardToday = false;

        if (Game1.dayOfMonth % 7 == 1)
        {
            GenerateTimberlineOrders();
        }
    }

    private static void OnPlayerWarped(object? sender, WarpedEventArgs e)
    {
        if (e.NewLocation.Name == "Custom_TimberlineFalls" && !HasViewedBoardToday && !Game1.player.team.acceptedSpecialOrderTypes.Contains("Timberline"))
        {
            bool hasOrders = Game1.player.team.availableSpecialOrders.Any(o => o.orderType.Value == "Timberline");

            if (hasOrders)
            {
                Vector2 boardTile = new(37, 49);

                var exclamationPoint = new TemporaryAnimatedSprite(
                    textureName: "LooseSprites\\Cursors",
                    sourceRect: new Rectangle(395, 497, 3, 8),
                    animationInterval: 99999f,
                    animationLength: 1,
                    numberOfLoops: 9999,
                    position: (boardTile * 64f) + new Vector2(21f, -64f),
                    flicker: false,
                    flipped: false,
                    layerDepth: (boardTile.Y * 64f + 64f) / 10000f,
                    alphaFade: 0f,
                    color: Color.White,
                    scale: 4f,
                    scaleChange: 0f,
                    rotation: 0f,
                    rotationChange: 0f
                )
                {
                    yPeriodic = true,
                    yPeriodicLoopTime = 2000f,
                    yPeriodicRange = 8f
                };

                e.NewLocation.temporarySprites.Add(exclamationPoint);
            }
        }
    }

    private static void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button.IsActionButton() || e.Button.IsUseToolButton())
        {
            IsProcessingAction = true;
        }

        if (!Context.IsPlayerFree) return;

        if (e.Button.IsActionButton())
        {
            Vector2 tile = e.Cursor.GrabTile;
            string action = Game1.currentLocation.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Action", "Buildings");

            if (string.IsNullOrEmpty(action))
            {
                action = Game1.currentLocation.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Action", "Back");
            }

            if (action == "TimberlineSpecialOrders")
            {
                HasViewedBoardToday = true;
                Game1.activeClickableMenu = new SpecialOrdersBoard("Timberline");

                for (int i = Game1.currentLocation.temporarySprites.Count - 1; i >= 0; i--)
                {
                    if (Game1.currentLocation.temporarySprites[i].sourceRect is { X: 395, Y: 497 })
                    {
                        Game1.currentLocation.temporarySprites.RemoveAt(i);
                    }
                }
            }
            else if (action == "DionMakeover")
            {
                Game1.currentLocation.createQuestionDialogue(
                    "Get a makeover from Dion? (500g)",
                    Game1.currentLocation.createYesNoResponses(),
                    "Timberline_DionMakeover"
                );
            }
        }
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        IsProcessingAction = false;
    }

    public static bool RecycleColas(string[] args, TriggerActionContext context, out string error)
    {
        error = null!;

        if (args.Length < 2)
        {
            error = string.Empty;
            return false;
        }

        string targetItemId = args[1];
        int goldPerItem = args.Length >= 3 && int.TryParse(args[2], out int parsedGold) ? parsedGold : 30;

        int totalAvailable = Game1.player.Items.Where(item => item != null && item.QualifiedItemId == targetItemId).Sum(item => item.Stack);

        if (totalAvailable <= 0)
        {
            return true;
        }

        Game1.activeClickableMenu = new NumberSelectionMenu(
            string.Empty,
            (amount, price, who) => ProcessRecycling(targetItemId, amount, goldPerItem),
            -1, 1, totalAvailable, totalAvailable
        );

        return true;
    }

    private static void ProcessRecycling(string targetItemId, int amountToRemove, int goldPerItem)
    {
        int remainingToRemove = amountToRemove;

        for (int i = Game1.player.Items.Count - 1; i >= 0; i--)
        {
            Item item = Game1.player.Items[i];
            if (item != null && item.QualifiedItemId == targetItemId)
            {
                if (item.Stack <= remainingToRemove)
                {
                    remainingToRemove -= item.Stack;
                    Game1.player.Items[i] = null!;
                }
                else
                {
                    item.Stack -= remainingToRemove;
                    remainingToRemove = 0;
                }

                if (remainingToRemove <= 0) break;
            }
        }

        int totalReward = amountToRemove * goldPerItem;
        Game1.player.Money += totalReward;

        Game1.playSound("coin");
        Game1.exitActiveMenu();
    }
}
