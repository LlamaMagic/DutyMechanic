using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 92: A Father First solo duty logic.
/// </summary>
public class FatherFirst : AbstractDungeon
{
    private const uint Surecast = 7559;
    private const uint ArmsLength = 7548;
    private readonly CapabilityManagerHandle brawlFacing = CapabilityManager.CreateNewHandle();
    private bool usedBrawlResistance;
    private bool reportedBrawlFacing;
    private DateTime nextTowerCastCancel;

    private static readonly HashSet<uint> CircleAoeCastIds =
    [
        EnemyAction.BurningSunPersistent,
        EnemyAction.BurningSunPersistentHit,
        EnemyAction.BurningSunSmall,
        EnemyAction.BurningSunSmallHit,
    ];

    private static readonly HashSet<uint> DualBlowsFirstCastIds =
    [
        EnemyAction.DualBlowsLeftFirst,
        EnemyAction.DualBlowsRightFirst,
    ];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AFatherFirst;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        

        // The finale has three overlapping casts at different towers. Only the next
        // impact owns the route. A preceding cross can still be resolving as they spawn.
        AvoidanceHelpers.AddAvoidDonut(
            canRun: () => Core.Player.InCombat && !GameObjectManager.GetObjectsOfType<BattleCharacter>()
                .Any(actor => actor.CastingSpellId is EnemyAction.SteeledStrike or EnemyAction.SteeledStrikeShade),
            collectionProducer: NextThrillTower,
            outerRadius: 90f,
            innerRadius: 1.25f);

        // Circle AOEs targeting characters or ground
        AvoidanceManager.AddAvoidLocation(
            canRun: () => Core.Player.InCombat,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => bc.SpellCastInfo?.CastLocation ?? bc.TargetGameObject.Location,
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>()
                .Where(bc => CircleAoeCastIds.Contains(bc.CastingSpellId)));

