using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using LlamaLibrary.RemoteWindows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DutyMechanic.Extensions;
using DutyMechanic.Logging;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 50.3: The Porta Decumana dungeon logic.
/// </summary>
public class PortaDecumana : AbstractDungeon
{
    private static readonly int CitadelBusterDuration = 5_000;
    private static readonly int LaserFocusDuration = 5_000;
    private static readonly int HomingRayDuration = 5_000;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.ThePortaDecumana;

    /// <inheritdoc/>
    public override DungeonId DungeonId => DungeonId.ThePortaDecumana;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new() { };

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        // General avoid while in combat to avoid standing on top of people
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && !Core.Player.IsMelee() && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana && !EnemyAction.LaserFocusHash.IsCasting(),
            objectSelector: bc => bc.Type == GameObjectType.Pc,
            radiusProducer: bc => 0.5f,
            priority: AvoidancePriority.Low));

        // Ultima Titan: GeoCrush
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Geocrush,
            radiusProducer: bc => 25.0f,
            priority: AvoidancePriority.High));

        // The Ultima Weapon: Eye of the Storm
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana,
            objectSelector: c => c.CastingSpellId == EnemyAction.EyeoftheStorm,
            outerRadius: 90.0f,
            innerRadius: 12f,
            priority: AvoidancePriority.Medium);

        // Ultima Ifrit: Vulcan Burst
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana,
            objectSelector: c => c.CastingSpellId == EnemyAction.VulcanBurst,
            outerRadius: 40.0f,
            innerRadius: 3.0F,
            priority: AvoidancePriority.Medium);

        // Boss 1
        // Let's avoid standing under the boss if we can help it.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana && !EnemyAction.LaserFocusHash.IsCasting(),
            objectSelector: bc => bc.NpcId == EnemyNpc.UltimaWeapon && bc.CanAttack,
            radiusProducer: bc => 4f,
            priority: AvoidancePriority.Medium));

        // The Ultima Weapon: Citadel Buster
        // Every time I tried to use a Rectangle here I couldn't get it to face in front of the mob. Always went off the side.
        // So i took the easy way out and made it a cone
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana,
            objectSelector: (bc) => bc.CastingSpellId == EnemyAction.CitadelBuster,
            leashPointProducer: () => ArenaCenter.UltimaArenaCenter1,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 180.0f);

        // The Ultima Weapon: Explosion
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana && !EnemyAction.AssaultCannon.IsCasting(),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Explosion,
            radiusProducer: bc => 17.4f,
            priority: AvoidancePriority.High));

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana,
            () => ArenaCenter.UltimaArenaCenter1,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.ThePortaDecumana,
            () => ArenaCenter.UltimaArenaCenter2,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        // This will press yes on ReadyCheck
        if (NotificationReadyCheck.Instance.IsOpen)
        {
            await UsefulTasks.HandleReadyCheck();
        }

        if (Core.Me.IsDPS() && LimitBreak.Percentage == 3 && (Core.Me.HasTarget && Core.Me.CurrentTarget.IsValid && Core.Me.CurrentTarget.CurrentHealthPercent > 1))
        {
            var limitBreak = DataManager.GetSpellData((uint)ClassJobRoles.LimitBreak3[Core.Me.CurrentJob]);

            Logger.Information($"Using {limitBreak.Name} on {Core.Me.CurrentTarget.Name}.");

            if (limitBreak.GroundTarget)
            {
                ActionManager.DoActionLocation(limitBreak.Id, Core.Me.CurrentTarget.Location);
                await Coroutine.Wait(10000, () => !Core.Me.IsCasting);
            }
            else
            {
                ActionManager.DoAction(limitBreak.Id, Core.Me.CurrentTarget);
                await Coroutine.Wait(10000, () => !Core.Me.IsCasting);
            }
        }

        // Stay within casting range of the tank if you're the healer
        if (Core.Me.IsHealer() && Core.Me.IsAlive && !CommonBehaviors.IsLoading &&
            !QuestLogManager.InCutscene && Core.Me.InCombat)
        {
            BattleCharacter tankPlayer = PartyManager.VisibleMembers
                .Select(pm => pm.BattleCharacter)
                .FirstOrDefault(obj => !obj.IsMe && obj.IsValid
                                                 && obj.IsTank());

            if (tankPlayer != null && PartyManager.IsInParty && !CommonBehaviors.IsLoading &&
                !QuestLogManager.InCutscene && Core.Me.Location.Distance2D(tankPlayer.Location) > 30)
            {
                await tankPlayer.Follow(15.0F, 0, true);
                await CommonTasks.StopMoving();
                await Coroutine.Sleep(30);
            }
        }

        // Soak Aetheroplasms if you're not the tank
        if (!Core.Me.IsTank() && Core.Me.IsAlive && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene && Core.Me.InCombat)
        {
            BattleCharacter aetheroplasmSoak = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.Aetheroplasm).OrderBy(bc => bc.Distance2D()).FirstOrDefault(bc => bc.IsVisible && bc.CurrentHealth > 0);

            if (aetheroplasmSoak != null && PartyManager.IsInParty && !CommonBehaviors.IsLoading &&
                !QuestLogManager.InCutscene && Core.Me.Location.Distance2D(aetheroplasmSoak.Location) > 1)
            {
                await aetheroplasmSoak.Follow(1F, 0, true);
                await CommonTasks.StopMoving();
                await Coroutine.Sleep(30);
            }
        }

        if (EnemyAction.LaserFocusHash.IsCasting() && Core.Me.IsAlive && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, LaserFocusDuration, $"Stacking for Laser Focus");

            BattleCharacter laserFocusTarget = PartyManager.VisibleMembers
                .Select(pm => pm.BattleCharacter)
                .FirstOrDefault(obj => !obj.IsMe && obj.IsAlive);

            await laserFocusTarget.Follow(4f, useMesh: true);
            await CommonTasks.StopMoving();
            await Coroutine.Sleep(30);
        }

        if (EnemyAction.HomingRay.IsCasting())
        {
            await MovementHelpers.Spread(HomingRayDuration);
        }

        await Coroutine.Yield();

        return false;
    }

    internal static class EnemyNpc
    {
        /// <summary>    private const int AetheroplasmNpc = 2138;
        /// First Boss: Ultima Weapon
        /// </summary>
        public const uint UltimaWeapon = 2137;

        /// <summary>
        /// First Boss: Aetheroplasm
        /// </summary>
        public const uint Aetheroplasm = 2138;
    }

    internal static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.UltimaWeapon"/>.
        /// </summary>
        public static readonly Vector3 UltimaArenaCenter2 = new(-703.6115f, -185.6595f, 479.6159f);

        /// <summary>
        /// First Boss: <see cref="EnemyNpc.UltimaWeapon"/>.
        /// </summary>
        public static readonly Vector3 UltimaArenaCenter1 = new(-771.9428f, -400.0628f, -600.3899f);
    }

    internal static class EnemyAction
    {
        /// <summary>
        /// The Ultima Weapon
        /// Assault Cannon
        /// Magitek Bits come out and do various multilined attacks.
        /// </summary>
        public static readonly HashSet<uint> AssaultCannon = new() { 29019 };

        /// <summary>
        /// The Ultima Weapon
        /// Homing Ray
        ///
        /// </summary>
        public static readonly HashSet<uint> HomingRay = new() { 29011, 29012 };

        /// <summary>
        /// The Ultima Weapon
        /// Citadel Buster
        /// Straight line AOE
        /// </summary>
        public const uint CitadelBuster = 29020;

        public static readonly HashSet<uint> CitadelBusterHash = new() { 29020 };


        /// <summary>
        /// The Ultima Weapon
        /// Laser Focus
        /// Gather to soak ability.
        /// </summary>
        public const uint LaserFocus = 29014;

        public static readonly HashSet<uint> LaserFocusHash = new() { 29013, 29014 };

        /// <summary>
        /// The Ultima Weapon
        /// Geocrush
        ///
        /// </summary>
        public const uint Geocrush = 28999;

        /// <summary>
        /// The Ultima Weapon
        /// Eye of the Storm
        ///
        /// </summary>
        public const uint EyeoftheStorm = 28980;

        /// <summary>
        /// The Ultima Weapon
        /// Vulcan Burn
        ///
        /// </summary>
        public const uint VulcanBurst = 29003;


        /// <summary>
        /// The Ultima Weapon
        /// Explosion
        ///
        /// </summary>
        public const uint Explosion = 29021;

        // Below this line are unavoidable moves that but are here for documentation's sake

        /// <summary>
        /// The Ultima Weapon
        /// Earthen Eternity
        /// This move is cast on Granite Goals to have them cast Granite Sepulchre
        /// </summary>
        public const uint EarthenEternity = 29435;

        /// <summary>
        /// The Ultima Weapon
        /// Granite Interment
        /// This move causes Granite Goals to form around all players, causing them to be unable to move.
        /// </summary>
        public const uint GraniteInterment = 28987;

        /// <summary>
        /// Granite Goal
        /// Granite Sepulchre
        /// This move causes the Granite Goal you're surrounded with to explode causing unavoidable damage.
        /// </summary>
        public const uint GraniteSepulchre = 28989;

        /// <summary>
        /// The Ultima Weapon
        /// Headsman's Wind
        /// This move is casted but doesn't actually ever complete.
        /// </summary>
        public const uint HeadsmansWind = 28989;

        /// <summary>
        /// The Ultima Weapon
        /// Earthen Fury (Titan)
        /// This move is casted but doesn't actually ever complete.
        /// </summary>
        public const uint EarthenFury = 28998;

        /// <summary>
        /// The Ultima Weapon
        /// Radiant Blaze
        /// This move is casted but doesn't actually ever complete. It's stopped by doing enough damage to make Ifirit leave Ultima Weapon.
        /// </summary>
        public static readonly HashSet<uint> RadiantBlaze = new() { 28991 };

        /// <summary>
        /// The Ultima Weapon
        /// Vortex Barrier (Garuda)
        /// This move causes a wind shield around Ultima Weapon.
        /// </summary>
        public const uint VortexBarrier = 28984;

        /// <summary>
        /// The Ultima Weapon
        /// Hellfire (Ifrit)
        /// This move causes a wind shield around Ultima Weapon.
        /// </summary>
        public static readonly HashSet<uint> Hellfire = new() { 28978, 29002 };

        /// <summary>
        /// The Ultima Weapon
        /// Ultima
        /// Kill the boss before this move finishes or it's a wipe.
        /// </summary>
        public const uint Ultima = 29024;
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Overseer Kanilokka
        /// Aura Name: Temporary Misdirection, Aura Id: 3909
        /// Causes loss of control of your character
        /// </summary>
        public const uint TemporaryMisdirection = 3909;

        public const uint Rampart = 1191;
    }
}
