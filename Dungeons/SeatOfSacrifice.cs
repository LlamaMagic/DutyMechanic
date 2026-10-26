using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Normal Seat of Sacrifice geometry and semantic positioning. The plugin pulse samples native
/// state; one movement lease arbitrates stack, bait, knockback and move/stay requirements.
/// Normal routine actions remain available at a safe position except during the fire/QTE hold.
/// </summary>
public static partial class SeatOfSacrifice
{
    // Native avoids track collection objects with object.Equals. Refresh an active
    // observation in place: replacing it for deadline jitter rebuilds the escape path
    // every frame. Derived bait records retain value equality for unchanged geometry.
    private sealed record Cast(uint Action, uint Target, Vector3 Origin, float Heading, DateTime Finish)
    {
        public uint Target { get; set; } = Target;
        public Vector3 Origin { get; set; } = Origin;
        public float Heading { get; set; } = Heading;
        public DateTime Finish { get; set; } = Finish;

        internal void Refresh(uint target, Vector3 origin, float heading, DateTime finish)
        {
            Target = target;
            Origin = origin;
            Heading = heading;
            Finish = finish;
        }
    }

    private sealed record Marker(uint Id, DateTime Until);
    // Brimstone keeps one native avoid through its growth stages, including updates
    // to the observed cast endpoint before impact; it is not a new hazard each pulse.
    private sealed record Ground(Vector3 Origin, DateTime Impact)
    {
        public Vector3 Origin { get; set; } = Origin;
        public DateTime Impact { get; set; } = Impact;
    }

