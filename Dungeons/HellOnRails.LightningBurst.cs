using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class HellOnRails
{
    private readonly Dictionary<uint, Vector3> lightningBurstCenters = [];
    private readonly HashSet<uint> lightningBurstSnapshots = [];
    private DateTime lightningBurstUntil;
    private bool lightningBurstVisual;

    private void RegisterLightningBurstAvoid()
    {
        // Marker 343 exposes both tank targets five seconds before the 0.2s helper casts.
        // Track each target until the helper snapshots, then hold that position through damage.
        // The six-yalm avoid adds one yalm of clearance to the five-yalm tankbuster.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            canRun: () => IsInHellOnRailsCombat() && DateTime.UtcNow < lightningBurstUntil,
            collectionProducer: () => lightningBurstCenters.Where(pair => pair.Key != Core.Me.ObjectId).Select(pair => pair.Value),
            radiusProducer: point => 6.0f,
            locationProducer: point => point,
            ignoreIfBlocking: false);
    }

    private void UpdateLightningBurst()
    {
        var now = DateTime.UtcNow;
        var visual = FindCaster(EnemyAction.LightningBurstVisual);
        if (visual != null)
        {
            if (!lightningBurstVisual)
            {
                lightningBurstCenters.Clear();
                lightningBurstSnapshots.Clear();
            }
            lightningBurstUntil = now + visual.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
        }
        lightningBurstVisual = visual != null;
        if (now >= lightningBurstUntil)
        {
            lightningBurstCenters.Clear();
            lightningBurstSnapshots.Clear();
        }
        if (visual != null)
        {
            foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false))
                if (!actor.IsNpc && actor.IsValid && HasEncounterMarker(actor, 343) && !lightningBurstSnapshots.Contains(actor.ObjectId))
                    lightningBurstCenters[actor.ObjectId] = actor.Location;
        }
        foreach (var helper in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsCasting && actor.CastingSpellId == EnemyAction.LightningBurst))
        {
            uint targetId = helper.SpellCastInfo.TargetId;
            var target = GameObjectManager.GetObjectByObjectId(targetId);
            if (target == null || !target.IsValid) continue;
            if (lightningBurstSnapshots.Add(targetId)) lightningBurstCenters[targetId] = target.Location;
            // Damage was still pending after the helper disappeared and the tank moved away.
            // Retain scalar positions for the observed effect tail; never retain native wrappers.
            lightningBurstUntil = now + helper.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }
    }

    private static bool HasEncounterMarker(BattleCharacter actor, uint id) => actor.VfxContainer.IsValid &&
        actor.VfxContainer.Vfx.Any(marker => marker != null && marker.IsValid && marker.Id == id);

    private void ResetLightningBurst()
    {
        lightningBurstCenters.Clear();
        lightningBurstSnapshots.Clear();
        lightningBurstUntil = DateTime.MinValue;
        lightningBurstVisual = false;
    }
}
