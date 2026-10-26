using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 80.3: Anamnesis Anyder dungeon logic.
/// </summary>
public partial class AnamnesisAnyder : AbstractDungeon
{
    private const uint UnknownA = 9260;
    private const uint UnknownB = 9261;
    private const uint Kyklops = 9263;
    private static readonly HashSet<uint> LuminousRayA = [20006];
    private static readonly HashSet<uint> LuminousRayB = [20007];
    private static readonly int LuminousRayDuration = 6_000;
    private static readonly HashSet<uint> FallingWater = [19325];
    private static readonly int FallingWaterDuration = 6_000;
    private static readonly HashSet<uint> NonSidestepSpells = [19325, 19327, 19328, 19332, 19339, 19322, 20006, 20007, ];
    private static DateTime luminousRayTimestamp = DateTime.MinValue;
    private static DateTime luminousRayBTimestamp = DateTime.MinValue;
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AnamnesisAnyder;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [19327, 19328, 19332, 19339, 19322, ];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Release encounter state before death or loading recovery can issue new movement.
        if (ff14bot.Core.Me == null || !ff14bot.Core.Me.IsValid || !ff14bot.Core.Me.IsAlive || ff14bot.Behavior.CommonBehaviors.IsLoading)
        {
            ResetRukshs();
            ResetKyklops();
            ResetUnknownGeometry();
            return false;
        }

        // Rukshs uses target semantics and native cross geometry, preserving
        // independent hazards instead of entering the legacy global-clear spread.
        if (RukshsActive())
        {
            return RunRukshs();
        }

        ResetRukshs();
        // Kyklops owns its cast geometry and the actual stack target. The legacy
        // closest-ally follow can cross tiles and erase concurrent spread hazards.
        if (KyklopsActive())
        {
            return RunKyklops();
        }

        ResetKyklops();
        // Scrutiny's validated support response owns its overlap window. Do not
        // let legacy Ectoplasmic follow request a second destination during it.
        if (UnknownActive())
        {
            UpdateUnknownGeometry();
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                unknownSupportMoving = false;
                return false;
            }

            if (unknownSupportPosition.HasValue)
            {
                return PositionUnknownSupport();
            }
        }
        else
        {
            ResetUnknownGeometry();
        }

        await FollowDodgeSpells();
        if (LuminousRayA.IsCasting() && luminousRayTimestamp < DateTime.Now)
        {
            BattleCharacter unknownNpcA = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(UnknownA).FirstOrDefault(bc => bc.IsValid && bc.IsAlive && bc.IsTargetable && bc.IsCasting && bc.CastingSpellId == 20006);
            // The original and clone have separate actions and lifetimes. The
            // original can already be dead when the clone casts (observed null crash).
            if (unknownNpcA == null)
            {
                return false;
            }

            SidestepPlugin.Enabled = true;
            AvoidanceHelpers.AddAvoidRectangle(unknownNpcA, 9.0f, 51.0f, yOffset: -0.5f);
            luminousRayTimestamp = DateTime.Now.AddMilliseconds(LuminousRayDuration);
        }

        if (LuminousRayB.IsCasting() && luminousRayBTimestamp < DateTime.Now)
        {
            BattleCharacter unknownNpcB = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(UnknownB).FirstOrDefault(bc => bc.IsValid && bc.IsAlive && bc.IsTargetable && bc.IsCasting && bc.CastingSpellId == 20007);
            if (unknownNpcB == null)
            {
                return false;
            }

            SidestepPlugin.Enabled = true;
            //50x8 installed/reference line, with0.5y margin on every edge.
            AvoidanceHelpers.AddAvoidRectangle(unknownNpcB, 9.0f, 51.0f, yOffset: -0.5f);
            luminousRayBTimestamp = DateTime.Now.AddMilliseconds(LuminousRayDuration);
        }

        if (FallingWater.IsCasting())
        {
            SidestepPlugin.Enabled = false;
            AvoidanceManager.RemoveAllAvoids(i => i.CanRun);
            await MovementHelpers.Spread(FallingWaterDuration, 10f);
        }

        SidestepPlugin.Enabled = !NonSidestepSpells.IsCasting();
        await Coroutine.Yield();
        return false;
    }
}
