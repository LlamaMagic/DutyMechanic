using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using Clio.XmlEngine;
using DutyMechanic.Dungeons;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using TreeSharp;

namespace ff14bot.NeoProfiles.Tags;

/// <summary>
/// Coordinates synced World of Darkness on the bot thread. Native objectives own progress,
/// witnessed own-party positions own initial navigation, and OrderBot/routine retain combat.
/// Missing evidence never authorizes abandoning a public duty or choosing an arbitrary portal.
/// </summary>
[XmlElement("WorldOfDarknessRun")]
public sealed class WorldOfDarknessRun : ProfileBehavior
{
    private static WorldOfDarknessRun running;
    // Global installed-client content rows, October 9 2026: territory151, finder111,
    // InstanceContent30020. NPC values below are BNpcName, not encounter BaseId values.
    private const uint Zone=151, Content=30020;
    private readonly CapabilityManagerHandle movement=CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle facing=CapabilityManager.CreateNewHandle();
    private Composite hook;
    private ITargetingProvider previousProvider;
    private Targets provider;
    private bool previousLock, active, moving, holding, faceOwned, previousAutoFace;
    private uint ownPoi, anchorId;
    private DateTime nextEvidence, nextNotice, gazeUntil, nextNavigation, visionUntil;
    private LabyrinthRoutineLease scholarTargetLease;
    private Vector3 gazeOrigin;
    private Vector3 visionGoal;
    private bool visionCasting;
    private DateTime rouletteUntil;
    private string mapEvidence="";
    private object sideStep;
    private readonly HashSet<uint> ownedOverrides=[];
    private bool overridesAttempted;
    private uint overrideSubzone;
    // AvoidanceManager matches collection objects with Equals; records containing a
    // refreshed End timestamp became new hazards every pulse. Run11 Hound rebuilt
    // its escape at30Hz without moving. Keep reference identity through each cast
    // and its impact fence; native polygon updates still observe real geometry
    // changes. These scalar objects are owned and updated only on the bot thread.
    private sealed class CastLine(Vector3 location,float heading,float length,DateTime end)
    {
        public Vector3 Location=location;
        public float Heading=heading,Length=length;
        public DateTime End=end;
    }
    private CastLine cloudBeam;
    private readonly Dictionary<uint,CastLine> electronLines=[];
    private CastLine houndLine;
    private Vector3 slabberPoint;
    private DateTime slabberUntil;
    private DateTime nextFacingEvidence;
    private BattleCharacter[] actors=[];
    private BattleCharacter[] party=[];
    private readonly HashSet<uint> eligible=[];
    private string intent="";
    private int lastProgress=-1;
    private int atomosLane=-1;
    private sealed record JumpWitness(Vector3 Point,DateTime Seen,bool Entry);
    private readonly Dictionary<uint,JumpWitness> jawsGround=[];
    private readonly Dictionary<int,Vector3> jawsEntries=[],jawsReturns=[];
    private DateTime nextJump;
    private DateTime markerCaptureUntil,nextMarkerCapture;
    private string markerEvidence="";
    private uint levelTarget,levelAction;
    private DateTime levelUntil;
    private uint thunderTarget;
    private DateTime thunderUntil;
    private BattleCharacter[] raidPlayers=[];
    private bool eyeEntered;
    private readonly Dictionary<uint,JumpWitness> jawsExitGround=[];
    private Vector3? jawsCenter,jawsExit;
    private DateTime recoveryQuietSince,nextRecoveryEvidence,nextReturn,nextShortcut;
    private uint pendingShortcut;

    /// <summary>Finishes only after a valid completed director or a verified departure, never on a missing director during loading.</summary>
    public override bool IsDone => !CommonBehaviors.IsLoading &&
        (!DutyManager.InInstance || WorldManager.ZoneId!=Zone || Director()?.InstanceEnded==true);

    private static InstanceContentDirector Director()
    {
        if(CommonBehaviors.IsLoading || !DutyManager.InInstance || WorldManager.ZoneId!=Zone)return null;
        var d=DirectorManager.ActiveDirector as InstanceContentDirector;
        return d!=null && d.IsValid && d.DungeonId==Content ? d : null;
    }

    /// <summary>
    /// Runs on the plugin's bot-thread pulse while an external death coroutine can
    /// starve this tag. Accepts only native dead-player Return after a cleared wing
    /// or a visibly reset Cerberus, never a duty departure; pending raises retain priority.
    /// </summary>
    internal static void PulseRecovery()
    {
        var owner=running;
        if(owner==null||!owner.active||!TreeRoot.IsRunning)return;
        var d=Director();
        if(d==null||Core.Player==null||Core.Player.IsAlive||Core.Player.InCombat||Core.Player.HasAura(148))
        {owner.recoveryQuietSince=DateTime.MinValue;return;}
        var enemies=GameObjectManager.GetObjectsOfType<BattleCharacter>(true,false)
            .Where(a=>a.IsValid&&a.IsNpc&&a.IsAlive&&a.CanAttack&&
                Math.Abs(a.Y-Core.Player.Y)<20&&a.Location.Distance2D(Core.Player.Location)<90).ToArray();
        var roster=PartyManager.AllMembers.Select(p=>p.ObjectId).ToHashSet();
        var allies=GameObjectManager.GetObjectsOfType<BattleCharacter>(true,false)
            .Where(a=>a.IsValid&&a.IsAlive&&roster.Contains(a.ObjectId)).ToArray();
        if(DateTime.UtcNow>=owner.nextRecoveryEvidence)
        {
            owner.nextRecoveryEvidence=DateTime.UtcNow.AddSeconds(10);
            owner.Log($"Recovery evidence: subzone={WorldManager.SubZoneId} ended={d.InstanceEnded} cerberus={d.GetTodoArgs(7).Item1} revive={ClientGameUiRevive.ReviveState} yesno={SelectYesno.IsOpen} combatEnemies={enemies.Count(a=>a.InCombat)} liveAllies={allies.Length} fightingAllies={allies.Count(a=>a.InCombat)}");
        }
        int slot=WorldManager.SubZoneId switch{1439=>2,1440=>3,1441=>4,1442=>5,1443=>6,1444=>7,1446=>8,_=>-1};
        bool cleared=d.InstanceEnded||slot>=0&&d.GetTodoArgs(slot).Item1==1;
        // No-enemy snapshots alone can mean actors unloaded. Require the actual
        // main boss at full health to prove an uncleared Cerberus has reset.
        bool reset=WorldManager.SubZoneId==1444&&enemies.Any(a=>a.BaseId==3574&&a.NpcId==3234&&
            a.IsTargetable&&!a.InCombat&&a.CurrentHealth==a.MaxHealth)&&allies.Length>=1;
        // Allies can already be fighting the next boss after this wing clears;
        // their remote combat must not strand a corpse in the cleared arena.
        bool quiet=!enemies.Any(a=>a.InCombat)&&(cleared||!allies.Any(a=>a.InCombat));
        if(!(cleared||reset)||!quiet||ClientGameUiRevive.ReviveState!=ReviveState.Dead||!SelectYesno.IsOpen)
        {owner.recoveryQuietSince=DateTime.MinValue;return;}
        // A fifteen-second quiet window allows a pending raise and late combat
        // updates to win. Return remains an in-instance recovery, not LeaveDuty.
        if(owner.recoveryQuietSince==DateTime.MinValue)owner.recoveryQuietSince=DateTime.UtcNow;
        if(DateTime.UtcNow-owner.recoveryQuietSince<TimeSpan.FromSeconds(15)||DateTime.UtcNow<owner.nextReturn)return;
        owner.nextReturn=DateTime.UtcNow.AddSeconds(10);
        owner.Log(cleared?"Recovery: cleared encounter while dead; accepting in-duty Return.":"Recovery: verified reset Cerberus while dead; accepting in-duty Return.");
        SelectYesno.Yes();
    }