    private static readonly Vector3 Center = new(100, 0, 100);
    private static readonly Dictionary<uint, Cast> Casts = new();
    // Helpers can move to their next cast before the meteor hit. Retain the first
    // observed targets through that effect instead of deriving them from current casts.
    private static readonly Dictionary<uint, DateTime> MeteorTargets = new();
    private static readonly Dictionary<(uint, uint), Marker> Markers = new();
    private static readonly Dictionary<uint, Ground> Brimstone = new();
    private static readonly Dictionary<uint, DateTime> Towers = new();
    private static readonly Dictionary<uint, ushort> MapStates = new();
    private static readonly Dictionary<uint, Cast> Swords = new();
    private static readonly List<AvoidInfo> Avoids = new();
    private static readonly HashSet<uint> Overrides = new();
    private static readonly CapabilityManagerHandle Movement = CapabilityManager.CreateNewHandle();
    private static DateTime nextAttempt, fireFinish, iceFinish;
    private static DateTime swordWindow, towerWindow;
    private static uint imbued;
    private static bool moving, leased, soakedTower;
    private static MovementGate movementGate;
    private static string goal = "";
    private static Vector3? destination;
    private static readonly uint[] PhysicalVulnerability =
    {
        56,
        126,
        200,
        493,
        657,
        695,
        934,
        2090,
        2940,
        3415,
        4162,
        5180
    };
    /// <summary>True only while mechanic movement or a mandatory no-action phase consumes TreeStart.</summary>
    public static bool HoldsTree { get; private set; }

    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && WorldManager.ZoneId == 922 && DutyManager.InInstance && Core.Me != null && Core.Me.IsValid;
    private static bool Active() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat && Math.Abs(Core.Me.Y) < 5;
    private static BattleCharacter[] Party() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Append(Core.Me).Where(a => a != null && a.IsValid && a.IsAlive && Math.Abs(a.Y) < 5 && (a.IsMe || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId))).GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
    /// <summary>Releases only this encounter's leases and avoids on stop, death, loading or departure.</summary>
    public static void Reset()
    {
        Release();
        RestoreHealing();
        foreach (var a in Avoids)
        {
            AvoidanceManager.RemoveAvoid(a);
        }

        foreach (var id in Overrides)
        {
            SideStep.RemoveHandler(id);
        }

        Avoids.Clear();
        Overrides.Clear();
        Casts.Clear();
        MeteorTargets.Clear();
        Markers.Clear();
        Brimstone.Clear();
        Towers.Clear();
        MapStates.Clear();
        Swords.Clear();
        imbued = 0;
        soakedTower = false;
        fireFinish = iceFinish = nextAttempt = DateTime.MinValue;
        swordWindow = towerWindow = DateTime.MinValue;
        HoldsTree = false;
    }

    /// <summary>Samples only on the existing bot-thread pulse; never pulses managers or owns another actor.</summary>
    public static void Pulse()
    {
        HoldsTree = false;
        if (!InDuty())
        {
            Reset();
            return;
        }

        // The Rift lies far below the arena. Hold position during QTE instead of pursuing a grind hotspot.
        if (QTE.IsOpen || Core.Me.Y < -100)
        {
            Hold(Core.Me.Location, "Rift/QTE", .5f);
            HoldsTree = true;
            return;
        }

        if (!Active())
        {
            Reset();
            return;
        }

        if (Avoids.Count == 0)
        {
            Register();
        }

        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && Math.Abs(a.Y) < 5 && a.Location.Distance2D(Center) < 85).ToArray();
        var party = Party();
        foreach (var key in MeteorTargets.Where(p => p.Value < now).Select(p => p.Key).ToArray())
        {
            MeteorTargets.Remove(key);
        }

        foreach (var key in Casts.Where(p => p.Value.Finish.AddSeconds(.7) < now).Select(p => p.Key).ToArray())
        {
            Casts.Remove(key);
        }

        foreach (var key in Markers.Where(p => p.Value.Until < now).Select(p => p.Key).ToArray())
        {
            Markers.Remove(key);
        }

        foreach (var key in Brimstone.Where(p => p.Value.Impact.AddSeconds(15) < now).Select(p => p.Key).ToArray())
        {
            Brimstone.Remove(key);
        }

        foreach (var a in actors.Where(a => a.IsCasting && a.SpellCastInfo.IsValid))
        {
            uint id = a.CastingSpellId;
            var finish = now + a.SpellCastInfo.RemainingCastTime;
            if (id == 20251 && party.Any(p => p.ObjectId == a.SpellCastInfo.TargetId) && !MeteorTargets.ContainsKey(a.SpellCastInfo.TargetId))
            {
                MeteorTargets[a.SpellCastInfo.TargetId] = MeteorDeadline(finish);
            }

            Vector3 origin = id is 20254 or 20266 ? a.SpellCastInfo.CastLocation : a.Location;
            if (!Casts.TryGetValue(a.ObjectId, out var cast) || cast.Action != id)
            {
                Casts[a.ObjectId] = new(id, a.SpellCastInfo.TargetId, origin, a.Heading, finish);
            }
            else
            {
                cast.Refresh(a.SpellCastInfo.TargetId, origin, a.Heading, finish);
            }

            if (id is 20242 or 20243)
            {
                imbued = id;
            }

            if (id == 20270 || (id is 20299 or 20300 && imbued == 20242))
            {
                fireFinish = finish;
            }

            if (id == 20269 || (id is 20299 or 20300 && imbued == 20243))
            {
                iceFinish = finish;
            }

            if (id == 20254)
            {
                if (!Brimstone.TryGetValue(a.ObjectId, out var ground))
                {
                    Brimstone[a.ObjectId] = new(origin, finish);
                }
                else
                {
                    ground.Origin = origin;
                    ground.Impact = finish;
                }
            }

            if (id == 20290)
            {
                swordWindow = finish.AddSeconds(15);
            }

            if (id is 20284 or 20285)
            {
                towerWindow = finish.AddSeconds(20);
            }
        }

        foreach (var a in party.Where(a => a.VfxContainer.IsValid))
        {
            foreach (var icon in a.VfxContainer.LockOns.Where(v => v != null && v.IsValid && v.Id is 87 or 161 or 234))
            {
                var key = (a.ObjectId, (uint)icon.Id);
                // A lock-on is a snapshot, not a timer to extend indefinitely each pulse.
                if (!Markers.ContainsKey(key))
                {
                    Markers[key] = new((uint)icon.Id, now.AddSeconds(icon.Id == 234 ? 6.7 : 5.8));
                }
            }
        }

        UpdateMaps(now);
        LimitBreakResponse(actors, now);
        FullHealRequirement(party);
        // Resolve the in/out geometry before the freeze deadline. Pyretic itself owns
        // the extended hold, avoiding a blind fixed-duration stop after an interrupted cast.
        bool fire = Core.Me.HasAura(960) || ScheduledWindow(fireFinish, now, .6, .8);
        if (fire)
        {
            Hold(Core.Me.Location, "Absolute Fire: stay", .5f);
            if (Core.Me.IsCasting)
            {
                ActionManager.StopCasting();
            }

            // Removing the attack target also prevents an already-enabled autoattack
            // from firing during Pyretic. OrderBot reacquires it after this hold ends.
            Core.Me.ClearTarget();
            HoldsTree = true;
            return;
        }

        // Native emergency escape always owns movement ahead of positive positioning.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            HoldsTree = true;
            return;
        }

        bool Safe(Vector3 p) => Inside(p) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        if (ScheduledWindow(iceFinish, now, 2, .8))
        {
            if (destination == null || goal != "Absolute Blizzard: move" || !Safe(destination.Value) || Core.Me.Distance2D(destination.Value) < .7f)
            {
                destination = Ring(Core.Me.Location, 3).Where(Safe).OrderByDescending(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3? )p).FirstOrDefault();
            }

            if (destination != null)
            {
                Hold(destination.Value, "Absolute Blizzard: move", .2f);
                HoldsTree = true;
                return;
            }
        }

        if (MeteorTargets.ContainsKey(Core.Me.ObjectId))
        {
            var choices = new[]
            {
                new Vector3(82, 0, 82),
                new Vector3(118, 0, 82),
                new Vector3(82, 0, 118),
                new Vector3(118, 0, 118)
            };
            var choice = choices.Where(Safe).OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3? )p).FirstOrDefault();
            if (choice != null)
            {
                Hold(choice.Value, "Radiant Meteor corner", .6f);
                return;
            }
        }

        var wave = Pending(20252).FirstOrDefault();
        if (wave != null)
        {
            // A30y directional push on a40y arena needs a source-side start. Keep a
            // landing margin; the stack resolves after knockback, not before staging.
            var point = Center - Forward(wave.Heading) * 16;
            if (Safe(point))
            {
                Hold(point, "Suiton source side", .6f);
                return;
            }
        }

        if (HasMarker(Core.Me.ObjectId, 87))
        {
            var point = Grid().Where(Safe).OrderByDescending(p => party.Where(a => !a.IsMe).Select(a => a.Location.Distance2D(p)).DefaultIfEmpty(0).Min()).ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3? )p).FirstOrDefault();
            if (point != null)
            {
                Hold(point.Value, "Deluge proximity bait", .6f);
                return;
            }
        }

        var coneSource = ConeBaits(actors, party).FirstOrDefault(b => b.Target == Core.Me.ObjectId);
        if (coneSource != null)
        {
            var point = Grid().Where(p => Safe(p) && p.Distance2D(coneSource.Origin) > 10).OrderBy(p => party.Count(a => !a.IsMe && InCone(a.Location, coneSource.Origin, Heading(coneSource.Origin, p), 46))).ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3? )p).FirstOrDefault();
            if (point != null)
            {
                Hold(point.Value, "Tether/Braver bait", .6f);
                return;
            }
        }

        uint stack = Pending(20253).Select(c => c.Target).FirstOrDefault();
        if (stack == 0)
        {
            stack = Markers.Where(p => p.Value.Id == 161).Select(p => p.Key.Item1).FirstOrDefault();
        }

        var target = party.FirstOrDefault(p => p.ObjectId == stack);
        if (target != null && !HasMarker(Core.Me.ObjectId, 87))
        {
            var anchors = target.IsMe ? party.Where(p => !p.IsMe && !HasMarker(p.ObjectId, 87)).Select(p => p.Location) : new[]
            {
                target.Location
            };
            var choice = new[]
            {
                Core.Me.Location
            }.Concat(anchors.SelectMany(p => Ring(p, 2).Append(p))).Where(p => Safe(p) && (target.IsMe || p.Distance2D(target.Location) < 4) && party.Any(a => !a.IsMe && !HasMarker(a.ObjectId, 87) && p.Distance2D(a.Location) < 4)).OrderByDescending(p => party.Count(a => p.Distance2D(a.Location) < 5)).ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3? )p).FirstOrDefault();
            if (choice != null)
            {
                Hold(choice.Value, "Holy/Katon stack", .7f);
                return;
            }
        }

        var lines = Pending(20248, 20249, 20829).OrderBy(c => c.Finish).ToArray();
        bool vulnerable = PhysicalVulnerability.Any(id => Core.Me.HasAura(id));
        if (lines.Length > 0 && !vulnerable)
        {
            var c = lines[0];
            var t = party.FirstOrDefault(p => p.ObjectId == c.Target);
            if (t != null)
            {
                var candidates = Ring(t.Location, 2).Append(t.Location).Where(Safe).Where(p => InLine(p, c.Origin, Heading(c.Origin, t.Location), 3, 60));
                var point = candidates.OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3? )p).FirstOrDefault();
                if (point != null)
                {
                    Hold(point.Value, "Desperado first eligible line", .6f);
                    return;
                }
            }
        }

        if (!soakedTower && Towers.Count > 0)
        {
            var tower = Towers.OrderBy(p => p.Value).ThenBy(p => TowerPoint(p.Key).Distance2D(Core.Me.Location)).Select(p => TowerPoint(p.Key)).Where(p => Safe(p) && party.Count(a => !a.IsMe && a.Location.Distance2D(p) < 4) < 2).Select(p => (Vector3? )p).FirstOrDefault();
            if (tower != null)
            {
                Hold(tower.Value, "Meteor tower soak", .5f);
                return;
            }
        }

        Release();
    }

    private static IEnumerable<Cast> Pending(params uint[] ids) => Casts.Values.Where(c => ids.Contains(c.Action));
    // Meteor spreads are centered on marked players, not every party member when
    // we are marked. Making all seven peers hazards can exclude every route to a corner.
    internal static bool MeteorHazard(uint actor, uint self, IEnumerable<uint> targets) => actor != self && targets.Contains(actor);
    // Retain the effect for three seconds to cover the observed 2.23-second damage delay.
    internal static DateTime MeteorDeadline(DateTime firstFinish) => firstFinish.AddSeconds(3);
    private static bool HasMarker(uint actor, uint id) => Markers.ContainsKey((actor, id));
    private static Vector3 Forward(float heading) => new((float)Math.Sin(heading), 0, (float)Math.Cos(heading));
    internal static bool Inside(Vector3 p) => Math.Abs(p.X - 100) <= 19 && Math.Abs(p.Z - 100) <= 19 && Math.Abs(p.Y) < 5;
    internal static float Heading(Vector3 from, Vector3 to) => (float)Math.Atan2(to.X - from.X, to.Z - from.Z);
    internal static bool InCone(Vector3 p, Vector3 origin, float heading, float halfDegrees) => Math.Cos(Heading(origin, p) - heading) >= Math.Cos(halfDegrees * Math.PI / 180);
    internal static bool InLine(Vector3 p, Vector3 origin, float heading, float halfWidth, float length)
    {
        var d = p - origin;
        float along = d.X * (float)Math.Sin(heading) + d.Z * (float)Math.Cos(heading);
        return along >= 0 && along <= length && Math.Abs(d.X * (float)Math.Cos(heading) - d.Z * (float)Math.Sin(heading)) <= halfWidth;
    }

    private static IEnumerable<Vector3> Ring(Vector3 p, float radius) => Enumerable.Range(0, 24).Select(i => p + Forward((float)(i * Math.PI / 12)) * radius);
    private static IEnumerable<Vector3> Grid() => Enumerable.Range(0, 19).SelectMany(x => Enumerable.Range(0, 19).Select(z => new Vector3(82 + x * 2, 0, 82 + z * 2)));
    private static IEnumerable<Cast> ConeBaits(BattleCharacter[] actors, BattleCharacter[] party)
    {
        var boss = actors.FirstOrDefault(a => a.NpcId == 9462 && a.IsTargetable);
        if (boss != null)
        {
            foreach (var a in party.Where(a => HasMarker(a.ObjectId, 234)))
            {
                yield return new(20246, a.ObjectId, Center, Heading(Center, a.Location), DateTime.MaxValue);
            }
        }

        foreach (var a in actors.Where(a => a.BaseId == 0x2DEB && a.VfxContainer.IsValid))
        {
            foreach (var tether in a.VfxContainer.Tethers.Where(t => t.Id == 17))
            {
                var target = party.FirstOrDefault(p => p.ObjectId == tether.TargetId);
                if (target != null)
                {
                    yield return new(20260, target.ObjectId, a.Location, Heading(a.Location, target.Location), DateTime.MaxValue);
                }
            }
        }
    }

    private static void Register()
    {
        // Override only owned geometry or semantic attacks. Generic stacks/raidwides
        // and the old24y donut interior would otherwise exclude the required refuge.
        foreach (uint id in new uint[]
        {
            20237,
            20238,
            20239,
            20240,
            20241,
            20246,
            20247,
            20248,
            20249,
            20251,
            20252,
            20253,
            20254,
            20255,
            20256,
            20257,
            20258,
            20259,
            20260,
            20261,
            20262,
            20263,
            20264,
            20265,
            20266,
            20296,
            20299,
            20300,
            21297,
            21627,
            21628,
            20829
        }

        )
        {
            if (SideStep.Override(id))
            {
                Overrides.Add(id);
            }
        }

        Avoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(Active, 38, 38, 140, 140, () => new[] { Center }, AvoidancePriority.High));
        Avoids.Add(AvoidanceManager.AddAvoidLocation(canRun: Active, radiusProducer: (Cast c) => c.Action is 20240 or 20299 ? 10.5f : 6.5f, locationProducer: (Cast c) => c.Origin, collectionProducer: () => Pending(20240, 20299, 20266)));
        Avoids.Add(AvoidanceHelpers.AddAvoidDonut(Active, () => Pending(20241, 20300).Select(c => c.Origin).ToArray(), 60.5, 4.5));
        Avoids.Add(AvoidanceManager.AddAvoidPolygon<Cast>(Active, null, 85, c => -c.Heading, _ => 1, _ => 12, c => c.Action == 20261 ? Rectangle(20.5f, 40.5f) : Cone(60.5f, c.Action == 20259 ? 23.5f : 46), c => c.Origin, () => Pending(20261, 20259, 20260).Where(c => c.Action != 20259 || c.Finish <= Pending(20259).Min(x => x.Finish).AddSeconds(.4))));
        Avoids.Add(AvoidanceManager.AddAvoidLocation(canRun: Active, radiusProducer: (Ground g) => BrimstoneRadius(DateTime.UtcNow - g.Impact), locationProducer: (Ground g) => g.Origin, collectionProducer: () => Brimstone.Values));
        Avoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: Active, objectSelector: a => a.IsValid && !a.IsMe && Party().Any(p => p.ObjectId == a.ObjectId) && MeteorHazard(a.ObjectId, Core.Me.ObjectId, MeteorTargets.Keys), radiusProducer: _ => 20.5f));
        Avoids.Add(AvoidanceManager.AddAvoidPolygon<Cast>(Active, null, 85, c => -c.Heading, _ => 1, _ => 12, _ => Cone(60.5f, 46), c => c.Origin, () => ConeBaits(GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray(), Party()).Where(c => c.Target != Core.Me.ObjectId)));
        Avoids.Add(AvoidanceManager.AddAvoidPolygon<Cast>(() => Active() && PhysicalVulnerability.Any(id => Core.Me.HasAura(id)), null, 85, c => -c.Heading, _ => 1, _ => 12, _ => Rectangle(8.5f, 60.5f), c => c.Origin, () => Pending(20248, 20249, 20829)));
        Avoids.Add(AvoidanceManager.AddAvoidPolygon<Cast>(Active, null, 85, c => -c.Heading, _ => 1, _ => 12, _ => Cone(50.5f, 27.5f), c => c.Origin, () => Swords.Values));
    }

    // Installed ground layout identifiers are not network packet indices. Recognize
    // only known assets. The low state1 arms and4 resolves; unfamiliar states are logged.
    private static void UpdateMaps(DateTime now)
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector d || !d.IsValid)
        {
            return;
        }

        foreach (var e in d.MapEffects)
        {
            bool changed = !MapStates.TryGetValue(e.ID, out var old) || old != e.State;
            MapStates[e.ID] = e.State;
            if (!changed)
            {
                continue;
            }

            if (TowerPoint(e.ID) != Vector3.Zero)
            {
                if (e.State == 1 && now < towerWindow)
                {
                    if (Towers.Count == 0)
                    {
                        soakedTower = false;
                    }

                    Towers[e.ID] = now;
                }
                else if (e.State == 4 && Towers.Remove(e.ID) && Core.Me.Location.Distance2D(TowerPoint(e.ID)) < 4)
                {
                    soakedTower = true;
                }
            }

            if (e.ID >= 0x7FBFE9 && e.ID <= 0x7FBFEC)
            {
                if (e.State == 1 && now < swordWindow)
                {
                    int side = (int)(e.ID - 0x7FBFE9);
                    float h = (float)(-side * Math.PI / 2);
                    // Live north sword remained armed for15s (state1 -> 0800 -> 4).
                    // State4 owns release;20s is only a stale-object watchdog, not impact timing.
                    Swords[e.ID] = new(20262, 0, Center - Forward(h) * 20, h, now.AddSeconds(20));
                }
                else if (e.State == 4 || e.State == 8)
                {
                    Swords.Remove(e.ID);
                }
            }
        }

        if (now >= towerWindow)
        {
            Towers.Clear();
        }

        foreach (var key in Swords.Where(p => p.Value.Finish < now).Select(p => p.Key).ToArray())
        {
            Swords.Remove(key);
        }
    }

    internal static Vector3 TowerPoint(uint id)
    {
        if (id == 0x80BC0C)
        {
            return new(104, 0, 100);
        }

        if (id == 0x80BC0D)
        {
            return new(96, 0, 100);
        }

        int i = id >= 0x7FBFD8 && id <= 0x7FBFDF ? (int)(id - 0x7FBFD8) : id >= 0x80D718 && id <= 0x80D71F ? (int)(id - 0x80D718) : -1;
        return i < 0 ? Vector3.Zero : Center + new Vector3((float)Math.Sin(i * Math.PI / 4) * 10.5f, 0, -(float)Math.Cos(i * Math.PI / 4) * 10.5f);
    }

    internal static float BrimstoneRadius(TimeSpan elapsed) => 6.5f + Math.Min(10.5f, elapsed.TotalSeconds < 0 ? 0 : (float)(.95 * (1 + Math.Max(0, elapsed.TotalSeconds - 2.1) / 1.07)));
    internal static Vector2[] Rectangle(float width, float length) => new[]
    {
        new Vector2(-width / 2, -.5f),
        new Vector2(width / 2, -.5f),
        new Vector2(width / 2, length),
        new Vector2(-width / 2, length)
    };
    internal static Vector2[] Cone(float radius, float half) => new[]
    {
        new Vector2(0, -.5f)
    }.Concat(Enumerable.Range(0, 33).Select(i =>
    {
        double a = (-half + i * half / 16) * Math.PI / 180;
        return new Vector2((float)Math.Sin(a) * radius, (float)Math.Cos(a) * radius);
    })).ToArray();
    private static void Hold(Vector3 point, string reason, float tolerance)
    {
        goal = reason;
        destination = point;
        if (!leased)
        {
            movementGate = new MovementGate(Navigator.PlayerMover);
        }

        leased = true;
        CapabilityManager.Update(Movement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            return;
        }

        if (Core.Me.Location.Distance2D(point) <= tolerance)
        {
            movementGate.Brake();
            moving = false;
            return;
        }

        movementGate.Command(point);
        moving = true;
        HoldsTree = true;
    }

    private static void Release()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            movementGate?.Brake();
        }

        if (leased)
        {
            CapabilityManager.Clear(Movement, CapabilityFlags.Movement, "Seat semantic owner released");
        }

        movementGate?.Detach();
        movementGate = null;
        moving = leased = false;
        goal = "";
        destination = null;
    }

    // A fresh pull has no fire/ice deadline. Compare a duration instead of subtracting
    // from DateTime.MinValue, which threw before semantic handling on the first live pull.
    internal static bool ScheduledWindow(DateTime finish, DateTime now, double lead, double hold)
    {
        if (finish == DateTime.MinValue)
        {
            return false;
        }

        var remaining = (finish - now).TotalSeconds;
        return remaining <= lead && remaining > -hold;
    }

    internal static bool LbWindow(uint action, double seconds) => action == 21628 && seconds > 0 && seconds <= 4;
    private static void LimitBreakResponse(BattleCharacter[] actors, DateTime now)
    {
        var c = actors.Where(a => a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId is 21627 or 21628).OrderByDescending(a => a.CastingSpellId == 21628).FirstOrDefault();
        if (c == null)
        {
            return;
        }

        bool tank = Core.Me.CurrentJob is ClassJobType.Paladin or ClassJobType.Warrior or ClassJobType.DarkKnight or ClassJobType.Gunbreaker;
        uint action = tank && ClassJobRoles.LimitBreak3.TryGetValue(Core.Me.CurrentJob, out var id) ? (uint)id : 0;
        double remaining = c.SpellCastInfo.RemainingCastTime.TotalSeconds;
        bool usable = action != 0 && LimitBreak.Percentage >= 3 && ActionManager.CanCast(action, Core.Me);
        // Action 21627 ends seven seconds before the impact cast. Save tank LB3 for action 21628.
        if (usable && LbWindow(c.CastingSpellId, remaining) && !Core.Me.IsCasting && now >= nextAttempt)
        {
            nextAttempt = now.AddSeconds(1);
            Logger.Information("[Seat] Tank LB3 accepted={0}", ActionManager.DoAction(action, Core.Me));
        }
    }
}
