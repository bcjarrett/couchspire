using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using CouchSpire.Scripts.Runtime;
using CouchSpire.Scripts.Runtime.Couch;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.addons.mega_text;

namespace CouchSpire.Scripts.Patch;

/// <summary>
/// Couch co-op: an attack intent's number is the damage the local player (<see cref="LocalContext.GetMe"/>) would
/// take — online every player sees their own. On the couch there is one screen and the local player is the driver,
/// so after P1 plays Apparition (Intangible) every intent reads 1 and P2 can't see what will hit them. When the couch
/// players would take different damage, the label shows each one in seat order, e.g. "1 / 12".
/// </summary>
[HarmonyPatch(typeof(NIntent), "UpdateVisuals")]
internal static class NIntentPatch
{
    private static readonly AccessTools.FieldRef<NIntent, AbstractIntent> IntentRef =
        AccessTools.FieldRefAccess<NIntent, AbstractIntent>("_intent");

    private static readonly AccessTools.FieldRef<NIntent, IEnumerable<Creature>> TargetsRef =
        AccessTools.FieldRefAccess<NIntent, IEnumerable<Creature>>("_targets");

    private static readonly AccessTools.FieldRef<NIntent, Creature> OwnerRef =
        AccessTools.FieldRefAccess<NIntent, Creature>("_owner");

    private static readonly AccessTools.FieldRef<NIntent, MegaRichTextLabel> ValueLabelRef =
        AccessTools.FieldRefAccess<NIntent, MegaRichTextLabel>("_valueLabel");

    [HarmonyPostfix]
    private static void PostfixUpdateVisuals(NIntent __instance)
    {
        if (!LocalSelfCoopContext.IsEnabled || IntentRef(__instance) is not AttackIntent attack)
        {
            return;
        }

        IEnumerable<Creature> targets = TargetsRef(__instance);
        Creature owner = OwnerRef(__instance);
        List<ulong> playerIds = targets
            .Where((Creature creature) => creature.IsPlayer && creature.IsAlive)
            .Select((Creature creature) => creature.Player!.NetId)
            .Where((ulong id) => LocalSelfCoopContext.LocalPlayerIds.Contains(id))
            .OrderBy((ulong id) => LocalSelfCoopContext.TryGetSlotIndex(id, out int slot) ? slot : int.MaxValue)
            .ToList();
        if (playerIds.Count < 2)
        {
            return;
        }

        // The game reads the defender from LocalContext, so evaluate the label as each couch player in turn.
        List<string> labels = new();
        ulong? driver = LocalContext.NetId;
        try
        {
            foreach (ulong id in playerIds)
            {
                LocalContext.NetId = id;
                labels.Add(attack.GetIntentLabel(targets, owner).GetFormattedText() ?? "");
            }
        }
        finally
        {
            LocalContext.NetId = driver;
        }

        if (labels.Distinct().Count() > 1)
        {
            string text = string.Join(" / ", labels);
            ValueLabelRef(__instance).Text = text;
            CouchLog.Throttled($"intent-{owner.CombatId}", $"Per-player intent for {owner.Name}: {text}", 5000);
        }
    }
}
