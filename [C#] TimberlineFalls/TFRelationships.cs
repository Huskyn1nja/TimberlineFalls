using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using System.Collections.Generic;
using System.Linq;
using TimberlineFalls.Models;

namespace TimberlineFalls.Patches
{
    public static class TFRelationships
    {
        private const string AmberClaspId = "(O)Huskyn1nja.TimberFalls_AmberClasp";
        private const string MermaidPendantId = "(O)460";

        public static readonly HashSet<string> BaseTimberlineResidents = new HashSet<string>
        {
            "Huskyn1nja.TimberFalls_Arden",
            "Huskyn1nja.TimberFalls_Blaise",
            "Huskyn1nja.TimberFalls_Cassidy",
            "Huskyn1nja.TimberFalls_Elara",
            "Huskyn1nja.TimberFalls_Gustavo",
            "Huskyn1nja.TimberFalls_Jax",
            "Huskyn1nja.TimberFalls_Jett",
            "Huskyn1nja.TimberFalls_Mara",
            "Huskyn1nja.TimberFalls_Remy",
            "Huskyn1nja.TimberFalls_Riley",
            "Huskyn1nja.TimberFalls_Roxy",
            "Huskyn1nja.TimberFalls_Vance",
        };

        public static void Initialize(IModHelper helper, Harmony harmony, IManifest manifest)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(NPC), nameof(NPC.tryToReceiveActiveObject)),
                prefix: new HarmonyMethod(typeof(TFRelationships), nameof(Prefix_TryToReceiveActiveObject))
            );
        }

        private static string GetShortName(string fullName)
        {
            int underscore = fullName.LastIndexOf('_');
            return underscore >= 0 ? fullName.Substring(underscore + 1) : fullName;
        }

        private static void ShowReply(NPC npc, string dialogueKey)
        {
            Dialogue? line = npc.TryGetDialogue(dialogueKey);

            npc.CurrentDialogue.Push(line ?? new Dialogue(npc, null, $"[Missing Key: {dialogueKey}]"));
            Game1.drawDialogue(npc);
        }

        private static void FaceNpc(Farmer player, NPC npc)
        {
            player.Halt();
            player.faceGeneralDirection(npc.getStandingPosition(), 0, opposite: false, useTileCalculations: false);
        }

        public static bool Prefix_TryToReceiveActiveObject(NPC __instance, Farmer who, bool probe, ref bool __result)
        {
            string? heldItemId = who.ActiveObject?.QualifiedItemId;
            if (heldItemId == null) return true;

            if (heldItemId != AmberClaspId && heldItemId != MermaidPendantId)
                return true;

            if (ModEntry.AmberClaspTargets.Count == 0) ModEntry.RefreshClaspTargets();

            string shortName = GetShortName(__instance.Name);

            bool listedInTargets = ModEntry.AmberClaspTargets.TryGetValue(__instance.Name, out ClaspCandidateModel? claspEntry) ||
                                   ModEntry.AmberClaspTargets.TryGetValue(shortName, out claspEntry);

            bool isCoreResident = BaseTimberlineResidents.Contains(__instance.Name);

            if (!isCoreResident && !listedInTargets)
                return true;

            who.friendshipData.TryGetValue(__instance.Name, out Friendship? friendship);
            int friendshipPoints = friendship?.Points ?? 0;

            if (heldItemId == MermaidPendantId)
            {
                if (!probe)
                {
                    FaceNpc(who, __instance);

                    string replyKey = isCoreResident ? "RejectPendant" : "RejectMermaidPendant";

                    if (!isCoreResident)
                    {
                        if (friendship != null && friendship.IsDivorced())
                            replyKey = "RejectMermaidPendant_Divorced";
                        else if (!__instance.datable.Value)
                            replyKey = "RejectMermaidPendant_NotDatable";
                        else if (friendship != null && who.spouse == __instance.Name && (friendship.IsEngaged() || friendship.IsMarried()))
                            replyKey = "RejectMermaidPendant_AlreadyTogether";
                        else if (friendship != null && (friendship.IsEngaged() || __instance.isMarried()))
                            replyKey = "RejectMermaidPendant_NpcWithSomeoneElse";
                        else if (who.spouse != null && who.spouse != __instance.Name)
                            replyKey = "RejectMermaidPendant_PlayerWithSomeoneElse";
                        else if (friendshipPoints < 2000)
                            replyKey = "RejectMermaidPendant_Under8Hearts";
                        else if (friendshipPoints < 2500)
                            replyKey = "RejectMermaidPendant_Under10Hearts";
                        else if (who.HouseUpgradeLevel < 1)
                            replyKey = "RejectMermaidPendant_NeedHouseUpgrade";
                    }

                    ShowReply(__instance, replyKey);
                }

                __result = true;
                return false;
            }

            string rejectKeyBase = "RejectClasp";
            string acceptReplyKey = "AcceptClasp";

            if (!isCoreResident && claspEntry != null)
            {
                if (!string.IsNullOrEmpty(claspEntry.RejectDialogueKey)) rejectKeyBase = claspEntry.RejectDialogueKey;
                if (!string.IsNullOrEmpty(claspEntry.AcceptDialogueKey)) acceptReplyKey = claspEntry.AcceptDialogueKey;
            }

            string replyToShow = rejectKeyBase;
            bool wasTurnedDown = true;

            if (friendship != null && friendship.IsDivorced())
                replyToShow = $"{rejectKeyBase}_Divorced";
            else if (!__instance.datable.Value)
                replyToShow = $"{rejectKeyBase}_NotDatable";
            else if (friendship != null && who.spouse == __instance.Name && (friendship.IsEngaged() || friendship.IsMarried()))
                replyToShow = $"{rejectKeyBase}_AlreadyTogether";
            else if (friendship != null && (friendship.IsEngaged() || __instance.isMarried()))
                replyToShow = $"{rejectKeyBase}_NpcWithSomeoneElse";
            else if (who.spouse != null && who.spouse != __instance.Name)
                replyToShow = $"{rejectKeyBase}_PlayerWithSomeoneElse";
            else if (friendshipPoints < 2000)
                replyToShow = $"{rejectKeyBase}_Under8Hearts";
            else if (friendshipPoints < 2500)
                replyToShow = $"{rejectKeyBase}_Under10Hearts";
            else if (who.HouseUpgradeLevel < 1)
                replyToShow = $"{rejectKeyBase}_NeedHouseUpgrade";
            else
                wasTurnedDown = false;

            if (wasTurnedDown)
            {
                if (!probe)
                {
                    FaceNpc(who, __instance);
                    ShowReply(__instance, replyToShow);
                }

                __result = true;
                return false;
            }

            if (!probe)
            {
                FaceNpc(who, __instance);

                Game1.changeMusicTrack("silence");
                who.spouse = __instance.Name;
                who.removeDatingActiveDialogueEvents(__instance.Name);

                WorldDate weddingDay = new WorldDate(Game1.Date);
                weddingDay.TotalDays += 3;

                while (!Game1.canHaveWeddingOnDay(weddingDay.DayOfMonth, weddingDay.Season))
                {
                    weddingDay.TotalDays++;
                }

                if (friendship != null)
                {
                    friendship.WeddingDate = weddingDay;
                    friendship.Status = FriendshipStatus.Engaged;
                }
                else
                {
                    who.friendshipData.Add(__instance.Name, new Friendship()
                    {
                        Points = friendshipPoints,
                        Status = FriendshipStatus.Engaged,
                        WeddingDate = weddingDay
                    });
                }

                who.changeFriendship(1, __instance);
                who.reduceActiveItemByOne();
                who.completelyStopAnimatingOrDoingAction();

                ShowReply(__instance, acceptReplyKey);
            }

            __result = true;
            return false;
        }
    }
}
