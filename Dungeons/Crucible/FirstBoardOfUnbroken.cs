using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DutyMechanic.Data;
using DutyMechanic.Dungeons;
using DutyMechanic.Helpers;
using Clio.Utilities;
using ff14bot;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Navigation;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons
{
    /// <summary>Owns First Board enemy avoidance and captures encounter evidence without treating friendly pet attacks as hazards.</summary>
    public sealed class FirstBoardOfUnbroken : AbstractDungeon
    {
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private DateTime nextHealth;
        private readonly List<GroundHazard> groundHazards = new List<GroundHazard>();
        private static readonly Vector3 BoneArena = new Vector3(120, 0, -420);
        private static readonly Vector3 ArchArena = new Vector3(520, 0, 0);
        private static readonly Vector3 PasArena = new Vector3(520, 0, -420);
        private readonly HashSet<uint> seenHearts = new HashSet<uint>();
        private readonly List<Vector3> heartPair = new List<Vector3>();
        private readonly CapabilityManagerHandle guardMovement = CapabilityManager.CreateNewHandle();
        private bool guardOwned, guardMoving;
        private readonly CapabilityManagerHandle dodgeMovement = CapabilityManager.CreateNewHandle();
        private readonly List<Lane> lanes = new List<Lane>();
        private readonly List<ArchBeam> archBeams = new List<ArchBeam>();
        private DateTime dodgeHoldUntil;
        private bool dodgeOwned, dodgeMoving;
        private bool wasGenericEscape;
        private Vector3? dodgeDestination;
        private DateTime nextPlanWarning;
        private Vector3? fireDestination;
        private bool fireMoving;
        private bool firePlanValid;
        private uint fireObjectId, fireConeKey;
        private DateTime fireSeen, nextFirePlan;
        private System.Numerics.Vector2? firePreferred;
        private bool wardMoving;
        private uint wardTarget;
        private int wardDirection;
        private static bool BurningWardActive() => InBoneArena() && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(a => a.NpcId == 14538 && a.IsAlive && a.HasAura(4175));
        /// <inheritdoc/>
        public override ZoneId ZoneId => (ZoneId)1339;
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToFollowDodge { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToTankBust { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToMitigate { get; } = new HashSet<uint>();
        private static bool InBoneArena() => WorldManager.ZoneId == 1339 && Core.Me.Distance2D(BoneArena) < 55;
        private static bool InArchArena() => WorldManager.ZoneId == 1339 && Core.Me.Distance2D(ArchArena) < 55;
        private static bool InPasArena() => WorldManager.ZoneId == 1339 && Core.Me.Distance2D(PasArena) < 55;
        private static GameObject ChasingFire() => InBoneArena() ? GameObjectManager.GameObjects.FirstOrDefault(a => a.IsVisible && a.BaseId == 0x4B8F) : null;

        /// <inheritdoc/>
        protected override Task<bool> EnterDungeonAsync()
        {
            // User-requested single ownership. The 2026-09-16 trial proved that
            // generic detection fled our own Meteor, displacing both pet and boss.
            // The base class enables SideStep before this callback; turn it off
            // before registering our shapes, and restore the original enabled
            // baseline through the base exit lifecycle when leaving this duty.
            SidestepPlugin.Enabled = false;
            groundHazards.Clear();
            casts.Clear();
            lanes.Clear();
            ReleaseDodge();
            seenHearts.Clear();
            heartPair.Clear();
            AvoidanceHelpers.AddAvoidDonut(InBoneArena, () => BoneArena, 65, 19);
            // Six-second helper 46868, not its boss choreography 46867, owns the
            // donut. Live helper/omen 107 agree with the reference's four-yalm hole.
            AvoidanceHelpers.AddAvoidDonut(InBoneArena,
                () => groundHazards.Where(h => h.Action == 46868 && h.Until > DateTime.UtcNow).Select(h => h.Center).ToArray(),
                40.5, 3.5, AvoidancePriority.High);
            AvoidanceManager.AddAvoidLocation<GroundHazard>(InBoneArena,
                h => h.Radius, h => h.Center,
                () => groundHazards.Where(h => (h.Action != 46922 || !firePlanValid) && h.Action != 46868 && (h.Action != 46908 || NextWebs().Contains(h)) && (h.Action != 46916 && h.Action != 46917 || NextMagma().Contains(h)) && (h.Action < 46902 || h.Action > 46905 || h.Action == 46902 && h == NextUplift() && UpliftCircleReady(h)) && h.Until > DateTime.UtcNow),
                h => true, false);
            // Ancient Aero is an actor-facing 40x 8 line. Use only this enemy
            // action and never broad cast-type matching that includes familiars.
            AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(InBoneArena,
                a => a.IsCasting && a.CastingSpellId == 46870 && a.NpcId == 14532,
                9, 41, yOffset: -0.5f, priority: AvoidancePriority.High);
            // Arch Demon uses a rectangular platform. Reference geometry and
            // the in-game briefing agree on lance lanes, sequential lanes,
            // sword circles and a frontal half-room; each has a 0.5 y margin.
            AvoidanceHelpers.AddAvoidSquareDonut(InArchArena, 39, 28.6f, 100, 100, () => new[] { ArchArena });
            // Dismember is staged explicitly below: two independent captures
            // showed earliest-only avoidance waiting until the occupied lane was
            // due, then losing most of its escape window to service path latency.
            // The 38536 trial reproduced lance/sword oscillation while CR
            // movement was leased off. One local Arch planner now incorporates
            // these shapes with lanes; duplicate generic avoids would compete.
            // Banemite shares the bone arena. Resolve the concentric sequence
            // one ring at a time; later rings must leave the resolved center safe.
            AvoidanceHelpers.AddAvoidDonut(InBoneArena, () => UpliftCenters(46903), 12.5, 5.5, AvoidancePriority.High);
            AvoidanceHelpers.AddAvoidDonut(InBoneArena, () => UpliftCenters(46904), 18.5, 11.5, AvoidancePriority.High);
            AvoidanceHelpers.AddAvoidDonut(InBoneArena, () => UpliftCenters(46905), 24.5, 17.5, AvoidancePriority.High);
            AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(InBoneArena,
                a => a.IsCasting && a.CastingSpellId == 46909, 5, 41, yOffset: -0.5f, priority: AvoidancePriority.High);
            // Ogre's damage is owned by destination helpers, including the 9.3 s
            // teleport version 49688. A boss-origin cone would choose the wrong
            // side of the second magma pool. Widen by 0.5 y and one degree.
            var cone = new List<Vector2> { new Vector2(0, -0.6f) };
            for (int i = 0; i <= 24; i++)
            {
                double angle = (-61 + 122.0 * i / 24) * Math.PI / 180;
                cone.Add(new Vector2((float)Math.Sin(angle) * 40.5f, (float)Math.Cos(angle) * 40.5f));
            }
            AvoidanceManager.AddAvoidPolygon<BattleCharacter>(condition: () => InBoneArena() && !firePlanValid, leashPointProducer: null, leashRadius: 60,
                rotationProducer: a => -a.Heading, scaleProducer: a => 1, heightProducer: a => 15,
                pointsProducer: a => cone.ToArray(), locationProducer: a => a.Location,
                collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsCasting && (a.CastingSpellId == 46912 || a.CastingSpellId == 49688)), priority: AvoidancePriority.High);
            // Native event-object IDs from the encounter reference; visible
            // objects are reacquired every frame, never retained after despawn.
            // The moving fireball is handled together with its overlapping cone
            // below. Generic near-boundary escapes repeatedly stopped in the
            // 19:25 capture, letting it catch up and resolve Arm of Purgatory.
            AvoidanceManager.AddAvoidLocation<GameObject>(InBoneArena, a => 5.5f, a => a.Location,
                // Repeated 20:03 escapes stopped at the central ward edge and
                // immediately retriggered. The phase planner below owns only
                // that central circle; outer magma keeps normal avoidance.
                () => GameObjectManager.GameObjects.Where(a => a.IsVisible && (a.BaseId == 0x1EC025 || a.BaseId == 0x1E9927) && !(BurningWardActive() && a.Distance2D(BoneArena) < 1)), a => true, false);
            // Keep the native fallback available when no verified manual leg
            // exists; suppress it only while a validated kite plan owns movement.
            AvoidanceManager.AddAvoidLocation<GameObject>(() => InBoneArena() && !firePlanValid, a => 11.5f, a => a.Location,
                () => GameObjectManager.GameObjects.Where(a => a.IsVisible && a.BaseId == 0x4B8F), a => true, false);
            RegisterPas();
            return Task.FromResult(false);
        }

        private void RegisterPas()
        {
            // The final arena is rectangular with broken edges, not the round
            // early arena. Preserve corners needed for the 24 y heart blast.
            // Reference floor half-extents 19.7/23.5 receive a 0.5 y wall inset;
            // individual edge cutouts below receive their own 0.5 y margin.
            AvoidanceHelpers.AddAvoidSquareDonut(InPasArena, 38.4f, 46, 100, 100, () => new[] { PasArena });
            AvoidanceManager.AddAvoidPolygon<FloorCutout>(InPasArena, null, 60,
                c => -c.Rotation * (float)Math.PI / 180, c => 1, c => 15,
                c => new[] { new Vector2(-c.X - .5f, -c.Z - .5f), new Vector2(c.X + .5f, -c.Z - .5f), new Vector2(c.X + .5f, c.Z + .5f), new Vector2(-c.X - .5f, c.Z + .5f) },
                c => c.Center, () => PasCutouts, priority: AvoidancePriority.High);
            // Only the damage helpers own Blood Rain; parent 46923/46925 are
            // choreography. The safe donut pocket is reduced from 8 to 7.5 y.
            AvoidanceHelpers.AddAvoidDonut(InPasArena,
                () => groundHazards.Where(h => h.Action == 46924 && h.Until > DateTime.UtcNow).Select(h => h.Center).ToArray(), 40.5, 7.5, AvoidancePriority.High);
            AvoidanceManager.AddAvoidLocation<GroundHazard>(InPasArena, h => h.Radius, h => h.Center,
                () => groundHazards.Where(h => h.Action != 46924 && h.Until > DateTime.UtcNow &&
                    (h.Action == 46926 || h.Action == 46929 || h.Action == 46938 && h.Until - DateTime.UtcNow < TimeSpan.FromSeconds(7))), h => true, false);
            AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(InPasArena,
                a => a.IsCasting && a.CastingSpellId == 46932, 9, 61, yOffset: -.5f, priority: AvoidancePriority.High);
            RegisterPasCone(46933, 60.5f, 11); // Aero helper:20-degree full cone, one-degree angular margin.
            RegisterPasCone(46930, 10.5f, 61); // Knight Sweet Steel:120-degree full cone.
        }

        private static void RegisterPasCone(uint spell, float radius, float halfAngle)
        {
            var points = new List<Vector2> { new Vector2(0, -.5f) };
            for (int i = 0; i <= 24; i++)
            {
                double angle = (-halfAngle + 2 * halfAngle * i / 24) * Math.PI / 180;
                points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
            }
            AvoidanceManager.AddAvoidPolygon<BattleCharacter>(InPasArena, null, 65, a => -a.Heading, a => 1, a => 15,
                a => points.ToArray(), a => a.Location,
                () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsCasting && a.CastingSpellId == spell), priority: AvoidancePriority.High);
        }

        // Floor exclusions from the pinned 2026-09 encounter reference. They are
        // geometric constraints, not memory offsets; retain each notch rather
        // than clipping the whole platform to a smaller rectangle.
        private static readonly FloorCutout[] PasCutouts =
        {
            new FloorCutout(502.88f,-398.45f,3.41f,5.42f,-7.38f), new FloorCutout(510.94f,-396.70f,1.30f,.50f),
            new FloorCutout(515.42f,-396.61f,.75f,.50f), new FloorCutout(524.58f,-396.61f,.75f,.50f),
            new FloorCutout(528.84f,-397.06f,1.57f,1.01f), new FloorCutout(539.61f,-408,.77f,1.20f),
            new FloorCutout(537.57f,-412.33f,2.50f,3.20f), new FloorCutout(541.41f,-420,2.50f,1.20f),
            new FloorCutout(538.77f,-426.12f,2.41f,3.97f), new FloorCutout(539.57f,-431.99f,.75f,1.20f),
            new FloorCutout(539.31f,-443.61f,.47f,.79f), new FloorCutout(505.35f,-443.55f,1.52f,.79f),
            new FloorCutout(501.05f,-440.73f,3.75f,5,5.79f), new FloorCutout(500.56f,-432.03f,.60f,1.25f),
            new FloorCutout(500.56f,-420,.60f,1.25f), new FloorCutout(500.60f,-408.02f,.60f,1.25f),
            new FloorCutout(539.50f,-396.55f,.70f,.60f), new FloorCutout(502.11f,-424.06f,2.50f,2.90f),
            new FloorCutout(501.36f,-415.86f,2.30f,1.80f,-5.80f), new FloorCutout(500.13f,-413.02f,1.10f,1.30f)
        };
        private sealed class FloorCutout
        {
            internal readonly Vector3 Center;
            internal readonly float X, Z, Rotation;
            internal FloorCutout(float x, float z, float halfX, float halfZ, float degrees = 0)
            {
                Center = new Vector3(x, 0, z);
                X = halfX;
                Z = halfZ;
                Rotation = degrees;
            }
        }

        private GroundHazard NextUplift() => groundHazards.Where(h => h.Action >= 46902 && h.Action <= 46905 && h.Until > DateTime.UtcNow)
            .OrderBy(h => h.Until).FirstOrDefault();

        private bool UpliftCircleReady(GroundHazard circle)
        {
            // Webs finish clockwise while the next Uplift is already casting.
            // Reserving its center immediately trapped Kember in the last webs
            // at06:26:17 September24. Keep their resolved center available until
            // 2.5s before Uplift's cast snapshot (Until includes1.1s effect grace).
            // That leaves time to clear6.5y; outside this overlap retain the
            // original full-cast warning and all subsequent ring timing.
            return !groundHazards.Any(h => h.Action == 46908 && h.Until > DateTime.UtcNow) ||
                circle.Until - DateTime.UtcNow <= TimeSpan.FromSeconds(3.6);
        }
        private Vector3[] UpliftCenters(uint action)
        {
            var h = NextUplift();
            return h != null && h.Action == action ? new[] { h.Center } : Array.Empty<Vector3>();
        }

        // Center then rotating outer circles: registering all nine at once
        // incorrectly covered the complete arena. The earliest six leave the
        // late side available until the center resolves, matching the capture
        // and the independent encounter sequence reference.
        private IEnumerable<GroundHazard> NextWebs() => groundHazards.Where(h => h.Action == 46908 && h.Until > DateTime.UtcNow).OrderBy(h => h.Until).Take(6);

        // RB 66844 at 21:01:44 fled a later bait into the still-unresolved first
        // Magma wave, transferring the hit/vulnerability to Cu Sith. Resolve
        // the earliest simultaneous batch first (helpers publish within 200 ms),
        // then hand off after its cast snapshot; later waves are about 1 s apart.
        // 77088 proved waiting for the later damage message leaves too little
        // time to escape the next batch's already-completed snapshot.
        private IEnumerable<GroundHazard> NextMagma()
        {
            var pending = groundHazards.Where(h => (h.Action == 46916 || h.Action == 46917) && h.Until > DateTime.UtcNow).ToArray();
            if (pending.Length == 0)
                return pending;
            var earliest = pending.Min(h => h.Until);
            return pending.Where(h => h.Until <= earliest.AddMilliseconds(200));
        }

        private static uint NextDismember() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsCasting && a.CastingSpellId == 46879)
            .OrderBy(a => a.SpellCastInfo.RemainingCastTime).ThenBy(a => a.ObjectId).FirstOrDefault()?.ObjectId ?? 0;

        /// <inheritdoc/>
        protected override Task<bool> ExitDungeonAsync()
        {
            ReleaseDodge();
            lanes.Clear();
            archBeams.Clear();
            ReleaseGuard();
            casts.Clear();
            groundHazards.Clear();
            return Task.FromResult(false);
        }
        /// <inheritdoc/>
        public override Task<bool> RunAsync()
        {
            if (WorldManager.ZoneId != 1339)
                return Task.FromResult(false);
            if (SidestepPlugin.Enabled)
                SidestepPlugin.Enabled = false;
            if (Core.Me.Location.X < -400)
            {
                ReleaseDodge();
                lanes.Clear();
                archBeams.Clear();
                ReleaseGuard();
                casts.Clear();
                groundHazards.Clear();
                seenHearts.Clear();
                heartPair.Clear();
                return Task.FromResult(false);
            }
            groundHazards.RemoveAll(h => h.Until <= DateTime.UtcNow);
            // Include friendly/untargetable helpers: the first arena gives them
            // the Bishop name ID, which must not merge them into the boss cast.
            foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false))
            {
                if (InPasArena() && actor.BaseId == 0x4B93 && actor.IsVisible && seenHearts.Add(actor.ObjectId))
                {
                    heartPair.Add(actor.Location);
                    if (heartPair.Count == 2)
                    {
                        // Hearts converge on their spawn midpoint. The reference
                        // predicts 13.5 s to impact; begin escaping 7 s before it,
                        // preserving earlier mechanics instead of blocking the
                        // center for the whole spawn animation. Live verification
                        // must establish this timing and any overlap refinement.
                        var midpoint = (heartPair[0] + heartPair[1]) / 2;
                        groundHazards.Add(new GroundHazard { Action = 46938, Center = midpoint, Radius = 24.5f, Until = DateTime.UtcNow.AddSeconds(14) });
                        ff14bot.Helpers.Logging.Write("[CrucibleHeart] predicted center={0}", midpoint);
                        heartPair.Clear();
                    }
                }
                uint spell = actor.IsCasting ? actor.CastingSpellId : 0;
                // Lance headings visibly settle during the first~0.9 s of the
                // cast.66844 used a stale initial heading and took Damage Down
                // at 20:57:46.377. Refresh scalar heading until cast completion,
                // then retain that final geometry while the delayed hit resolves.
                if (spell == 46876 && actor.SpellCastInfo.RemainingCastTime > TimeSpan.Zero)
                {
                    var beam = archBeams.LastOrDefault(b => b.Caster == actor.ObjectId);
                    if (beam != null)
                        beam.Heading = actor.Heading;
                }
                if (casts.TryGetValue(actor.ObjectId, out uint previous) && previous == spell)
                    continue;
                casts[actor.ObjectId] = spell;
                if (spell == 0)
                    continue;
                var cast = actor.SpellCastInfo;
                if (InArchArena() && spell == 46879)
                    lanes.Add(new Lane { X = actor.Location.X, Finish = DateTime.UtcNow + cast.RemainingCastTime });
                if (InArchArena() && (spell == 46876 || spell == 46886))
                    archBeams.Add(new ArchBeam
                    {
                        Caster = actor.ObjectId,
                        Center = actor.Location,
                        Heading = actor.Heading,
                        HalfWidth = spell == 46876 ? 2.5f : 40.5f,
                        // Captured lance damage landed~1.1 s after cast end.
                        Until = DateTime.UtcNow + cast.RemainingCastTime + TimeSpan.FromMilliseconds(spell == 46876 ? 1300 : 350)
                    });
                if (InPasArena() && (spell == 46924 || spell == 46926 || spell == 46929 || spell == 46938))
                {
                    // Actual Heart Shatter replaces prediction with its helper's
                    // location; do not leave a stale second circle blocking escape.
                    if (spell == 46938)
                        groundHazards.RemoveAll(h => h.Action == 46938);
                    groundHazards.Add(new GroundHazard
                    {
                        Action = spell,
                        Center = spell == 46929 ? cast.CastLocation : actor.Location,
                        Radius = spell == 46926 ? 8.5f : spell == 46929 ? 10.5f : 24.5f,
                        Until = DateTime.UtcNow + cast.RemainingCastTime + TimeSpan.FromMilliseconds(500)
                    });
                }
                if (InBoneArena() && (spell == 46916 || spell == 46917 || spell == 46922))
                    groundHazards.Add(new GroundHazard
                    {
                        Action = spell,
                        Center = spell == 46922 ? actor.Location : cast.CastLocation,
                        Radius = spell == 46916 ? 3.5f : spell == 46917 ? 5.5f : 10.5f,
                        // 77088 was inside the second bait at its 21:17:36.238
                        // cast end but 3.5 y clear by damage 36.952: the snapshot
                        // precedes the message. Handoff 100 ms after cast end
                        // leaves~0.9 s to escape the next 1 s-spaced bait.
                        Until = DateTime.UtcNow + cast.RemainingCastTime + TimeSpan.FromMilliseconds(spell == 46922 ? 350 : 100)
                    });
                if (InBoneArena() && (spell >= 46902 && spell <= 46905 || spell == 46908))
                    groundHazards.Add(new GroundHazard
                    {
                        Action = spell,
                        Center = spell == 46908 ? cast.CastLocation : actor.Location,
                        // Uplift damage follows the cast by about one second;
                        // the previous 350 ms expiry opened the center too early.
                        Radius = spell == 46908 ? 9.5f : 6.5f,
                        Until = DateTime.UtcNow + cast.RemainingCastTime + TimeSpan.FromMilliseconds(spell == 46908 ? 350 : 1100)
                    });
                if (InArchArena() && (spell == 46882 || spell == 46884))
                    groundHazards.Add(new GroundHazard
                    {
                        Action = spell,
                        Center = actor.Location,
                        Radius = spell == 46882 ? 6.5f : 3.5f,
                        Until = DateTime.UtcNow + cast.RemainingCastTime + TimeSpan.FromMilliseconds(350)
                    });
                if (InBoneArena() && (actor.NpcId == 14531 || actor.NpcId == 14532))
                {
                    // Snapshot ground coordinates: reacquiring a moving target
                    // would drag an eruption after the player instead of baiting it.
                    if (spell == 46868 || spell == 46866 || spell == 46874 || spell == 46900)
                        groundHazards.Add(new GroundHazard
                        {
                            Action = spell,
                            Center = spell == 46874 || spell == 46900 ? cast.CastLocation : actor.Location,
                            Radius = spell == 46866 ? 6.5f : 5.5f,
                            Until = DateTime.UtcNow + cast.RemainingCastTime + TimeSpan.FromMilliseconds(350)
                        });
                }
                ff14bot.Helpers.Logging.Write("[CrucibleCast] oid={0:X} npc={1} name={2} action={3} cast={4} pos={5} heading={6:F3} ground={7} target={8:X} remaining={9:F3} player={10}",
                    actor.ObjectId, actor.NpcId, actor.Name, spell, cast.Name, actor.Location, actor.Heading,
                    cast.CastLocation, cast.TargetId, cast.RemainingCastTime.TotalSeconds, Core.Me.Location);
            }
            if (Core.Me.InCombat && DateTime.UtcNow >= nextHealth)
            {
                nextHealth = DateTime.UtcNow.AddSeconds(2);
                // Cu Sith's Cover hid failed Wisp explosions from player-only
                // diagnostics on September 16. Include the familiar's observed
                // Vulnerability Up status (1789) so survival cannot imply clean play.
                // Lance hits instead inflict Damage Down, seen in 66844's battle
                // log; resolve its English data name rather than guessing its ID.
                ff14bot.Helpers.Logging.Write("[CrucibleHealth] player={0:F1} pos={1} pet={2} petHP={3:F1} target={4} targetHP={5:F1} petVulnerability={6} playerDamageDown={7} petDamageDown={8}", Core.Me.CurrentHealthPercent, Core.Me.Location, Core.Me.Pet?.Name, Core.Me.Pet?.CurrentHealthPercent, Core.Me.CurrentTarget?.Name, (Core.Me.CurrentTarget as BattleCharacter)?.CurrentHealthPercent, Core.Me.Pet?.HasAura(1789) == true, Core.Me.CharacterAuras.Any(a => a.Name == "Damage Down"), Core.Me.Pet?.CharacterAuras.Any(a => a.Name == "Damage Down") == true);
            }
            // Registration/capture does not consume TreeStart: the avoidance
            // manager moves the player while available routine actions continue.
            if (HandleDodge())
                return Task.FromResult(false);
            return Task.FromResult(HandleGuard());
        }

        private bool HandleDodge()
        {
            var now = DateTime.UtcNow;
            if (HandleWard())
                return true;
            if (HandleChasingFire())
                return true;
            archBeams.RemoveAll(b => b.Until <= now);
            if (lanes.Count > 0 && lanes.Max(l => l.Finish).AddMilliseconds(500) < now)
                lanes.Clear();
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                wasGenericEscape = true;
                // RB released an escape at the shape boundary in repeated trials.
                // Hold ordinary approach until the recorded cast window ends so
                // it cannot pull the player back into the same telegraph.
                dodgeHoldUntil = now.AddMilliseconds(700);
                dodgeMoving = false;
                HoldDodge();
                return true;
            }
            if (wasGenericEscape)
            {
                // The 200 ms trace proved generic avoidance leaves movement bits
                // active after "Done running". With approach suppressed, that
                // residual input walked to the arena wall. Take ownership once
                // at the completed escape edge, then hold the reached safe spot.
                MovementManager.MoveStop();
                wasGenericEscape = false;
            }
            if (InArchArena() && lanes.Count > 0)
            {
                ReleaseGuard();
                var first = lanes.OrderBy(l => l.Finish).First();
                float direction = first.X < ArchArena.X ? 1 : -1;
                // Stage in the final lane adjacent to the fourth. Live helper
                // spacings are 8 y; the fourth-to-fifth gap is over 1 s, unlike the
                // first pair's 0.47 s. Cross 1.9 y into the resolved fourth lane,
                // retaining 0.7 y/1.2 y clearance from the actual 8 y-wide stripes.
                var fourth = lanes.FirstOrDefault(l => Math.Abs(l.X - (first.X + direction * 24)) < 1);
                float x = first.X + direction * (fourth != null && now > fourth.Finish.AddMilliseconds(250) ? 26.8f : 28.7f);
                var candidate = Enumerable.Range(0, 25).Select(i => new Vector3(x, 0, -12 + i))
                    .Where(p => SafeArch(p, .25f) && ReachableArch(p))
                    // Only the authored next lane is eligible; favor melee along it.
                    .OrderBy(CrucibleMeleePreference.CaptureWorld()).ThenBy(p => dodgeDestination.HasValue ? dodgeDestination.Value.Distance2D(p) : Core.Me.Distance2D(p)).FirstOrDefault();
                HoldDodge();
                if (candidate == default(Vector3))
                {
                    // Concurrent swords/lances may temporarily remove this edge.
                    // Preserve generic emergency avoidance; do not force a point
                    // through another registered hazard or through the arena wall.
                    if (dodgeMoving)
                    {
                        MovementManager.MoveStop();
                        dodgeMoving = false;
                    }
                    WarnNoArchPoint();
                    return true;
                }
                MoveDodge(candidate);
                return true;
            }
            if (InArchArena() && (archBeams.Count > 0 || groundHazards.Any(h => h.Action == 46882 || h.Action == 46884)))
            {
                HoldDodge();
                // Keep a still-safe destination through overlapping cast starts.
                // Do not reselect the nearest voxel on every tick, which caused
                // repeated short run-outs in the live generic-avoidance trace.
                if (dodgeDestination.HasValue && SafeArch(dodgeDestination.Value, .25f) && ReachableArch(dodgeDestination.Value))
                    MoveDodge(dodgeDestination.Value);
                else if (SafeArch(Core.Me.Location, .75f))
                {
                    if (dodgeMoving)
                    {
                        MovementManager.MoveStop();
                        dodgeMoving = false;
                    }
                    dodgeDestination = Core.Me.Location;
                }
                else
                {
                    var meleePreference = CrucibleMeleePreference.CaptureWorld();
                    var choices = from ix in Enumerable.Range(0, 39)
                                  from iz in Enumerable.Range(0, 27)
                                  let p = new Vector3(501 + ix, 0, -13 + iz)
                                  where SafeArch(p, .75f) && ReachableArch(p)
                                  orderby meleePreference(p), Core.Me.Distance2D(p)
                                  select p;
                    var point = choices.FirstOrDefault();
                    if (point != default(Vector3))
                        MoveDodge(point);
                    else
                    {
                        if (dodgeMoving)
                        {
                            MovementManager.MoveStop();
                            dodgeMoving = false;
                        }
                        WarnNoArchPoint();
                    }
                }
                return true;
            }
            if (dodgeOwned && (now < dodgeHoldUntil || groundHazards.Any(h => h.Until > now) ||
                GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsCasting && IsDodgeCast(a.CastingSpellId))))
            {
                // Improve a completed native dodge without crossing its still-
                // active hazards. Ward/fire and authored Arch sequences already
                // returned above and retain their required movement lifecycle.
                var melee = CrucibleMeleePreference.ReachableMelee(dodgeDestination, p =>
                    InPasArena() ? Math.Abs(p.X - PasArena.X) < 18 && Math.Abs(p.Z - PasArena.Z) < 22 :
                    InBoneArena() && p.Distance2D(BoneArena) < 18);
                if (melee.HasValue) MoveDodge(melee.Value);
                HoldDodge();
                return true;
            }
            ReleaseDodge();
            return false;
        }

        private bool HandleWard()
        {
            if (!BurningWardActive())
            {
                if (wardMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                    MovementManager.MoveStop();
                wardMoving = false;
                wardTarget = 0;
                return false;
            }
            // Two live captures reproduced sub-yalm oscillation at the ward
            // edge despite routine detours. Keep one continuous movement owner
            // for this lifecycle, suppressing ordinary pursuit while rotation
            // and pet commands remain schedulable. Never enter the central 6.5 y
            // clearance; an 8 y orbit leaves room for the short chords below.
            HoldDodge();
            if (AvoidanceManager.IsRunningOutOfAvoid)
                return true;
            // The dedicated routine publishes a horn order before the client
            // exposes IsCasting. Read its optional contract without a compile
            // dependency on the routine assembly; other routines retain normal
            // behavior. This prevents ward pursuit cancelling the summon.
            var routine = RoutineManager.Current;
            var hold = routine?.GetType().GetProperty("SummonMovementHoldUntil")?.GetValue(routine);
            if (hold is DateTime until && DateTime.UtcNow < until)
            {
                if (wardMoving)
                    MovementManager.MoveStop();
                wardMoving = false;
                return true;
            }
            var start = Core.Me.Location;
            var target = Core.Me.CurrentTarget as BattleCharacter;
            bool wisp = target != null && target.IsAlive && (target.BaseId == 0x4B8E || target.BaseId == 0x4DD4);
            var relative = start - BoneArena;
            float radius = start.Distance2D(BoneArena);
            double angle = Math.Atan2(relative.Z, relative.X);
            var destination = start;
            if (radius < 7.5f)
                destination = BoneArena + new Vector3((float)Math.Cos(angle) * 8, 0, (float)Math.Sin(angle) * 8);
            // The 60552 trace followed a slow approaching Wisp with repeated
            // sub-yalm starts/stops while already able to attack. Hold a valid
            // melee position instead of chasing its changing preferred offset;
            // resume pursuit only outside the routine's existing reach+2 range.
            else if (wisp && target.Distance2D(start) > Math.Max(2, target.CombatReach + 2))
            {
                var enemy = target.Location - BoneArena;
                double goalAngle = Math.Atan2(enemy.Z, enemy.X);
                float goalRadius = Math.Max(8, Math.Min(18, target.Distance2D(BoneArena) - Math.Max(2, target.CombatReach + 1.7f)));
                var goal = BoneArena + new Vector3((float)Math.Cos(goalAngle) * goalRadius, 0, (float)Math.Sin(goalAngle) * goalRadius);
                var leg = goal - start;
                float length2 = leg.X * leg.X + leg.Z * leg.Z;
                float fraction = length2 > 0 ? Math.Max(0, Math.Min(1, -(relative.X * leg.X + relative.Z * leg.Z) / length2)) : 0;
                var nearest = relative + leg * fraction;
                double delta = Math.Atan2(Math.Sin(goalAngle - angle), Math.Cos(goalAngle - angle));
                if (nearest.X * nearest.X + nearest.Z * nearest.Z < 7 * 7 && Math.Abs(delta) > .2)
                {
                    if (wardTarget != target.ObjectId)
                    {
                        wardTarget = target.ObjectId;
                        wardDirection = delta < 0 ? -1 : 1;
                        ff14bot.Helpers.Logging.Write("[CrucibleMove] Ward orbit target={0:X} direction={1}", wardTarget, wardDirection);
                    }
                    double next = angle + wardDirection * Math.Min(.4, Math.Abs(delta));
                    destination = BoneArena + new Vector3((float)Math.Cos(next) * 8, 0, (float)Math.Sin(next) * 8);
                }
                else
                    destination = goal;
            }
            if (start.Distance2D(destination) > .4f && !Core.Me.IsCasting)
            {
                Navigator.PlayerMover.MoveTowards(destination);
                wardMoving = true;
            }
            else if (wardMoving)
            {
                MovementManager.MoveStop();
                wardMoving = false;
            }
            return true;
        }

        private bool HandleChasingFire()
        {
            var now = DateTime.UtcNow;
            var fire = ChasingFire();
            var burst = groundHazards.FirstOrDefault(h => h.Action == 46922 && h.Until > now);
            if (fire == null && burst == null)
            {
                if (fireMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                    MovementManager.MoveStop();
                fireMoving = firePlanValid = false;
                fireObjectId = 0;
                fireDestination = null;
                firePreferred = null;
                return false;
            }
            if (fire != null && fireObjectId != fire.ObjectId)
            {
                fireObjectId = fire.ObjectId;
                fireSeen = now;
                nextFirePlan = default;
            }
            var danger = fire?.Location ?? burst.Center;
            var start = Core.Me.Location;
            var cones = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(a => a.IsCasting && (a.CastingSpellId == 46912 || a.CastingSpellId == 49688)).ToArray();
            uint coneKey = cones.Aggregate(0u, (key, a) => key ^ a.ObjectId);
            if (now >= nextFirePlan || coneKey != fireConeKey || !fireDestination.HasValue || start.Distance2D(fireDestination.Value) < .5f)
            {
                fireConeKey = coneKey;
                nextFirePlan = now.AddMilliseconds(450);
                var pools = GameObjectManager.GameObjects.Where(a => a.IsVisible && (a.BaseId == 0x1EC025 || a.BaseId == 0x1E9927))
                    .Select(a => new System.Numerics.Vector2(a.Location.X, a.Location.Z)).ToArray();
                var snapshots = cones.Select(a => new FireKitePlanner.Cone
                {
                    Center = new System.Numerics.Vector2(a.Location.X, a.Location.Z),
                    Heading = a.Heading,
                    Remaining = a.SpellCastInfo.RemainingCastTime.TotalSeconds
                }).ToArray();
                // RB 77088: first visible fire 21:18:52.817, movement~57.5,
                // explosion snapshot 21:19:05.685. Use an early 12.6 s estimate
                // until the actual 0.7 s cast supplies a deadline. Receding-horizon
                // planning corrects pursuit from fresh positions every 450 ms.
                double age = (now - fireSeen).TotalSeconds;
                double blastIn = burst != null ? (burst.Until - now).TotalSeconds - .35 : 12.6 - age;
                double sprint = Core.Me.CharacterAuras.Where(a => a.Name == "Sprint").Select(a => a.TimespanLeft.TotalSeconds).DefaultIfEmpty(0).Max();
                var route = FireKitePlanner.Plan(new System.Numerics.Vector2(start.X, start.Z),
                    new System.Numerics.Vector2(danger.X, danger.Z), new System.Numerics.Vector2(BoneArena.X, BoneArena.Z),
                    pools, snapshots, Math.Max(0, 4.5 - age), blastIn, sprint, firePreferred,
                    meleePreference: CrucibleMeleePreference.Capture());
                if (route.Length == 0)
                {
                    // No full-horizon solution is evidence of an unsafe model or
                    // state. Keep native emergency handling available, and log
                    // enough timing to reproduce it instead of inventing a leg.
                    fireDestination = null;
                    firePreferred = null;
                    firePlanValid = false;
                    if (now >= nextPlanWarning)
                    {
                        nextPlanWarning = now.AddSeconds(2);
                        ff14bot.Helpers.Logging.Write("[CrucibleMove] No complete fire route player={0} fire={1} age={2:F2} blastIn={3:F2} sprint={4:F2} cones={5}", start, danger, age, blastIn, sprint, cones.Length);
                    }
                    if (fireMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                        MovementManager.MoveStop();
                    fireMoving = false;
                    return false;
                }
                fireDestination = new Vector3(route[0].X, start.Y, route[0].Y);
                firePreferred = route.Length > 1 ? route[1] : route[0];
                ff14bot.Helpers.Logging.Write("[CrucibleMove] Timed fire destination={0} fire={1} player={2} blastIn={3:F2} sprint={4:F2}", fireDestination, danger, start, blastIn, sprint);
            }
            firePlanValid = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
                return false;
            HoldDodge();
            if (start.Distance2D(fireDestination.Value) > .3f)
            {
                Navigator.PlayerMover.MoveTowards(fireDestination.Value);
                fireMoving = true;
            }
            else if (fireMoving)
            {
                MovementManager.MoveStop();
                fireMoving = false;
            }
            return true;
        }
        private static bool InsideOgreCone(Vector3 p, Vector3 origin, float heading)
        {
            float dx = p.X - origin.X, dz = p.Z - origin.Z;
            float distance = (float)Math.Sqrt(dx * dx + dz * dz);
            return distance <= 40.5f && (distance < .6f ||
                (dx * (float)Math.Sin(heading) + dz * (float)Math.Cos(heading)) / distance >= Math.Cos(61 * Math.PI / 180));
        }

        private static bool IsDodgeCast(uint action) => action == 46870 || action == 46876 || action == 46886 || action == 46909 || action == 46912 || action == 49688 || action == 46930 || action == 46932 || action == 46933;

        private bool SafeArch(Vector3 point, float clearance)
        {
            if (Math.Abs(point.X - 520) > 19.5f - clearance || Math.Abs(point.Z) > 14.3f - clearance)
                return false;
            foreach (var h in groundHazards.Where(h => h.Action == 46882 || h.Action == 46884))
                if (point.Distance2D(h.Center) < h.Radius + clearance)
                    return false;
            foreach (var beam in archBeams)
            {
                float dx = point.X - beam.Center.X, dz = point.Z - beam.Center.Z;
                float forward = dx * (float)Math.Sin(beam.Heading) + dz * (float)Math.Cos(beam.Heading);
                float side = dx * (float)Math.Cos(beam.Heading) - dz * (float)Math.Sin(beam.Heading);
                if (forward >= -.5f - clearance && forward <= 40.5f + clearance && Math.Abs(side) <= beam.HalfWidth + clearance)
                    return false;
            }
            return true;
        }

        private bool ReachableArch(Vector3 destination)
        {
            // Arelia 51080 at 23:41:35.967 escaped a later Transfixion from
            // (531,-0.93) toward(530,8), crossing a still-resolving lance and
            // receiving Damage Down at 37.643. Endpoint safety alone is not a
            // valid path. Each active shape permits an initial escape if already
            // inside, but no subsequent entry/reentry along the direct segment.
            // Half-yalm samples are smaller than the narrowest 5 y beam and use
            // its existing 0.5 y geometry margin plus 0.25 y sampling clearance.
            var start = new System.Numerics.Vector2(Core.Me.Location.X, Core.Me.Location.Z);
            var end = new System.Numerics.Vector2(destination.X, destination.Z);
            foreach (var beam in archBeams)
                if (!ArchPathSafety.Beam(start, end, new System.Numerics.Vector2(beam.Center.X, beam.Center.Z), beam.Heading, beam.HalfWidth))
                    return false;
            foreach (var hazard in groundHazards.Where(h => h.Action == 46882 || h.Action == 46884))
                if (!ArchPathSafety.Circle(start, end, new System.Numerics.Vector2(hazard.Center.X, hazard.Center.Z), hazard.Radius))
                    return false;
            return true;
        }
        private void HoldDodge()
        {
            if (!dodgeOwned)
            {
                // Manual ownership must cancel a residual routine approach once,
                // even when the current point is already safe. Never cancel an
                // active generic emergency escape while acquiring a later hold.
                if (!AvoidanceManager.IsRunningOutOfAvoid)
                    MovementManager.MoveStop();
                ff14bot.Helpers.Logging.Write("[CrucibleMove] dodge owns movement/facing");
            }
            dodgeOwned = true;
            // Update validates one capability at a time (unlike Clear's mask).
            // Combined Movement|Facing compiled but threw in the first live dodge.
            CapabilityManager.Update(dodgeMovement, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Crucible: stable dodge hold");
            CapabilityManager.Update(dodgeMovement, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(500), "Crucible: stable dodge hold");
        }

        private void WarnNoArchPoint()
        {
            if (DateTime.UtcNow < nextPlanWarning)
                return;
            nextPlanWarning = DateTime.UtcNow.AddSeconds(3);
            ff14bot.Helpers.Logging.Write("[CrucibleMove] No shared Arch destination: lanes={0} beams={1} circles={2} player={3}", lanes.Count, archBeams.Count,
                groundHazards.Count(h => h.Action == 46882 || h.Action == 46884), Core.Me.Location);
        }

        private void MoveDodge(Vector3 point)
        {
            if (!dodgeDestination.HasValue || dodgeDestination.Value.Distance2D(point) > 1)
                ff14bot.Helpers.Logging.Write("[CrucibleMove] Arch destination={0} player={1}", point, Core.Me.Location);
            dodgeDestination = point;
            // Flat, measured Arch platform: direct movement avoids the observed
            // 438 ms remote path request. No navigation provider is replaced.
            if (Core.Me.Distance2D(point) > .25f)
            {
                Navigator.PlayerMover.MoveTowards(point);
                dodgeMoving = true;
            }
            else if (dodgeMoving)
            {
                MovementManager.MoveStop();
                dodgeMoving = false;
            }
        }

        private void ReleaseDodge()
        {
            if ((dodgeMoving || fireMoving || wardMoving) && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            wardMoving = false;
            wardTarget = 0;
            fireMoving = false;
            firePlanValid = false;
            fireObjectId = 0;
            firePreferred = null;
            fireDestination = null;
            dodgeMoving = false;
            dodgeDestination = null;
            wasGenericEscape = false;
            if (!dodgeOwned)
                return;
            CapabilityManager.Clear(dodgeMovement, CapabilityFlags.Movement | CapabilityFlags.Facing, "Crucible: dodge window ended");
            dodgeOwned = false;
            ff14bot.Helpers.Logging.Write("[CrucibleMove] dodge released");
        }

        private sealed class Lane
        {
            internal float X;
            internal DateTime Finish;
        }

        // Scalar geometry snapshots, not retained native actor wrappers. A beam
        // uses the same verified heading convention and 0.5 y margin as its former
        // polygon registration; expiry includes the observed resolution grace.
        private sealed class ArchBeam
        {
            internal uint Caster;
            internal Vector3 Center;
            internal float Heading, HalfWidth;
            internal DateTime Until;
        }

        private bool HandleGuard()
        {
            var knight = Core.Me.CurrentTarget as BattleCharacter;
            if (!InBoneArena() || knight == null || knight.NpcId != 14531 || !knight.IsAlive || !knight.HasAura(680))
            {
                ReleaseGuard();
                return false;
            }
            // Live 2026-09-16: Forward Guard grants Directional Parry 680 while
            // Rehabilitation 1263 restores HP. Front attacks stalled at 38→53%.
            // This is positive rear positioning, not a damaging-region avoid.
            // Let Snarl establish the familiar as tank before chasing its rear.
            if (knight.CurrentTargetId == Core.Me.ObjectId || AvoidanceManager.IsRunningOutOfAvoid)
                return false;
            var rear = knight.Location - new Vector3((float)Math.Sin(knight.Heading), 0, (float)Math.Cos(knight.Heading)) * 2.7f;
            if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(rear)))
                return false;
            if (!guardOwned)
                ff14bot.Helpers.Logging.Write("[CrucibleGuard] holding rear at {0}", rear);
            guardOwned = true;
            CapabilityManager.Update(guardMovement, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Crucible: Knight directional parry rear");
            if (Core.Me.Distance2D(rear) > .5f)
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(rear, "Crucible: rear of Forward Guard") { DistanceTolerance = .3f });
                guardMoving = true;
                return true;
            }
            if (guardMoving)
            {
                MovementManager.MoveStop();
                guardMoving = false;
            }
            return false; // Hold only CR movement; attacks continue at the rear.
        }

        private void ReleaseGuard()
        {
            if (guardMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            guardMoving = false;
            if (!guardOwned)
                return;
            CapabilityManager.Clear(guardMovement, CapabilityFlags.Movement, "Crucible: Forward Guard ended");
            guardOwned = false;
            ff14bot.Helpers.Logging.Write("[CrucibleGuard] released rear positioning");
        }

        // Scalar snapshots survive actor refresh without retaining native wrappers.
        private sealed class GroundHazard
        {
            internal uint Action;
            internal Vector3 Center;
            internal float Radius;
            internal DateTime Until;
        }
    }
}