    /// <inheritdoc/>
    protected override void OnStart()
    {
        // OrderBot can re-enter the XML after death recovery without disposing the earlier
        // behavior. Restore its leases before acquiring ours; two controllers must never
        // fight over targeting, movement, or the saved auto-face preference.
        if(ReferenceEquals(running,this)&&active)return;
        running?.Release(!CommonBehaviors.IsLoading);
        running=this;
        active=true;TreeRoot.OnStop+=OnStop;
        previousProvider=CombatTargeting.Instance.Provider;previousLock=CombatTargeting.Instance.Locked;
        provider=new Targets(this);CombatTargeting.Instance.Locked=false;
        CombatTargeting.Instance.Provider=provider;CombatTargeting.Instance.Locked=true;
        // Labyrinth established that Scholar's independent multi-dot selection can bypass
        // OrderBot eligibility. Reuse its reversible lease; never persist a routine setting.
        if(Core.Player.CurrentJob.ToString()=="Scholar"&&RoutineManager.Current?.Name.Contains("Magitek")==true)
        {
            var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("Magitek.Models.Scholar.ScholarSettings",false)).FirstOrDefault(t=>t!=null);
            var settings=type?.GetProperty("Instance")?.GetValue(null);
            var property=type?.GetProperty("BioMultipleTargets");
            if(settings!=null&&property?.PropertyType==typeof(bool)&&property.CanRead&&property.CanWrite)
                scholarTargetLease=new LabyrinthRoutineLease(settings,property);
        }
        // A normal Order slot does not pulse during Kill POIs. One TreeStart hook owns both
        // urgent mechanics and ready checks; the ordinary tag slot remains inert.
        hook=new ActionRunCoroutine(_=>Tick());TreeHooks.Instance.AddHook("TreeStart",hook);
        // Breath of Ice3286 spawned777/778/779 successively at the recorded impact
        // positions. SideStep removed the initial cast avoid; Deep Freeze then hit6.5
        // yalms from a growing pool. Reserve12 yalms through the visible object lifetime
        // as a conservative first-test margin, not a claimed measured final pool radius.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition:()=>active&&WorldManager.ZoneId==Zone&&WorldManager.SubZoneId==1442,
            objectSelector:o=>o.IsValid&&o.IsVisible&&o.NpcId is >=2004777 and <=2004779,
            radiusProducer:_=>12,priority:AvoidancePriority.High));
        // Slabber left2004620 at its cast location. The first run acquired Seized609
        // and died after the cast avoid expired. Until Mini/belly entry is verified,
        // the outside helper role must keep clear of this persistent Devour trigger.
        // Its8-yalm action radius gets2 yalms of movement/hitbox margin.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition:()=>active&&WorldManager.ZoneId==Zone&&WorldManager.SubZoneId==1444,
            objectSelector:o=>o.IsValid&&o.IsVisible&&o.NpcId==2004620,
            radiusProducer:_=>10,priority:AvoidancePriority.High));
        // Sixth-run wrapper evidence paired lock-on44 with3275 and45 with3277;
        // tether5 pointed at that marked player. Both installed child actions
        // have radius12. Reserve one extra yalm and let RB choose the mesh escape.
        // The bound marked player cannot leave; never create a self-centered avoid.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition:()=>active&&WorldManager.ZoneId==Zone&&WorldManager.SubZoneId==1440&&
                DateTime.UtcNow<levelUntil&&levelTarget!=0&&levelTarget!=Core.Player.ObjectId,
            objectSelector:o=>o.IsValid&&o.ObjectId==levelTarget,
            radiusProducer:_=>13,priority:AvoidancePriority.High));
        // Global3297 is a60-long,24-wide forward beam. At12:02:07.777 the generic
        // polygon left Tired8.2 yalms off its axis and Vulnerability202 followed.
        // Anchor at the relocated caster, not its stale prior cast location; pad
        // edges0.5 and retain1.25s past cast end (observed status delay0.86s).
        // Run10 escaped along the axis beyond60 and still took3297 at17:11:13.
        // Conservatively include the caster's14-yalm CombatReach so RB chooses
        // a lateral exit instead of treating the unverified far endpoint as safe.
        // This is an observed false-safe correction, not a measured74-yalm claim.
        AvoidanceManager.AddAvoidPolygon<CastLine>(
            ()=>active&&WorldManager.ZoneId==Zone&&WorldManager.SubZoneId==1446,
            null,100,b=>-b.Heading,_=>1,_=>15,
            b=>new[]{new Vector2(-12.5f,-.5f),new Vector2(12.5f,-.5f),new Vector2(12.5f,b.Length+.5f),new Vector2(-12.5f,b.Length+.5f)},
            b=>b.Location,()=>cloudBeam!=null&&DateTime.UtcNow<cloudBeam.End?new[]{cloudBeam}:Array.Empty<CastLine>(),
            priority:AvoidancePriority.High);
        // Sixth-run3253 had zero generic avoids and applied Electrocution while
        // Tired stood between its two Electron actors. Global Action3253 is a
        // target-length line (type8, radius0, width4), so stale CastLocation and
        // initial caster heading cannot define it. Use the actual linked endpoint,
        // pad half-width2 by0.5, and preserve0.8s for observed impact latency.
        AvoidanceManager.AddAvoidPolygon<CastLine>(
            ()=>active&&WorldManager.ZoneId==Zone&&WorldManager.SubZoneId==1444,
            null,100,b=>-b.Heading,_=>1,_=>15,
            b=>new[]{new Vector2(-2.5f,-.5f),new Vector2(2.5f,-.5f),new Vector2(2.5f,b.Length+.5f),new Vector2(-2.5f,b.Length+.5f)},
            b=>b.Location,()=>electronLines.Values.Where(b=>DateTime.UtcNow<b.End).ToArray(),
            priority:AvoidancePriority.High);
        // Hound3247 hit twice with zero generic avoids. Global Action columns28/29/30
        // are8/0/14: a target-length charge14 wide. The native cast target is its
        // invisible base410 endpoint; initial heading and CastLocation were stale.
        // Reuse endpoint geometry with0.5-yalm padding and0.8s impact retention.
        AvoidanceManager.AddAvoidPolygon<CastLine>(
            ()=>active&&WorldManager.ZoneId==Zone&&WorldManager.SubZoneId==1444,
            null,100,b=>-b.Heading,_=>1,_=>15,
            b=>new[]{new Vector2(-7.5f,-.5f),new Vector2(7.5f,-.5f),new Vector2(7.5f,b.Length+.5f),new Vector2(-7.5f,b.Length+.5f)},
            b=>b.Location,()=>houndLine!=null&&DateTime.UtcNow<houndLine.End?new[]{houndLine}:Array.Empty<CastLine>(),
            priority:AvoidancePriority.High);
        Log("Tag owns World of Darkness mechanics and travel; combat remains with OrderBot.");
    }
    /// <inheritdoc/>
    protected override Composite CreateBehavior()=>new ActionAlwaysFail();
    /// <inheritdoc/>
    protected override void OnDone(){Release(!CommonBehaviors.IsLoading);}
    private void OnStop(BotBase bot)=>Release(false);

    private void Release(bool native)
    {
        if(!active)return;active=false;
        if(native)StopTravel(); else moving=false;
        if(native&&ownPoi!=0&&Poi.Current.Type==PoiType.Kill&&Poi.Current.Unit?.ObjectId==ownPoi)Poi.Clear("World of Darkness owner released");
        scholarTargetLease?.Dispose();scholarTargetLease=null;
        ReleaseHold();ReleaseFacing();ReleaseMechanicOverrides();eligible.Clear();cloudBeam=null;electronLines.Clear();houndLine=null;
        if(hook!=null)TreeHooks.Instance.RemoveHook("TreeStart",hook);hook=null;
        if(ReferenceEquals(CombatTargeting.Instance.Provider,provider))
        {CombatTargeting.Instance.Locked=false;CombatTargeting.Instance.Provider=previousProvider;CombatTargeting.Instance.Locked=previousLock;}
        TreeRoot.OnStop-=OnStop;
        if(ReferenceEquals(running,this))running=null;
    }

    private async Task<bool> Tick()
    {
        if(!active)return false;
        var d=Director();
        if(d==null || d.InstanceEnded || Core.Player==null || !Core.Player.IsAlive || QuestLogManager.InCutscene)
        {eligible.Clear();ReleaseHold();ReleaseFacing();if(!CommonBehaviors.IsLoading)StopTravel();return false;}
        Snapshot(d);
        UpdateMechanicOverrides(WorldManager.SubZoneId);
        Notices();
        // Heat Wave's action3294 applies Pyretic639. This is an action prohibition, not an
        // unsafe floor. Consuming the short status window intentionally prevents rotation damage.
        if(Core.Player.HasAura(639) && actors.Any(a=>a.NpcId==3227))
        {
            ReleaseFacing();HoldMovement("Pyretic: waiting for status to end");StopTravel();
            if(Core.Player.IsCasting)ActionManager.StopCasting();
            return true;
        }
        // Mortal Gaze is a facing check. Reserve facing independently from movement so
        // ordinary emergency avoidance can still escape a simultaneous floor mechanic.
        // Live capture showed boss3281 ends 1.5s before helper3499. The helper owns the
        // actual Doom check; releasing on the boss alone caused two confirmed deaths.
        var gaze=actors.Where(a=>a.NpcId==3231 && a.IsCasting && a.CastingSpellId is 3281 or 3499)
            .OrderByDescending(a=>a.SpellCastInfo.RemainingCastTime).FirstOrDefault();
        if(gaze!=null)
        {gazeOrigin=gaze.Location;gazeUntil=DateTime.UtcNow+gaze.SpellCastInfo.RemainingCastTime+TimeSpan.FromMilliseconds(1000);}
        if(DateTime.UtcNow<gazeUntil)
        {
            if(!faceOwned){previousAutoFace=GameSettingsManager.FaceTargetOnAction;GameSettingsManager.FaceTargetOnAction=false;faceOwned=true;}
            CapabilityManager.Update(facing,CapabilityFlags.Facing,2000,"Mortal Gaze look-away");
            if(Core.Player.IsCasting)ActionManager.StopCasting();
            // Installed DriverProvider confirms atan2(X,Z) and wraps negative angles.
            // The second run still gained Doom with the longer timer, so also remove
            // combat's target/movement ownership rather than stopping only tag travel.
            eligible.Clear();
            if(Poi.Current.Type==PoiType.Kill&&Poi.Current.Unit?.ObjectId==ownPoi)Poi.Clear("Mortal Gaze facing owner");
            Core.Player.ClearTarget();Navigator.Stop();MovementManager.MoveStop();moving=false;
            var away=Core.Player.Location-gazeOrigin;
            float desired=(float)Math.Atan2(away.X,away.Z);
            if(DateTime.UtcNow>=nextFacingEvidence)
            {
                nextFacingEvidence=DateTime.UtcNow.AddMilliseconds(500);
                Log($"Facing evidence: actual={Core.Player.Heading:F3} desired={desired:F3} casting={Core.Player.IsCasting} holdMs={(gazeUntil-DateTime.UtcNow).TotalMilliseconds:F0}");
            }
            Core.Player.SetFacing(desired);
            StopTravel();SetIntent("Mortal Gaze: facing away through impact");return true;
        }
        ReleaseFacing();
        if(AvoidanceManager.IsRunningOutOfAvoid){moving=false;ReleaseHold();return false;}
        // SMN stopped in1439 while still on the Eye boundary (Oct9 19:20), then
        // fought at the sealed landing rim. Subzone alone is not proof of entry.
        // Require the inner floor around the captured arena object2004735 before
        // assigning ranged combat, following an already present own-party cluster.
        var eyeCenter=new Vector3(-76.98546f,16,382.7466f);
        if(WorldManager.SubZoneId==1438)eyeEntered=false;
        if(d.GetTodoArgs(2).Item1==1||WorldManager.SubZoneId==1439&&Core.Player.Location.Distance2D(eyeCenter)<23)eyeEntered=true;
        if(!eyeEntered&&d.GetTodoArgs(2).Item1!=1)
        {
            SelectTargets();
            var inside=party.Where(p=>p.IsAlive&&Math.Abs(p.Y-16)<2&&p.Location.Distance2D(eyeCenter)<23).ToArray();
            if(inside.Length>=3)
                return await Move(inside.OrderBy(p=>p.Location.Distance2D(Core.Player.Location)).First().Location,2,"Eye: finish entering with own party");
        }
        // Return keeps the public duty active. At the starting floor, only its
        // captured wormhole generator2004722 or native shortcut2000700 may recover
        // travel; accept a Yes/no only after our own nearby interaction. Unknown
        // selection menus remain untouched rather than choosing a numeric slot.
        if(d.GetTodoArgs(2).Item1==1&&WorldManager.SubZoneId==1438&&Core.Player.Z>430&&!Core.Player.InCombat)
        {
            var shortcut=GameObjectManager.GameObjects.FirstOrDefault(o=>o.IsValid&&o.IsTargetable&&
                o.NpcId is 2004722 or 2000700&&o.Location.Distance(Core.Player.Location)<35);
            if(shortcut!=null)
            {
                if(SelectYesno.IsOpen)
                {
                    if(pendingShortcut==shortcut.ObjectId&&shortcut.Location.Distance(Core.Player.Location)<5&&DateTime.UtcNow>=nextShortcut)
                    {Log("Recovery: accepting owned entrance shortcut confirmation.");SelectYesno.Yes();nextShortcut=DateTime.UtcNow.AddSeconds(3);}
                    return false;
                }
                if(SelectString.IsOpen||SelectIconString.IsOpen){StopTravel();SetIntent("Recovery: shortcut selection needs verified game-text mapping");return false;}
                if(!shortcut.IsWithinInteractRange)return await Move(shortcut.Location,2,"Recovery: entrance shortcut");
                StopTravel();
                if(DateTime.UtcNow>=nextShortcut){pendingShortcut=shortcut.ObjectId;shortcut.Interact();nextShortcut=DateTime.UtcNow.AddSeconds(2);}
                return false;
            }
        }
        else pendingShortcut=0;

        // Color preparation must not pull an escaped player back into a level
        // circle. Keep routine actions available while its movement is leased;
        // an active native escape above still owns movement. For the bound target,
        // this also avoids repeatedly trying an impossible color reposition.
        if(WorldManager.SubZoneId==1440&&DateTime.UtcNow<levelUntil&&levelTarget!=0)
        {
            HoldMovement("Angra level circle: preserve marked-target separation");
            StopTravel();SelectTargets();return false;
        }

        // Global Lockon318 is com_share3_6s0p, observed on Dragon players before
        // the shared hit; Action3287 has radius6. Gather within2yalms so normal
        // arrival error still shares damage. A marked player seeks a nearby
        // living alliance cluster, not an arbitrary edge or another alliance's
        // route. Existing floor escapes and Pyretic remain higher priority.
        if(WorldManager.SubZoneId==1442&&DateTime.UtcNow<thunderUntil&&thunderTarget!=0)
        {
            var marked=raidPlayers.FirstOrDefault(p=>p.ObjectId==thunderTarget&&p.IsAlive);
            bool mainTank=actors.Any(a=>a.BaseId==3580&&a.CurrentTargetId==Core.Player.ObjectId);
            // Do not rotate the boss into the raid to chase another player's stack.
            if(marked!=null&&!mainTank)
            {
                var destination=marked;
                if(marked.ObjectId==Core.Player.ObjectId)
                {
                    var nearby=raidPlayers.Where(p=>p.ObjectId!=Core.Player.ObjectId&&p.IsAlive&&
                        Math.Abs(p.Y-Core.Player.Y)<2&&p.Location.Distance2D(Core.Player.Location)<25).ToArray();
                    if(nearby.Count(p=>p.Location.Distance2D(Core.Player.Location)<5)>=3)
                    {HoldMovement("Breath of Thunder: hold shared stack");StopTravel();SelectTargets();return false;}
                    destination=nearby.OrderByDescending(p=>nearby.Count(q=>q.Location.Distance2D(p.Location)<5))
                        .ThenBy(p=>p.Location.Distance2DSqr(Core.Player.Location)).FirstOrDefault();
                }
                if(destination!=null){SelectTargets();return await Hold(destination.Location,2,"Breath of Thunder: gather for shared damage");}
            }
        }

        // Fifth run13:55:00 remained inside a correctly centered Slabber cast
        // avoid without RB initiating escape; the visible puddle arrived only
        // after Seized. Bridge that gap from the captured cast through8s after
        // impact, and use an observed nearby ally outside every Slabber circle
        // only when ordinary avoidance is not already moving us. This is a
        // scoped fallback for the reproduced failure, not general combat travel.
        if(WorldManager.SubZoneId==1444&&!Core.Player.HasAura(3518)&&!Core.Player.HasAura(609)&&!Core.Player.HasAura(645))
        {
            var puddles=GameObjectManager.GetObjectsOfType<EventObject>().Where(o=>o.IsValid&&o.IsVisible&&o.NpcId==2004620)
                .Select(o=>o.Location).ToList();
            if(DateTime.UtcNow<slabberUntil)puddles.Add(slabberPoint);
            if(puddles.Any(p=>p.Distance2D(Core.Player.Location)<11))
            {
                // Native Slabber radius8 plus3yalms tolerates arrival error and
                // the transition between the cast point and the persistent object.
                // If nobody supplies a safe witness, leave recovery to RB; never
                // invent an arena-edge point or consume combat pulses indefinitely.
                var safe=party.Where(p=>p.IsAlive&&Math.Abs(p.Y-Core.Player.Y)<2&&
                    p.Location.Distance2D(Core.Player.Location)<30&&puddles.All(q=>q.Distance2D(p.Location)>12))
                    .OrderBy(p=>p.Location.Distance2DSqr(Core.Player.Location)).FirstOrDefault();
                if(safe!=null){SelectTargets();return await Hold(safe.Location,1,"Slabber: witnessed escape fallback");}
            }
        }

        // Tail Blow3246 hit a ranged healer17.45yalms behind Cerberus during its
        // 1.7s cast (13:15:24, Blunt Resistance Down573 immediately afterward).
        // Combine Global Action radius9 with CombatReach as a conservative reach
        // model for live testing; Omen4 is fan090_1bf, a rear90-degree cone.
        // Prepare non-tanks on the nearer flank
        // before that short deadline. Reserve an extra15degrees/2yalms at its edge,
        // and leave Mini/belly handling and all active floor escapes to their owners.
        var cerberus=actors.FirstOrDefault(a=>a.BaseId==3574&&a.NpcId==3234&&a.IsAlive&&a.InCombat);
        bool tankJob=Core.Player.CurrentJob.ToString() is "Paladin" or "Warrior" or "DarkKnight" or "Gunbreaker";
        if(cerberus!=null&&!tankJob&&!Core.Player.HasAura(3518)&&!Core.Player.HasAura(609)&&!Core.Player.HasAura(645)&&!Core.Player.HasAura(646))
        {
            var forward=new Vector3((float)Math.Sin(cerberus.Heading),0,(float)Math.Cos(cerberus.Heading));
            var right=new Vector3(forward.Z,0,-forward.X);
            var delta=Core.Player.Location-cerberus.Location;
            float distance=Core.Player.Location.Distance2D(cerberus.Location);
            if(distance<cerberus.CombatReach+11&&delta.X*forward.X+delta.Z*forward.Z<-.5f*distance)
            {
                float side=delta.X*right.X+delta.Z*right.Z<0?-1:1;
                SelectTargets();
                return await Hold(cerberus.Location+right*(side*(cerberus.CombatReach+3)),1,"Cerberus: prepare outside rear Tail Blow cone");
            }
        }

        // Installed statuses636/637 and the primary trigger agree: Sullen needs the
        // rear/red half, Ireful the front/white half of the next Double Vision. Capture
        // one destination at cast start so the newly applied brand cannot reverse it
        // during impact. A small offset crosses the dividing line without chasing edges.
        var vision=actors.FirstOrDefault(a=>a.NpcId==3231&&a.IsCasting&&a.CastingSpellId==3272);
        if(vision!=null&&!visionCasting)
        {
            bool sullen=Core.Player.HasAura(636),ireful=Core.Player.HasAura(637);
            if(sullen!=ireful)
            {
                var forward=new Vector3((float)Math.Sin(vision.Heading),0,(float)Math.Cos(vision.Heading));
                var right=new Vector3(forward.Z,0,-forward.X);
                var delta=Core.Player.Location-vision.Location;
                float lateral=Math.Clamp(delta.X*right.X+delta.Z*right.Z,-12,12);
                visionGoal=vision.Location+forward*(sullen?-5:5)+right*lateral;
                visionUntil=DateTime.UtcNow+vision.SpellCastInfo.RemainingCastTime+TimeSpan.FromMilliseconds(700);
            }
        }
        visionCasting=vision!=null;
        if(DateTime.UtcNow<visionUntil)
        {SelectTargets();return await Hold(visionGoal,1,"Double Vision: cross to the opposite brand half");}

        // Fourth-run ranged position was23yalms behind Angra at the2.2s cast;
        // movement stopped still behind and retained Ireful, a confirmed wrong half.
        // Prepare ranged jobs after the preceding impact instead of waiting for a
        // deadline they cannot meet. Keep12yalms lateral clearance from Stare's
        // frontal line. Tank/melee placement remains with their existing cast response.
        // Hourglass deaths can trigger Roulette immediately: allow six seconds after
        // the last live hourglass before changing position for the later color cast.
        bool ranged=Core.Player.CurrentJob.ToString() is "Scholar" or "WhiteMage" or "Astrologian" or "Sage" or
            "Summoner" or "BlackMage" or "RedMage" or "Pictomancer" or "Bard" or "Machinist" or "Dancer";
        var angra=actors.FirstOrDefault(a=>a.BaseId==3584&&a.NpcId==3231&&a.IsAlive&&a.InCombat);
        bool hasSullen=Core.Player.HasAura(636),hasIreful=Core.Player.HasAura(637);
        if(ranged&&angra!=null&&hasSullen!=hasIreful&&DateTime.UtcNow>=rouletteUntil)
        {
            var forward=new Vector3((float)Math.Sin(angra.Heading),0,(float)Math.Cos(angra.Heading));
            var right=new Vector3(forward.Z,0,-forward.X);
            var delta=Core.Player.Location-angra.Location;
            float side=delta.X*right.X+delta.Z*right.Z<0?-12:12;
            SelectTargets();
            return await Hold(angra.Location+forward*(hasSullen?-5:5)+right*side,1.5f,"Double Vision: prepare ranged opposite half");
        }

        // Jaws lanes are established by the living own-party majority around an Atomos.
        // Never infer alliance from a sealed teleport landing or carry Labyrinth's assignment.
        // Sixth run returned during combat, then remained below after the clear.
        // A completed objective removes living Atomos actors but does not put us
        // back on the route. Retain the witnessed lower return through that state;
        // native jumps remain bounded and ordinary sealed transfer still recovers.
        if(d.GetTodoArgs(6).Item1==1&&WorldManager.SubZoneId==1443&&Core.Player.Y>94.8f&&Core.Player.Y<95.7f&&jawsExit is Vector3 exit)
            return await JumpFromWitness(exit,"Jaws: witnessed departure pad",.15f);
        if(WorldManager.SubZoneId==1443&&Core.Player.Y>89&&Core.Player.Y<92&&
            jawsReturns.TryGetValue(atomosLane,out var recovery))
            return await JumpFromWitness(recovery,"Jaws: witnessed lower-ring return");
        // Each lane also has an identically named helper434 at the same location.
        // Counting helpers made every majority a tie in the first live Jaws encounter.
        var atomos=actors.Where(a=>a.IsAlive && a.BaseId==3492 && a.NpcId is >=3380 and <=3382).ToArray();
        if(atomos.Length>0)
        {
            if(atomosLane<0)
            {
                var votes=atomos.Select(a=>new{Actor=a,Votes=party.Count(p=>p.IsAlive&&p.Location.Distance(a.Location)<23)})
                    .OrderByDescending(a=>a.Votes).ToArray();
                if(votes.Length>0&&votes[0].Votes>=3&&(votes.Length==1||votes[0].Votes>votes[1].Votes))
                {atomosLane=(int)votes[0].Actor.NpcId;Log("Jaws own-party lane actor="+atomosLane);}
            }
            var own=atomos.FirstOrDefault(a=>a.NpcId==atomosLane);
            // The third run's center endpoint hit an obstruction before combat began.
            // An own-party ally instead launched from(6.33,94.75,92) into the east
            // lane. Learn the actual takeoff/landing pair per run; never route to
            // an airborne party member or infer a lane from a sealed transfer.
            if(d.GetTodoArgs(6).Item1!=1&&Core.Player.Z>84.8f&&Core.Player.Z<135&&
                Math.Abs(Core.Player.X)<15&&jawsEntries.TryGetValue(atomosLane,out var entry))
            {
                // The fifth run stopped0.46yalms before its witnessed point and
                // never launched. Reach the takeoff itself, not the previous0.6
                // acceptance edge, before trying the native jump.
                // Run10's ally jumped early at Z90.13: its last grounded sample
                // was not the pad trigger, so our stationary jumps never launched.
                // For the independently assigned middle lane only, finish at the
                // validated run9 launch(-.143,94.75,89.257). Retain witnessed entry
                // as the prerequisite; do not apply this point to either side lane.
                if(atomosLane==3381)entry=new Vector3(-.143f,94.75f,89.257f);
                return await JumpFromWitness(entry,"Jaws: own-party witnessed entry pad",.15f);
            }
            // Sealed transfer can land on another lane at the same height. A
            // height match alone must not route across the gap to our Atomos.
            if(own!=null&&Math.Abs(Core.Player.Y-own.Y)<2&&Core.Player.Location.Distance2D(own.Location)<23&&own.IsCasting&&own.CastingSpellId==3416)
            {
                SelectTargets();
                return await Hold(own.Location,1,"Jaws Shockwave: stage under own Atomos");
            }
        }
        // Abandonment is removed by proximity. Choose a live own-party ally rather than
        // a global alliance cluster; no movement is required once within the small goal.
        if(Core.Player.HasAura(646)&&actors.Any(a=>a.NpcId==3234))
        {
            var friend=party.Where(p=>p.IsAlive&&Math.Abs(p.Y-Core.Player.Y)<5).OrderBy(p=>p.Location.Distance(Core.Player.Location)).FirstOrDefault();
            if(friend!=null){SelectTargets();return await Hold(friend.Location,2,"Abandonment: stay near own party");}
        }
        ReleaseHold();
        SelectTargets();
        // A sealed transfer can set combat before any eligible target is in range.
        // Let own-party navigation close that gap instead of idling at the arena door.
        if(Poi.Current.Type==PoiType.Kill || Core.Player.InCombat&&eligible.Count>0){StopTravel();return false;}

        // Cloud disappears/repositions before3297. In the fourth run, losing its
        // target triggered follow travel13:18:22 into the future beam's center;
        // only1.394s remained when the cast was observed. With no eligible add,
        // hold the existing arena position during this combat phase instead of
        // treating target loss as a route transition. Floor avoidance still wins.
        if(WorldManager.SubZoneId==1446&&Core.Player.InCombat)
        {StopTravel();return false;}

        // The final transit removes allies from the local floor instantaneously. A
        // distance-limited follow stops short when its last anchor disappears. Only
        // a cleared Cerberus objective and a witnessed party on the upper Cloud floor
        // permit finishing the last witnessed approach. At11:25:18 the anchor was at
        //(-.748,143.635,-325.173), then teleported. Ordinary four-yalm follow stopped
        // short and switched backwards to the trailing ally. No object interaction.
        if(d.GetTodoArgs(7).Item1==1&&Core.Player.Y<200&&Core.Player.Z< -270&&
            party.Count(p=>p.IsAlive&&p.Y>250&&p.X< -250)>=3)
        {
            return await Move(new Vector3(-.748f,143.635f,-325.173f),.25f,"Cloud of Darkness transit approach");
        }

        // Initial route discovery follows witnessed party progress through RB's mesh. Hysteresis
        // preserves a follow episode across ticks instead of start/stop movement at one threshold.
        var near=party.Where(p=>p.IsAlive&&Math.Abs(p.Y-Core.Player.Y)<30&&p.Location.Distance(Core.Player.Location)<120).ToArray();
        var anchor=near.FirstOrDefault(p=>p.ObjectId==anchorId);
        // Stable does not mean permanent: do not stay with one idle party member after the
        // rest of the party has moved on. Require a coherent replacement cluster first.
        if(anchor!=null&&near.Count(p=>p.Location.Distance(anchor.Location)<15)<3&&
            near.Any(p=>near.Count(q=>q.Location.Distance(p.Location)<15)>=3))anchor=null;
        if(anchor==null)
        {
            anchor=near.OrderByDescending(p=>near.Count(q=>q.Location.Distance(p.Location)<15))
                .ThenBy(p=>p.Location.Distance(Core.Player.Location)).FirstOrDefault();
            anchorId=anchor?.ObjectId??0;
        }
        if(anchor!=null)
        {
            var distance=anchor.Location.Distance(Core.Player.Location);
            if(distance>10 || moving&&distance>4)return await Move(anchor.Location,4,"Follow own-party progress");
        }
        StopTravel();return false;
    }

    private static CastLine RefreshLine(CastLine line,Vector3 location,float heading,float length,DateTime end)
    {
        // Expiry separates casts. Timer jitter alone must never invalidate RB's
        // in-progress escape, while a moving endpoint must still update its shape.
        if(line==null||DateTime.UtcNow>=line.End)return new CastLine(location,heading,length,end);
        line.Location=location;line.Heading=heading;line.Length=length;line.End=end;
        return line;
    }

    private void Snapshot(InstanceContentDirector d)
    {
        var all=GameObjectManager.GetObjectsOfType<BattleCharacter>(true,false).Where(a=>a.IsValid).ToArray();
        raidPlayers=all.Append(Core.Player).DistinctBy(a=>a.ObjectId).Where(a=>!a.IsNpc&&a.IsAlive&&
            a.Location.Distance2D(Core.Player.Location)<65&&Math.Abs(a.Y-Core.Player.Y)<2).ToArray();
        var roster=PartyManager.AllMembers.Select(p=>p.ObjectId).ToHashSet();
        party=all.Where(a=>roster.Contains(a.ObjectId)&&a.ObjectId!=Core.Player.ObjectId).ToArray();
        actors=all.Where(a=>a.IsNpc&&a.Location.Distance2D(Core.Player.Location)<100&&Math.Abs(a.Y-Core.Player.Y)<30).ToArray();
        foreach(var expired in electronLines.Where(x=>DateTime.UtcNow>=x.Value.End).Select(x=>x.Key).ToArray())electronLines.Remove(expired);
        if(WorldManager.SubZoneId==1444)
        {
            var hound=actors.FirstOrDefault(a=>a.BaseId==3574&&a.IsCasting&&a.CastingSpellId==3247);
            if(hound!=null)
            {
                var endpoint=actors.FirstOrDefault(a=>a.ObjectId==hound.SpellCastInfo.TargetId&&a.BaseId==410&&a.NpcId==3234);
                if(endpoint!=null&&hound.Location.Distance2D(endpoint.Location)>1)
                {
                    var delta=endpoint.Location-hound.Location;
                    houndLine=RefreshLine(houndLine,hound.Location,(float)Math.Atan2(delta.X,delta.Z),hound.Location.Distance2D(endpoint.Location),
                        DateTime.UtcNow+hound.SpellCastInfo.RemainingCastTime+TimeSpan.FromMilliseconds(800));
                }
            }
            foreach(var electron in actors.Where(a=>a.NpcId==3239&&a.IsCasting&&a.CastingSpellId==3253))
            {
                var endpoint=actors.FirstOrDefault(a=>a.NpcId==3239&&a.ObjectId==electron.SpellCastInfo.TargetId);
                if(endpoint==null)continue; // An unresolved endpoint never becomes a guessed forward beam.
                var delta=endpoint.Location-electron.Location;
                float length=electron.Location.Distance2D(endpoint.Location);
                if(length<1)continue;
                electronLines.TryGetValue(electron.ObjectId,out var existingLine);
                electronLines[electron.ObjectId]=RefreshLine(existingLine,electron.Location,(float)Math.Atan2(delta.X,delta.Z),length,
                    DateTime.UtcNow+electron.SpellCastInfo.RemainingCastTime+TimeSpan.FromMilliseconds(800));
            }
        }
        var slabber=actors.FirstOrDefault(a=>a.BaseId==3574&&a.IsCasting&&a.CastingSpellId==3241);
        if(slabber!=null&&slabber.SpellCastInfo.CastLocation!=Vector3.Zero)
        {slabberPoint=slabber.SpellCastInfo.CastLocation;slabberUntil=DateTime.UtcNow+slabber.SpellCastInfo.RemainingCastTime+TimeSpan.FromSeconds(8);}
        CaptureEncounterMarkers(all);
        if(actors.Any(a=>a.NpcId==3233&&a.IsAlive))rouletteUntil=DateTime.UtcNow.AddSeconds(6);
        CaptureJawsLaunches(d);
        var beam=actors.FirstOrDefault(a=>a.NpcId==3240&&a.IsCasting&&a.CastingSpellId==3297);
        if(beam!=null)cloudBeam=RefreshLine(cloudBeam,beam.Location,beam.Heading,60+beam.CombatReach,DateTime.UtcNow+beam.SpellCastInfo.RemainingCastTime+TimeSpan.FromMilliseconds(1250));
        int progress=(int)d.GetUI8A;
        if(progress!=lastProgress){lastProgress=progress;anchorId=0;Log("Native objective change UI8A="+progress);}
        // Map states often change between five-second snapshots. Keep transition evidence
        // for cleansing pads/towers without assuming that visibility means activation.
        var maps=string.Join(';',d.MapEffects.Select(m=>$"{m.ID}:{m.State}"));
        if(maps!=mapEvidence){mapEvidence=maps;Log("Map evidence: "+maps);}
        if(DateTime.UtcNow<nextEvidence)return;
        nextEvidence=DateTime.UtcNow.AddSeconds(5);
        // The first live director placed the seven objectives in slots2..8; slots0/1
        // are auxiliary fields. Capture all nine instead of silently omitting the finale.
        var todo=Enumerable.Range(0,9).Select(i=>$"{i}:{d.GetTodoArgs(i).Item1}/{d.GetTodoArgs(i).Item2}");
        Log($"Objective evidence: UI8A={progress} ended={d.InstanceEnded} subzone={WorldManager.SubZoneId} todo=[{string.Join(',',todo)}] player={Core.Player.Location} combat={Core.Player.InCombat} job={Core.Player.CurrentJob}.");
        // Party-wide vulnerability distinguishes an unattended personal dodge from
        // an add's raidwide failure; boss reach constrains the moving Devour overlap.
        // Supported wrapper properties avoid introducing guessed memory offsets.
        Log("Party evidence: "+string.Join(';',party.Select(p=>$"{p.ObjectId:X}:{p.Location}:alive={p.IsAlive}:combat={p.InCombat}:target={p.CurrentTargetId:X}:hp={p.CurrentHealth}/{p.MaxHealth}:vuln={p.HasAura(202)}")));
        // Include invisible helpers and event objects: targetable-boss-only capture hid crucial
        // shelter/floor state during Labyrinth. Persist scalar strings, not native wrappers.
        Log("Actor evidence: "+string.Join(';',actors.Select(a=>$"{a.ObjectId:X}/base{a.BaseId}/npc{a.NpcId}:{a.Location}:alive={a.IsAlive}:visible={a.IsVisible}:targetable={a.IsTargetable}:action={a.CastingSpellId}:heading={a.Heading:F3}:target={a.CurrentTargetId:X}:reach={a.CombatReach:F2}")));
        Log("Object evidence: "+string.Join(';',GameObjectManager.GetObjectsOfType<EventObject>().Where(o=>o.IsValid&&o.Location.Distance(Core.Player.Location)<70)
            .Select(o=>$"{o.ObjectId:X}/npc{o.NpcId}:{o.Location}:visible={o.IsVisible}:targetable={o.IsTargetable}")));
        Log("Aura evidence: "+string.Join(';',Core.Player.Auras.Select(a=>$"{a.Id}:{a.Name}:{a.TimespanLeft.TotalSeconds:F1}:value={a.Value}")));
    }

    private void CaptureEncounterMarkers(BattleCharacter[] all)
    {
        if(WorldManager.SubZoneId==1442)
        {
            var shared=raidPlayers.Where(p=>p.VfxContainer is { IsValid:true } v&&
                v.LockOns.Any(x=>x!=null&&x.IsValid&&x.Id==318)).ToArray();
            if(shared.Length==1)
            {
                if(thunderTarget!=shared[0].ObjectId)Log($"Breath of Thunder stack marker318 target={shared[0].ObjectId:X}");
                thunderTarget=shared[0].ObjectId;
                // Marker removal and damage can arrive on adjacent pulses. Keep
                // a short1.5s fence, without guessing a cast from a stale boss target.
                thunderUntil=DateTime.UtcNow.AddSeconds(1.5);
            }
            else if(shared.Length>1)thunderTarget=0;
        }
        // Level150 Death killed Tired13:45:40 after a cast with no target ID and
        // stale CastLocation. Capture installed wrapper lock-ons/tethers across
        // all nearby players, not just our party, before choosing an unsafe region.
        // Dragon's uncast knock-up hit seven party members13:51:30; capture its
        // markers too so a marked player's obligation can be distinguished from
        // collateral damage. IDs are raw VFX, not assumed network headmarker IDs.
        // Keep native reads on the bot thread and only retain scalar text.
        var level=actors.FirstOrDefault(a=>a.NpcId==3231&&a.IsCasting&&a.CastingSpellId is 3275 or 3277);
        if(level!=null)
        {
            if(levelAction!=level.CastingSpellId||DateTime.UtcNow>=levelUntil)levelTarget=0;
            levelAction=level.CastingSpellId;
            // Impact can trail the parent cast; Flare Suppuration appeared about
            // one second later. The1.5s fence is independent of the longer logs.
            levelUntil=DateTime.UtcNow+level.SpellCastInfo.RemainingCastTime+TimeSpan.FromSeconds(1.5);
            markerCaptureUntil=levelUntil.AddSeconds(1.5);
            uint marker=levelAction==3275?44u:45u;
            var marked=all.Append(Core.Player).DistinctBy(a=>a.ObjectId).Where(a=>!a.IsNpc&&
                a.Location.Distance2D(Core.Player.Location)<65&&a.VfxContainer is { IsValid:true } v&&
                v.LockOns.Any(x=>x!=null&&x.IsValid&&x.Id==marker)).ToArray();
            // Reject ambiguous evidence rather than choosing whichever actor is
            // enumerated first. Retain no native wrapper beyond this bot pulse.
            if(marked.Length==1&&levelTarget!=marked[0].ObjectId)
            {levelTarget=marked[0].ObjectId;Log($"Level circle action={levelAction} marker={marker} target={levelTarget:X}");}
            else if(marked.Length>1)levelTarget=0;
        }
        if(WorldManager.SubZoneId==1442&&actors.Any(a=>a.NpcId==3227&&a.IsAlive&&a.InCombat))
            markerCaptureUntil=DateTime.UtcNow.AddSeconds(1);
        if(WorldManager.SubZoneId is not (1440 or 1442)||DateTime.UtcNow>=markerCaptureUntil)return;
        if(DateTime.UtcNow<nextMarkerCapture)return;
        nextMarkerCapture=DateTime.UtcNow.AddMilliseconds(500);
        var rows=new List<string>();
        foreach(var a in all.Append(Core.Player).DistinctBy(a=>a.ObjectId).Where(a=>
            (!a.IsNpc||a.NpcId is 3231 or 3227)&&a.Location.Distance2D(Core.Player.Location)<65))
        {
            var v=a.VfxContainer;
            if(v==null||!v.IsValid)continue;
            string locks=string.Join(',',v.LockOns.Where(x=>x!=null&&x.IsValid).Select(x=>$"{x.Id}@{x.Projection.Center}"));
            // Empty native slots are always present; logging them made every
            // moving actor look like a signal change and hid the actual markers.
            string tethers=string.Join(',',(v.Tethers??[]).Where(x=>x.Id!=0&&x.TargetId!=0&&x.TargetId!=0xE0000000).Select(x=>$"{x.Id}:{x.TargetId:X}:{x.Progress}"));
            if(locks.Length>0||tethers.Length>0)rows.Add($"{a.ObjectId:X}@{a.Location}:locks=[{locks}]:tethers=[{tethers}]");
        }
        string evidence=string.Join(';',rows);
        if(evidence==markerEvidence)return;
        markerEvidence=evidence;
        Log("Encounter marker evidence: "+evidence);
    }

    private void CaptureJawsLaunches(InstanceContentDirector d)
    {
        if(d.GetTodoArgs(5).Item1!=1)return;
        var assigned=actors.FirstOrDefault(a=>a.BaseId==3492&&a.NpcId==atomosLane);
        if(assigned!=null)jawsCenter=assigned.Location;
        if(d.GetTodoArgs(6).Item1==1)
        {
            // At19:35 allies left the middle floor near(0,95.35,-21), rose above
            // Y110, and RB tried to path straight to them from(-4,95,-9). Record
            // their last grounded point on our own platform before that launch.
            // This is a per-run witness, not an assumed exit shared by all lanes.
            if(jawsCenter is not Vector3 center||jawsExit!=null)return;
            foreach(var p in party.Where(p=>p.IsAlive))
            {
                if(p.Y>94.8f&&p.Y<95.7f&&p.Location.Distance2D(center)<25&&p.Z<center.Z-5)
                    jawsExitGround[p.ObjectId]=new JumpWitness(p.Location,DateTime.UtcNow,false);
                else if(p.Y>100&&jawsExitGround.TryGetValue(p.ObjectId,out var ground)&&
                    DateTime.UtcNow-ground.Seen<TimeSpan.FromSeconds(5)&&p.Z<ground.Point.Z-2)
                {jawsExit=ground.Point;Log($"Jaws witnessed departure: lane={atomosLane} takeoff={ground.Point} ally={p.ObjectId:X}");break;}
            }
            return;
        }
        var lanes=actors.Where(a=>a.IsAlive&&a.BaseId==3492&&a.NpcId is >=3380 and <=3382).ToArray();
        foreach(var p in party.Where(p=>p.IsAlive))
        {
            // These floor bands are live-measured: entrance94.75, lower ring90.25,
            // platforms95.25. Capture scalar positions only, on the bot thread.
            bool entry=p.Z>89&&p.Z<97&&Math.Abs(p.X)<12&&p.Y>94.5f&&p.Y<95.6f;
            bool lower=p.Z> -35&&p.Z<80&&Math.Abs(p.X)<65&&p.Y>90&&p.Y<90.6f;
            if(entry||lower)
            {jawsGround[p.ObjectId]=new JumpWitness(new Vector3(p.X,entry?94.75f:90.25f,p.Z),DateTime.UtcNow,entry);continue;}
            if(!jawsGround.TryGetValue(p.ObjectId,out var ground)||(DateTime.UtcNow-ground.Seen).TotalSeconds>8||
                p.Y<94.8f||p.Y>95.6f||p.Z>75)continue;
            var lane=lanes.Where(a=>p.Location.Distance(a.Location)<23).OrderBy(a=>p.Location.Distance2DSqr(a.Location)).FirstOrDefault();
            if(lane==null||!ground.Entry&&ground.Point.Distance2D(lane.Location)>40)continue;
            var map=ground.Entry?jawsEntries:jawsReturns;
            if(!map.ContainsKey((int)lane.NpcId))
            {map[(int)lane.NpcId]=ground.Point;Log($"Jaws witnessed {(ground.Entry?"entry":"return")}: lane={lane.NpcId} takeoff={ground.Point} ally={p.ObjectId:X}");}
            jawsGround.Remove(p.ObjectId);
        }
    }

    private async Task<bool> JumpFromWitness(Vector3 point,string reason,float tolerance=.6f)
    {
        eligible.Clear();
        if(Poi.Current.Type==PoiType.Kill&&Poi.Current.Unit?.ObjectId==ownPoi)Poi.Clear(reason);
        if(Core.Player.IsCasting)ActionManager.StopCasting();
        if(Core.Player.Location.Distance2D(point)>tolerance)return await Move(point,tolerance,reason);
        StopTravel();SetIntent(reason);
        // The captured ally rose in place before launch. Avoid repeated input in
        // flight; lack of activation is recoverable and does not authorize leaving.
        if(DateTime.UtcNow>=nextJump&&Math.Abs(Core.Player.Y-point.Y)<.8f)
        {MovementManager.Jump();nextJump=DateTime.UtcNow.AddSeconds(3);}
        return true;
    }

    private void Notices()
    {
        if(DateTime.UtcNow<nextNotice)return;
        if(LlamaLibrary.RemoteWindows.NotificationIcLockoutWar.Instance.IsOpen)
        {
            nextNotice=DateTime.UtcNow.AddSeconds(2);
            if(SelectYesno.IsOpen){Log("Accepting sealed-area transfer; retaining party-derived lane.");SelectYesno.Yes();}
            else RaptureAtkUnitManager.GetWindowByName("_Notification")?.SendAction(2,3,0,3,0xA);
        }
        else if(LlamaLibrary.RemoteWindows.NotificationReadyCheck.Instance.IsOpen&&SelectYesno.IsOpen)
        {nextNotice=DateTime.UtcNow.AddSeconds(2);Log("Accepting ready check.");SelectYesno.Yes();}
        // Never consume a pulse merely because a notification persists after answering.
    }

    private void UpdateMechanicOverrides(uint subzone)
    {
        if(subzone is not (1440 or 1446)){ReleaseMechanicOverrides();return;}
        var plugin=DutyMechanic.Helpers.PluginHelpers.GetSideStepPlugin()?.Plugin;
        if(!ReferenceEquals(plugin,sideStep)||overrideSubzone!=subzone)
        {ReleaseMechanicOverrides();sideStep=plugin;overrideSubzone=subzone;}
        if(plugin==null||overridesAttempted)return;
        var add=plugin.GetType().GetMethod("Override",[typeof(uint)]);
        var remove=plugin.GetType().GetMethod("RemoveOverride",[typeof(uint)]);
        if(add==null||remove==null)return;
        overridesAttempted=true;
        // The second run's generic radius60 Double Vision avoid preempted the color
        // response and could not find an escape. These three actions have semantic
        // owners here; suppress only them, leaving ordinary geometry with SideStep.
        foreach(uint action in subzone==1440?new uint[]{3272,3281,3499}:new uint[]{3297})
        {
            try {if(add.Invoke(plugin,[action]) is true)ownedOverrides.Add(action);}
            catch(Exception ex){Log($"Mechanic override unavailable for {action}: {ex.GetType().Name}");}
        }
    }
    private void ReleaseMechanicOverrides()
    {
        foreach(uint action in ownedOverrides)
        {
            try {sideStep?.GetType().GetMethod("RemoveOverride",[typeof(uint)])?.Invoke(sideStep,[action]);}
            catch(Exception ex){Log($"Mechanic override cleanup for {action}: {ex.GetType().Name}");}
        }
        ownedOverrides.Clear();sideStep=null;overridesAttempted=false;overrideSubzone=0;
    }

    private void SelectTargets()
    {
        eligible.Clear();
        // Runs7–9 selected an engaged Garm from the entrance corridor and the
        // ranged routine stopped outside the Eye seal. Continue native party
        // travel until subzone1439 before assigning combat; healing still runs.
        var director=Director();
        bool enteringEye=!eyeEntered&&director!=null&&director.GetTodoArgs(2).Item1!=1;
        var tank=Core.Player.CurrentJob.ToString() is "Paladin" or "Warrior" or "DarkKnight" or "Gunbreaker";
        var ownTargets=party.Where(p=>p.IsAlive&&p.InCombat).Select(p=>p.CurrentTargetId).ToHashSet();
        var ownIds=party.Select(p=>p.ObjectId).Append(Core.Player.ObjectId).ToHashSet();
        foreach(var a in actors.Where(a=>a.IsAlive&&a.CanAttack&&a.IsTargetable&&Math.Abs(a.Y-Core.Player.Y)<8&&a.Location.Distance2D(Core.Player.Location)<55))
        {
            if(enteringEye)continue;
            // Gastric Juice supplies Mini for belly entrants; an outside helper must
            // not destroy the encounter's enabling actor merely because it is closest.
            if(a.NpcId==3237)continue;
            if(a.NpcId is >=3380 and <=3382 && a.NpcId!=atomosLane)continue;
            // Non-tanks may assist engaged party targets/threats, never initiate because a mob
            // happens to be in range. Tanks can engage trash with three nearby companions.
            bool assist=a.InCombat&&(ownTargets.Contains(a.ObjectId)||ownIds.Contains(a.CurrentTargetId)||party.Count(p=>p.IsAlive&&p.InCombat&&p.Location.Distance(a.Location)<30)>=3);
            bool tankPull=tank&&a.NpcId is not (3231 or 3227 or 3234 or 3240)&&party.Count(p=>p.IsAlive&&p.Location.Distance(a.Location)<25)>=3;
            if(assist||tankPull)eligible.Add(a.ObjectId);
        }
        if(Poi.Current.Type==PoiType.Kill&&Poi.Current.Unit is BattleCharacter old&&!eligible.Contains(old.ObjectId))Poi.Clear("World of Darkness party/phase target restriction");
        if(Core.Player.CurrentTarget is BattleCharacter target&&target.CanAttack&&!eligible.Contains(target.ObjectId))Core.Player.ClearTarget();
        var preferred=provider.GetObjectsByWeight().FirstOrDefault();
        if(preferred!=null&&(Poi.Current.Type!=PoiType.Kill||Poi.Current.Unit?.ObjectId!=preferred.ObjectId))
        {preferred.Target();Poi.Current=new Poi(preferred,PoiType.Kill);ownPoi=preferred.ObjectId;}
    }

    private sealed class Targets(WorldOfDarknessRun owner):ITargetingProvider
    {
        public List<BattleCharacter> GetObjectsByWeight()=>!owner.active?[]:
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true,false)
                .Where(a=>a.IsValid&&a.IsAlive&&a.CanAttack&&a.IsTargetable&&owner.eligible.Contains(a.ObjectId))
                .OrderByDescending(a=>a.NpcId is 3233 or 3298 or 3228 or 3301 or 3305 or 3222 or 3223 or 3224 or 3225 or 3238 or 3241 or 3302 or 3303?100:0)
                .ThenBy(a=>a.Location.Distance2DSqr(Core.Player.Location)).ToList();
    }

    private async Task<bool> Hold(Vector3 destination,float tolerance,string reason)
    {
        HoldMovement(reason);
        if(Core.Player.Location.Distance(destination)<=tolerance){StopTravel();return false;}
        if(Core.Player.IsCasting)ActionManager.StopCasting();
        return await Move(destination,tolerance,reason);
    }
    private void HoldMovement(string reason)
    {holding=true;CapabilityManager.Update(movement,CapabilityFlags.Movement,2000,reason);SetIntent(reason);}
    private void ReleaseHold()
    {if(holding)CapabilityManager.Clear(movement,CapabilityFlags.Movement,"World of Darkness hold ended");holding=false;}
    private void ReleaseFacing()
    {
        if(!faceOwned)return;
        CapabilityManager.Clear(facing,CapabilityFlags.Facing,"Mortal Gaze ended");
        GameSettingsManager.FaceTargetOnAction=previousAutoFace;faceOwned=false;gazeUntil=DateTime.MinValue;
    }
    private async Task<bool> Move(Vector3 destination,float tolerance,string reason)
    {
        if(DateTime.UtcNow<nextNavigation)return false;
        SetIntent(reason);
        moving=await CommonTasks.MoveAndStop(new MoveToParameters(destination){DistanceTolerance=tolerance,UseMount=false},tolerance,true,reason);
        // A failed service path is recoverable: reacquire party state after a bounded delay,
        // rather than hammering the service, stopping RB, or abandoning the public duty.
        if(!moving&&Core.Player.Location.Distance(destination)>tolerance)nextNavigation=DateTime.UtcNow.AddSeconds(2);
        return moving;
    }
    private void StopTravel()
    {if(!moving)return;moving=false;if(AvoidanceManager.IsRunningOutOfAvoid)return;Navigator.Clear();Navigator.PlayerMover.MoveStop();}
    private void SetIntent(string value){if(value==intent)return;intent=value;Log(value);}
}