        // The chase continually re-baits at the player's position. Preserve its escape when
        // overlapping zigzag circles temporarily block the avoidance solver.
        AvoidanceManager.AddAvoidLocation<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            radiusProducer: cast => cast.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: cast => cast.SpellCastInfo.CastLocation,
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>()
                .Where(cast => cast.CastingSpellId == EnemyAction.MorningStarsAimed));
        AvoidanceManager.AddAvoidLocation<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            radiusProducer: cast => cast.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: cast => cast.SpellCastInfo.CastLocation,
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>()
                .Where(cast => cast.CastingSpellId == EnemyAction.MorningStarsZigZag),
            ignoreIfBlocking: true);

        // Persistent circle AOE
        AvoidanceManager.AddAvoidObject<GameObject>(
            canRun: () => Core.Player.InCombat,
            objectSelector: obj => obj.NpcId == EnemyNpc.BurningSunPuddle,
            radiusProducer: obj => 7f);

        // Cone AOEs
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.CoiledStrikeCleave,
            leashPointProducer: () => Core.Player.Location,
            leashRadius: 100f,
            rotationDegrees: 0f,
            radius: 80f,
            arcDegrees: 160f);

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.CoiledStrikeBlueVisual or EnemyAction.CoiledStrikeOrangeVisual,
            radiusProducer: bc => 3.0f));

        // Line AOEs
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.GloryBlaze,
            width: 8f,
            length: 60f);

        // The damage helper holds the real center and angle through impact,
        // including the gap after the boss's visual cast. The shade hits first.
        AvoidanceHelpers.AddAvoidCross<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: actor => actor.CastingSpellId == EnemyAction.SteeledStrikeShade ||
                actor.CastingSpellId == EnemyAction.SteeledStrike &&
                !GameObjectManager.GetObjectsOfType<BattleCharacter>()
                    .Any(other => other.CastingSpellId == EnemyAction.SteeledStrikeShade),
            thickness: 9f,
            length: 30f);

        // Left/right cleaves
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.DualBlowsLeftFirst,
            width: 90f,
            length: 90f,
            xOffset: -43f,
            yOffset: -45f);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.DualBlowsLeftSecond && bc.SpellCastInfo.RemainingCastTime.TotalSeconds <= 2.0 && !DualBlowsFirstCastIds.IsCasting(),
            width: 90f,
            length: 90f,
            xOffset: -43f,
            yOffset: -45f);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.DualBlowsRightFirst,
            width: 90f,
            length: 90f,
            xOffset: 43f,
            yOffset: -45f);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.DualBlowsRightSecond && bc.SpellCastInfo.RemainingCastTime.TotalSeconds <= 2.0 && !DualBlowsFirstCastIds.IsCasting(),
            width: 90f,
            length: 90f,
            xOffset: 43f,
            yOffset: -45f);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        if (WorldManager.ZoneId == (uint)ZoneId.AFatherFirst)
        {
            SidestepPlugin.Enabled = false;
            return HandleBrawlEnder() || HoldOffenseForThrillTowers();
        }
        else
        {
            SidestepPlugin.Enabled = true;
        }

        return false;
    }

    private static Vector3[] NextThrillTower()
    {
        var pending = GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .Where(IsThrillTower)
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .ThenBy(actor => actor.ObjectId)
            .Take(2)
            .ToArray();
        if (pending.Length == 0)
            return [];

        var target = pending[0].Location;
        if (pending.Length == 2)
        {
            // Stage on the side of this tower nearest the next one. The 1.25-yalm
            // offset and safe radius both fit inside the server's three-yalm tower.
            var next = pending[1].Location;
            var distance = target.Distance2D(next);
            if (distance > 1.25f)
                target += (next - target) * (1.25f / distance);
        }
        return [target];
    }

    private static bool IsThrillTower(BattleCharacter actor) =>
        actor.CastingSpellId is EnemyAction.TheThrillShade or EnemyAction.TheThrill;

    private bool HoldOffenseForThrillTowers()
    {
        if (!Core.Me.IsAlive || !GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(IsThrillTower))
            return false;

        // The final towers resolve about two seconds apart. A full Verstone
        // delayed native avoidance until the second soak was already lost.
        var now = DateTime.UtcNow;
        if (Core.Me.IsCasting && now >= nextTowerCastCancel)
        {
            ActionManager.StopCasting();
            nextTowerCastCancel = now.AddMilliseconds(250);
        }
        return true;
    }

    private bool HandleBrawlEnder()
    {
        var cast = GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .FirstOrDefault(actor => actor.NpcId == EnemyNpc.GuloolJaJa &&
                actor.CastingSpellId == EnemyAction.BrawlEnder)?.SpellCastInfo;
        if (cast == null || !cast.IsValid)
        {
            usedBrawlResistance = false;
            reportedBrawlFacing = false;
            return false;
        }

        // The bind starts when the five-second cast ends. The actual push follows
        // about 2.6 seconds later, so apply the six-second role buff during the cast.
        var remaining = cast.RemainingCastTime.TotalSeconds;
        if (usedBrawlResistance || remaining > 2.1 || !Core.Me.IsAlive)
            return false;
        var action = ActionManager.CanCast(Surecast, Core.Me) ? Surecast :
            ActionManager.CanCast(ArmsLength, Core.Me) ? ArmsLength : 0;
        if (action != 0 && ActionManager.DoAction(action, Core.Me))
        {
            usedBrawlResistance = true;
            Logger.Information($"[A Father First] Used knockback resistance ({action}) for Brawl Ender.");
            return true;
        }

        // Brawl Ender can repeat in about 90 seconds, before either role action
        // has recovered. Aim the player-facing push outside the current puddles.
        // Native escape owns movement and facing whenever it is actively dodging.
        if (remaining > 1.5 || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid)
            return false;

        var player = Core.Me.Location;
        var hazards = AvoidanceManager.Avoids.Where(avoid => avoid.IsValid && !avoid.ShouldIgnore).ToArray();
        if (hazards.Length == 0 || hazards.Any(avoid => avoid.IsPointInAvoid(player)))
            return false;

        foreach (var step in Enumerable.Range(-7, 16).OrderBy(offset => Math.Abs(offset)))
        {
            var angle = Core.Me.Heading + step * MathF.PI / 8;
            var direction = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
            var landing = PredictBrawlLanding(player, direction);
            if (hazards.Any(avoid => avoid.IsPointInAvoid(landing)))
                continue;

            // Finish turning before the bind. An in-flight spell can auto-face the
            // boss at resolution, undoing the heading during that brief window.
            CapabilityManager.Update(brawlFacing, CapabilityFlags.Facing, 350, "Brawl Ender safe facing");
            if (Core.Me.IsCasting)
                ActionManager.StopCasting();
            Core.Me.SetFacing(angle);
            if (!reportedBrawlFacing)
            {
                reportedBrawlFacing = true;
                Logger.Information($"[A Father First] Aiming Brawl Ender from {player} toward {landing}.");
            }
            return true;
        }

        return false;
    }

    private static Vector3 PredictBrawlLanding(Vector3 player, Vector3 direction)
    {
        // The 20-yalm push stops at Sunperch's arena wall. Use a half-yalm inset
        // of the 30-by-40-yalm platform centered at (0, 49).
        var distance = 20f;
        if (MathF.Abs(direction.X) > 0.001f)
            distance = MathF.Min(distance, ((direction.X > 0 ? 14.5f : -14.5f) - player.X) / direction.X);
        if (MathF.Abs(direction.Z) > 0.001f)
            distance = MathF.Min(distance, ((direction.Z > 0 ? 68.5f : 29.5f) - player.Z) / direction.Z);
        return player + direction * MathF.Max(0, distance);
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Main boss.
        /// </summary>
        public const uint GuloolJaJa = 12675;

        /// <summary>
        /// Untargetable adds.
        /// </summary>
        public const uint ShadeGuloolJaJa = 12676;

        /// <summary>
        /// Large persistent fire puddle.
        /// </summary>
        public const uint BurningSunPuddle = 2002331;
    }

    private static class EnemyAura
    {
        /// <summary>
        /// Stunned by pseudo-cutscene.
        /// </summary>
        public const uint InEvent = 1268;

        /// <summary>
        /// AOE stun from <see cref="EnemyAction.BattleBreaker"/>.
        /// </summary>
        public const uint DownForTheCount = 1963;
    }

    private static class EnemyAction
    {
        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s raid-wide AOE. 60 yalms radius.
        /// </summary>
        public const uint FancyBladework = 36413;  // 0x8E3D

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s orange/clockwise spin cleave. Visual + dummy cast.
        /// </summary>
        public const uint CoiledStrikeOrangeVisual = 36405;  // 0x8E35

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s blue/counter-clockwise spin cleave. Visual + dummy cast.
        /// </summary>
        public const uint CoiledStrikeBlueVisual = 36406;  // 0x8E36

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s spin cleave cones.
        /// </summary>
        public const uint CoiledStrikeCleave = 36407;  // 0x8E37

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s AOE stun before cutscene.
        /// </summary>
        public const uint BattleBreaker = 36414;  // 0x8E3E

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s zig zag-aimed circle AOE. Ground Target.
        /// </summary>
        public const uint MorningStarsZigZag = 39135;  // 0x98DF

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s player-aimed circle AOE. Ground Target.
        /// </summary>
        public const uint MorningStarsAimed = 38819;  // 0x97A3

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s fire circle AOE spam. Dummy Cast.
        /// </summary>
        public const uint BurningSunDummy = 36408;  // 0x8E38

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s small fire circle AOE spam.
        /// </summary>
        public const uint BurningSunSmall = 36409;  // 0x8E39

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s small fire circle AOE spam.
        /// </summary>
        public const uint BurningSunSmallHit = 36411;  // 0x8E3B

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s large fire circle AOE spam. Persistent puddle.
        /// </summary>
        public const uint BurningSunPersistent = 36410;  // 0x8E3A

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s large fire circle AOE spam. Persistent puddle.
        /// </summary>
        public const uint BurningSunPersistentHit = 36412;  // 0x8E3C

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s TODO. Ground Target.
        /// </summary>
        public const uint BlankCastA = 36409;  // 0x8E39

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s TODO. Ground Target.
        /// </summary>
        public const uint BlankCastB = 36410;  // 0x8E3A

        /// <summary>
        /// <see cref="EnemyNpc.ShadeGuloolJaJa"/>'s line AOE.
        /// </summary>
        public const uint GloryBlaze = 36417;  // 0x8E41

        /// <summary>
        /// Five-second cast before the delayed forced movement through Burning Sun puddles.
        /// </summary>
        public const uint BrawlEnder = 36397;

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s left half-room cleave.
        /// </summary>
        public const uint DualBlowsLeftSecond = 35423;  // 0x8A5F

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s right -> left half-room cleaves. Dummy cast.
        /// </summary>
        public const uint DualBlowsRightLeft = 35424;  // 0x8A60

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s right half-room cleave.
        /// </summary>
        public const uint DualBlowsRightFirst = 36395;  // 0x8E2B

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s left half-room cleave.
        /// </summary>
        public const uint DualBlowsLeftFirst = 36393;  // 0x8E29

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s left -> right half-room cleaves. Dummy cast.
        /// </summary>
        public const uint DualBlowsLeftRight = 35422;  // 0x8A5E

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s right half-room cleave.
        /// </summary>
        public const uint DualBlowsRightSecond = 35421;  // 0x8A5D

        /// <summary>
        /// <see cref="EnemyNpc.ShadeGuloolJaJa"/>'s stand-in tower. Ground Target.
        /// </summary>
        public const uint TheThrillShade = 38817;

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s stand-in tower. Ground Target.
        /// </summary>
        public const uint TheThrill = 36420;

        /// <summary>
        /// <see cref="EnemyNpc.ShadeGuloolJaJa"/>'s Cross AOE. Ground Target.
        /// </summary>
        public const uint SteeledStrikeShade = 36392;

        /// <summary>
        /// <see cref="EnemyNpc.GuloolJaJa"/>'s Cross AOE. Ground Target.
        /// </summary>
        public const uint SteeledStrike = 36390;
    }
}
