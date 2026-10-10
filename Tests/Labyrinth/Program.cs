using System.Numerics;
using System.Text.Json;
using P=DutyMechanic.Dungeons.LabyrinthPlanner;
using R=DutyMechanic.Dungeons.LabyrinthRoutes;

// Assert gameplay invariants and replay the actual cast/actor lifecycle. The replay deliberately
// cannot claim navigation, live scheduler, or A/C tank validation from the manual B/SCH baseline.
int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
RoutineLeaseScenarios.Verify(Check);
// Reproduce noisy distance samples around the former follow cutoff, then arrival
// and renewed separation. Healing should regain movement without immediate reacquisition.
bool fireFollowing=false;
foreach(float distance in new[]{13f,11.9f,12.1f,11.8f,8f,5.1f})
{
    fireFollowing=P.ContinueFireAssist(distance,fireFollowing);
    Check(fireFollowing,"Fire follow stays continuous until arrival");
}
fireFollowing=P.ContinueFireAssist(5,fireFollowing);
Check(!fireFollowing,"Fire arrival releases routine movement");
foreach(float distance in new[]{5.1f,8f,11.9f,12f})
{
    fireFollowing=P.ContinueFireAssist(distance,fireFollowing);
    Check(!fireFollowing,"Fire follow does not restart inside the separation threshold");
}
Check(P.ContinueFireAssist(12.1f,fireFollowing),"New party separation starts another follow");
// Forced landing support cannot become a guessed permanent alliance or cross-lane route.
Check(P.SupportForcedLanding(2,true,-1,new(198.46f,245.177f),50.68f),"Unknown sealed arrival supports the physical landing pad");
Check(!P.SupportForcedLanding(2,false,-1,new(198.46f,245.177f),50.68f),"Ordinary entry still requires own-party evidence");
// Known B/C can contribute to physical A after lockout without rewriting their identity.
Check(P.SupportForcedLanding(2,true,1,new(198.46f,245.177f),50.68f),"Known B supports its forced physical landing");
Check(P.SupportForcedLanding(2,true,2,new(198.46f,245.177f),50.68f),"Known C supports its forced physical landing");
Check(!P.SupportForcedLanding(2,true,0,new(198.46f,245.177f),50.68f),"Known A retains normal role and linked-kill handling");
Check(!P.SupportForcedLanding(3,true,-1,new(198.46f,245.177f),50.68f),"Native clear releases temporary support");
Check(!P.SupportForcedLanding(2,true,-1,new(198.46f,280),50.68f),"Recovery never crosses to A from another lane");
Check(!P.SupportForcedLanding(2,true,-1,new(170,245),58),"Stair approach cannot activate forced landing support");
// Lava is a floor constraint independent of alliance or role, released for transit.
for(int i=0;i<72;i++)
{
    var direction=new Vector2(MathF.Sin(i*MathF.Tau/72),MathF.Cos(i*MathF.Tau/72));
    var lava=P.FireCenter+direction*34;
    Check(P.FireBoundary.Any(h=>h.Contains(lava)),"Outer fire floor is forbidden around the entire room");
    Check(!P.FireBoundary.Any(h=>h.Contains(P.FireRecovery(lava))),"Lava recovery finishes inside safe floor");
    Check(!P.FireBoundary.Any(h=>h.Contains(P.FireCenter+direction*30)),"Interior remains available to all roles");
}
Check(!P.FireBoundaryActive(4,true,P.FireCenter+new Vector2(0,50)),"Entrance combat must not activate the finite lava donut");
Check(P.FireBoundaryActive(4,true,P.FireCenter),"Combat enables fire boundary");
Check(!P.FireBoundaryActive(5,true,P.FireCenter)&&!P.FireBoundaryActive(4,false,P.FireCenter),"Clear and pre-pull travel release fire boundary");
Check(!P.FireBoundaryActive(4,true,P.BehemothCenter),"Remote combat cannot trap transit in the fire room");
P.Actor A(uint id,uint b,Vector2 p,bool alive=true,bool attack=true,uint action=0,double end=0)
    =>new(id,b,p,650,0,b==P.Behemoth?8.7f:1,alive,attack,0,action,end);
Check(P.Phase(null)==-1&&P.Phase(2)==-1,"Missing/unknown checkpoints must fail closed");
Check(P.Phase(3)==1&&P.Phase(7)==2&&P.Phase(127)==6&&P.Phase(255)==7,"Confirmed director progression");
// The C-to-B recovery misidentification came from the common final approach, outside
// the actual pad. Travel must also keep followers behind a group while allowing tanks to lead.
var finalGroup=Enumerable.Range(1,4).Select(i=>new P.Ally((uint)i,P.FinalPads[2],650,P.Role.Ranged,true)).ToArray();
Check(P.InferAlliance(finalGroup,6)==2,"Four own allies occupying C identify C");
Check(P.InferAlliance(finalGroup.Take(3).ToArray(),6)==-1,"Three passing allies cannot identify final alliance");
Check(P.InferAlliance(finalGroup.Select(a=>a with{P=P.FinalPads[1]+new Vector2(0,10)}).ToArray(),6)==-1,"Shared final approach is not B-pad occupancy");
var travelGroup=finalGroup.Select(a=>a with{P=new Vector2(10,0),Y=45}).ToArray();
Check(P.CanApproach(Vector2.Zero,45,new(100,0),P.Role.Healer,99,travelGroup),"Follower catches up to own group ahead");
Check(!P.CanApproach(new(20,0),45,new(100,0),P.Role.Healer,99,travelGroup),"Follower does not run ahead of own group");
travelGroup[0]=travelGroup[0] with{Role=P.Role.Healer};
Check(P.CanApproach(new(20,0),45,new(100,0),P.Role.Tank,99,travelGroup),"Tank can lead a nearby group with healer");
Check(!P.CanApproach(new(60,0),45,new(100,0),P.Role.Tank,99,travelGroup),"Tank cannot leave distant group behind");
var own=A(1,P.Atomos,P.AtomosPads[1],false);
// Regression for the October 8 landing delay and movement-triggered early pull.
var entryGroup=Enumerable.Range(1,3).Select(i=>new P.Ally((uint)i,new(170+i,245),58,P.Role.Ranged,true)).ToArray();
var descent=P.AtomosEntry(new(119,267),62,-1,P.Role.Healer,99,entryGroup,false);
Check(descent.P.X==171&&descent.Y==58,"Unknown alliance follows actual clustered descent without guessing a pad");
Check(P.AtomosEntry(new(190,245),51,0,P.Role.Healer,99,entryGroup,false).P.X==190,"Pad mechanic cannot overtake a trailing group");
Check(P.AtomosEntry(new(119,267),62,-1,P.Role.Healer,99,entryGroup.Take(2).ToArray(),false).P.X==119,"Two allies cannot authorize descent");
Check(P.AtomosEntry(new(190,245),51,0,P.Role.Healer,99,[],true).P==P.AtomosPads[0],"Combat releases entry gate despite missing allies");
Check(P.AtomosEntry(new(119,267),62,-1,P.Role.Healer,99,entryGroup.Select((a,i)=>a with{P=new(171,245+i*35)}).ToArray(),false).P.X==119,"Scattered lanes cannot authorize a guessed descent");
var padGroup=entryGroup.Select(a=>a with{P=P.AtomosPads[0],Y=51}).ToArray();
Check(P.AtomosEntry(new(190,245),51,0,P.Role.Healer,99,padGroup,false).P==P.AtomosPads[0],"Three own allies on pad permit joining them");
Check(P.AtomosEntry(new(190,245),51,0,P.Role.Healer,99,padGroup,false).Tolerance==2.3f,"Fourth pad member enters activation circle instead of stopping at following distance");
padGroup[0]=padGroup[0] with{Role=P.Role.Healer};
Check(P.AtomosEntry(new(190,245),51,0,P.Role.Tank,99,padGroup,false).P==P.AtomosPads[0],"Tank retains group-ready leading behavior");
// Scope the observed middle-room hold to its live pack, not the same-named Atomos adds.
var greaterDemon=A(80,2400,P.GreaterDemonPlatform);
// Opening-room regression: preserve role, room and lifecycle boundaries so a ranged
// floor correction cannot seize pre-pull movement or a later encounter's Valefor.
var openingValefor=A(79,2398,P.OpeningPlatform);
Check(P.HoldOpeningPlatform(0,new(-370.147f,283.217f),true,P.Role.Ranged,[openingValefor]),"SMN low-floor capture enters measured opening platform");
Check(P.HoldOpeningPlatform(0,P.OpeningPlatform,true,P.Role.Healer,[openingValefor with{Base=2392}]),"Surviving homunculus retains opening support");
Check(!P.HoldOpeningPlatform(0,P.OpeningPlatform,false,P.Role.Ranged,[openingValefor]),"Opening support does not pre-pull");
Check(!P.HoldOpeningPlatform(0,P.OpeningPlatform,true,P.Role.Tank,[openingValefor]),"Opening support preserves tank engagement");
Check(!P.HoldOpeningPlatform(0,P.OpeningPlatform,true,P.Role.Melee,[openingValefor]),"Opening support preserves melee engagement");
Check(!P.HoldOpeningPlatform(0,P.OpeningPlatform,true,P.Role.Ranged,[openingValefor with{Alive=false}]),"Opening clear releases movement");
Check(!P.HoldOpeningPlatform(0,P.GreaterDemonPlatform,true,P.Role.Ranged,[openingValefor]),"Opening support cannot pull from next room");
Check(!P.HoldOpeningPlatform(2,P.OpeningPlatform,true,P.Role.Ranged,[openingValefor with{Base=2404}]),"Atomos Valefor cannot activate opening support");
Check(P.HoldGreaterDemonPlatform(0,P.GreaterDemonPlatform,true,P.Role.Healer,[greaterDemon]),"Live middle-room pack retains the raised platform");
Check(!P.HoldGreaterDemonPlatform(0,P.GreaterDemonPlatform,true,P.Role.Healer,[greaterDemon with{Alive=false}]),"Cleared pack releases platform travel");
Check(!P.HoldGreaterDemonPlatform(2,P.GreaterDemonPlatform,true,P.Role.Healer,[greaterDemon with{Base=2405}]),"Atomos demons cannot activate the Pools hold");
Check(!P.HoldGreaterDemonPlatform(0,P.GreaterDemonPlatform,false,P.Role.Healer,[greaterDemon]),"Pre-pull travel is not seized by the combat platform hold");
// Third-room actors share phase zero with Greater Demon; scope both room and lifecycle
// and avoid imposing a support-only observation on unverified melee/tank behavior.
// A healer-capture anchor cannot prevent melee engagement or tank pickup.
Check(!P.HoldGreaterDemonPlatform(0,P.GreaterDemonPlatform,true,P.Role.Tank,[greaterDemon]),"Greater Demon support hold leaves tank engagement free");
Check(!P.HoldGreaterDemonPlatform(0,P.GreaterDemonPlatform,true,P.Role.Melee,[greaterDemon]),"Greater Demon support hold leaves melee engagement free");
Check(P.HoldGreaterDemonPlatform(0,P.GreaterDemonPlatform,true,P.Role.Ranged,[greaterDemon]),"Greater Demon ranged support retains the raised floor");
var dira=greaterDemon with{Base=2401,P=P.DiraPlatform};
Check(P.HoldDiraPlatform(0,P.DiraPlatform,true,P.Role.Healer,[dira]),"SCH enters the Dira support platform");
Check(P.HoldDiraPlatform(0,P.DiraPlatform,true,P.Role.Ranged,[dira with{Base=2402}]),"Surviving eyes retain the platform hold");
Check(!P.HoldDiraPlatform(0,P.DiraPlatform,true,P.Role.Tank,[dira]),"Support capture does not dictate tank engagement");
Check(!P.HoldDiraPlatform(0,P.GreaterDemonPlatform,true,P.Role.Healer,[dira]),"Dira hold cannot pull from the preceding room");
Check(!P.HoldDiraPlatform(0,P.DiraPlatform,true,P.Role.Healer,[dira with{Alive=false}]),"Dead third-room pack releases hold");
Check(!P.HoldDiraPlatform(1,P.DiraPlatform,true,P.Role.Healer,[dira]),"Bone checkpoint releases Dira positioning");
var linked=A(2,P.Atomos,P.AtomosPads[2]);
Check(P.LinkedAtomosAlive([own,linked],1),"Own Atomos death cannot release B's pad");
Check(P.LinkedAtomosAlive([own],1),"Missing linked boss is not a clear");
Check(!P.LinkedAtomosAlive([own,linked with{Alive=false}],1),"Observed linked death releases pad");
// Replay the observed death -> corpse disappearance -> possible respawn lifecycle.
bool rememberedLinkedDeath=!P.LinkedAtomosAlive([linked with{Alive=false}],1);
Check(!P.LinkedAtomosAlive([],1,rememberedLinkedDeath),"Corpse despawn preserves the observed linked clear");
Check(P.LinkedAtomosAlive([linked],1,rememberedLinkedDeath),"A live respawn overrides remembered death");
Check(P.LinkedAtomosAlive([],1,false),"A new attempt cannot inherit an unobserved clear");
var roster=Enumerable.Range(0,8).Select(i=>new P.Ally((uint)i,new(215,280),51,
    i==0?P.Role.Tank:i<3?P.Role.Healer:i<6?P.Role.Ranged:P.Role.Melee,true)).ToArray();
Check(roster.Count(a=>P.PadSupport(a.Id,roster))==4,"Exactly four support assignments");
Check(!P.PadSupport(0,roster)&&P.PadSupport(1,roster)&&P.PadSupport(2,roster),"Tank stays free and healers hold");
Check(P.InferAlliance(roster,2)==1,"Own-party B lane consensus");
Check(P.InferAlliance(roster.Take(2).ToArray(),2)==-1,"Two members cannot identify alliance");
var boss=A(9,P.Phlegethon,P.FinalCenter,action:1730,end:7);
// A selected but unpulled boss must not bypass initial alliance assembly; actual combat,
// casting and completion must release staging so it cannot mask a live Flare or a clear.
Check(P.FinalStaging(boss with{Action=0},false),"Idle final boss requires starting-pad staging");
Check(P.FinalStaging(null,false),"Missing boss cannot authorize an early pull");
Check(!P.FinalStaging(boss with{Action=0,InCombat=true},false),"Another player's pull releases staging");
Check(!P.FinalStaging(boss,false),"Ancient Flare cannot be mistaken for pre-pull staging");
Check(!P.FinalStaging(boss with{Action=0},true),"Player combat releases pre-pull staging");
Check(!P.FinalStaging(boss with{Action=0,Alive=false},false),"Dead final boss permits completion");
var flare=new P.FlareState();flare.Update(0,boss,[]);
Check(flare.Holding,"Flare starts retreat immediately");
flare.Update(8,boss with{Action=0},[]);
Check(flare.Holding,"Boss still targetable after cast cannot release pad");
var giant=A(10,P.FinalGiant,P.FinalPads[1]);
flare.Update(11,boss with{Action=0,Attackable=false},[giant]);
flare.Update(13,boss with{Action=0},[giant]);
Check(flare.Holding,"Boss return cannot bypass living own giant");
flare.Update(15,boss with{Action=0},[giant with{Alive=false}]);
Check(!flare.Holding,"Boss return plus resolved own giant releases hold");
flare.Update(20,boss with{Finish=27},[]);flare.Reset();
Check(!flare.Holding,"Wipe clears the old cast lifecycle");
var assignments=new Dictionary<uint,int>{{10,1},{11,0}};
Check(P.Eligible(giant,6,1,false,true,assignments),"Own giant attackable while holding");
Check(!P.Eligible(giant with{Id=11},6,1,false,true,assignments),"Other alliance giant rejected");
Check(!P.Eligible(boss,6,1,false,true,assignments),"Boss cannot pull player off Flare pad");
Check(!P.Eligible(A(5,P.Thanatos,new(440,280)),3,1,false,false,assignments),"Unbuffed Thanatos immune");
Check(P.Eligible(A(5,P.Thanatos,new(440,280)),3,1,true,false,assignments),"Actual aura enables Thanatos");
Check(!P.Eligible(A(12,P.Claw,P.FinalPads[1]),6,-1,false,false,assignments),"Unknown alliance cannot chase claws");
Check(P.Priority(P.Vassago,4,P.Role.Tank,false,10)>P.Priority(2408,4,P.Role.Tank,false,100),"Tank holds Vassago instead of dragging it after a balloon");
Check(P.Priority(P.Claw,6,P.Role.Healer,false,200)>P.Priority(P.Phlegethon,6,P.Role.Healer,false,1),"Healer prioritizes own rescuable claw");
Check(P.Priority(P.Behemoth,5,P.Role.Tank,true,1)>P.Priority(2357,5,P.Role.Tank,true,50),"Main tank retains Behemoth when giant spawns");
Check(P.Priority(2357,5,P.Role.Tank,false,50)>P.Priority(P.Behemoth,5,P.Role.Tank,false,1),"Off tank picks up the giant");
Check(P.Priority(P.Atomos,2,P.Role.Melee,false,1)>P.Priority(2406,2,P.Role.Melee,false,50),"Melee focus assigned Atomos while support handles adds");
var behemoth=A(20,P.Behemoth,P.BehemothCenter);
var rock=A(21,P.Comet,P.BehemothCenter+new Vector2(14,0));
Check(P.Shelter(P.BehemothCenter+new Vector2(15,10),behemoth,[rock],[]) is { } shelter&&shelter.X>rock.P.X,"Shelter lies BEHIND the rock");
Check(P.Shelter(P.BehemothCenter,behemoth,[rock with{Alive=false}],[])==null,"Destroyed comet cannot shelter");
Check(P.Shelter(P.BehemothCenter,behemoth,[rock with{P=P.BehemothCenter+new Vector2(5,0)}],[])==null,"Comet in boss hitbox rejected");
// Captured no-shelter death: a live, non-overlapping comet missed only the preferred
// clearance margin. Prefer roomy shelters, but do not idle exposed when only this remains.
var tightRock=rock with{P=P.BehemothCenter+new Vector2(11.41f,0),Radius=2.4f};
var tightShelter=P.Shelter(tightRock.P,behemoth,[tightRock],[]);
Check(tightShelter is {} ts&&ts.X>tightRock.P.X,"Non-overlapping comet is fallback when preferred margin is unavailable");
Check(P.Shelter(tightRock.P,behemoth,[tightRock,rock],[])==P.Shelter(tightRock.P,behemoth,[rock],[]),"Preferred clearance wins over nearer fallback");
Check(P.Shelter(tightRock.P,behemoth,[tightRock with{Alive=false}],[])==null,"Destroyed fallback is never selected");
Check(P.Shelter(tightRock.P,behemoth,[tightRock with{P=P.BehemothCenter+new Vector2(11,0)}],[])==null,"Fallback cannot overlap boss hitbox");
Check(P.Shelter(tightRock.P,behemoth,[tightRock],[new P.Hazard(99,1789,tightRock.P,0,5,P.Shape.Circle,8.9f)])==null,"Fallback retains timed hazard rejection");
var blast=new P.Hazard(30,1789,rock.P,0,5,P.Shape.Circle,8.9f);
Check(P.Shelter(P.BehemothCenter,behemoth,[rock],[blast])==null,"Earlier bomb damage invalidates an otherwise valid shelter");
// Captured failure: a meteorGiant's untimed facing prediction ejected an already sheltered
// player at Meteor impact. The exception must not disable real timed damage or bounds.
var meteorGiant=A(40,2357,rock.P);
var predicted=new P.Hazard(40,0,rock.P,0,double.PositiveInfinity,P.Shape.Cone,15.5f,0,MathF.PI/3);
Check(P.MeteorHazards([predicted,blast,P.FinalNorthBoundary],[meteorGiant]).SequenceEqual([blast,P.FinalNorthBoundary]),"Meteor preserves timed damage and boundaries while suppressing meteorGiant facing prediction");
Check(P.MeteorHazards([predicted with {Action=1789,Finish=5}],[meteorGiant]).Length==1,"Actual timed meteorGiant damage stays active during Meteor");
Check(P.MeteorHazards([predicted],[meteorGiant with {Base=P.Bone}]).Length==1,"Meteor exception cannot suppress another actor's frontal");
Check(P.Shelter(P.BehemothCenter,behemoth,[rock,meteorGiant],P.MeteorHazards([predicted],[meteorGiant]))!=null,"An intact comet remains usable when only predicted frontal overlaps");
var cone=P.Cast(A(31,2361,new(0,0),action:1736,end:3));
Check(cone.Contains(new(0,20))&&!cone.Contains(new(20,0))&&!cone.Contains(new(0,-20)),"Helper cone heading and back safe side");
var ring=P.Cast(A(32,2361,new(0,0),action:1739,end:3));
Check(!ring.Contains(new(0,10))&&ring.Contains(new(0,15))&&!ring.Contains(new(0,-15)),"Half-ring inner gap and rear safe side preserved");
Check(P.FinalNorthBoundary.Contains(P.FinalCenter-new Vector2(0,40)),"Final apron must not invent walkable northern floor");
Check(P.FinalPads.All(p=>!P.FinalNorthBoundary.Contains(p)),"All three Flare pads remain inside the real apron footprint");
Check(P.OnBonePlatform(P.BoneCenter)&&P.OnBonePlatform(P.BoneCenter+new Vector2(0,18.02f))&&!P.OnBonePlatform(P.BoneCenter+new Vector2(0,10)),"Common Bone platform islands exclude poison gap");
Vector3[] path=[new(0,0,0),new(100,0,0)];
Vector3[] group=[new(10,0,0),new(10,0,1),new(10,0,-1),new(90,0,0)];
Check(R.Tether(new(12,0,0),group,path,false)==null,"Follower must not run ahead of median party because one player sprints");
Check(R.Tether(new(12,0,0),group,path,true) is {} lead&&lead.X<=20,"Tank leads within bounded tether");
Check(R.Project(new(-110,650,240),R.Drop).Distance>500,"Final lower arena cannot be joined to hub by nearest-XZ path");
// Protect measured floor topology: sparse replacements previously cut through ramps and
// route generators must never connect a transport's source and destination with a mesh edge.
foreach(var measured in new[]{R.Bone,R.FireApproach,R.Drop,R.Behemoth,R.AtomosBDescent,R.AtomosApproach,R.Atomos(0),R.Atomos(1),R.Atomos(2)})
    Check(measured.Zip(measured.Skip(1)).All(pair=>Vector3.Distance(pair.First,pair.Second)<10),"Measured terrain routes contain no sparse chord or transport jump");
Check(R.Drop.All(p=>p.Y<100)&&R.Drop.Last().Z>295,"Final drop route ends on the upper trigger floor");
Check(Math.Abs(R.Project(new(-110.46f,45.51234f,308.49887f),R.FireApproach).Point.Y-45.51234f)<.2f,"Hub route preserves the flat floor before the ramp");
var hub=new Vector3(-111.193504f,45.405502f,317.33838f);
Check(Vector3.Distance(hub,R.ApproachStep(hub,R.FireApproach))<12,"Party beyond Fire transition cannot skip 247 yalms of approach");
Check(R.ApproachStep(hub,R.FireApproach).Z>300,"Hub recovery first follows the nearby ramp instead of the remote trigger");
// The 20:24 stall requested Y57.3 over a measured Y56.03 plateau before the next ramp.
Check(Math.Abs(R.Project(new(-110,56.128265f,-286.01724f),R.Behemoth).Point.Y-56.1f)<.2f,"Behemoth approach preserves the lower plateau floor");
// The failed B-lane chord put movement below the ramp; test the consumer route as well
// as generator spacing so a correct capture array cannot be left unused accidentally.
Check(Math.Abs(R.Project(new(177.09975f,57.413685f,279.0562f),R.Atomos(1)).Point.Y-57.413685f)<.2f,"B approach follows the observed descent floor");
Check(R.Atomos(0).Last().Z<250&&R.Atomos(2).Last().Z>310,"B terrain correction preserves distinct A/C destinations");
Check(R.Atomos(-1).Last().X<125&&R.Atomos(-1).Last().Y>62,"Unknown alliance waits on the upper landing rather than guessing a descent");
// Regression for the transferred C member exiting physical A at 20:20 UTC.
var departure=new R.DepartureLane();
Check(departure.Resolve(3,new(214,51,245),2)==0,"Departure follows physically occupied A after a C lockout transfer");
Check(departure.Resolve(3,new(270,51,263),2)==0,"Crossing toward the merge cannot relabel the departure corridor");
Check(departure.Resolve(3,new(285,52,280),2)==2,"Assigned routing resumes at the common corridor");
Check(departure.Resolve(3,new(214,51,315),0)==2,"A later physical C departure does not retain the old lane");
// The failed sparse side routes descended before the staircase. Preserve the captured
// intermediate plateau and require a continuous join across the C landing connector.
foreach(var lane in new[]{0,2})
    Check(R.Atomos(lane).Where(p=>p.X>=155&&p.X<=165).All(p=>p.Y>58)&&R.Atomos(lane).Any(p=>p.X>=155&&p.X<=165),"Side descent retains its observed intermediate plateau");
Check(Math.Abs(R.Project(new(-26.99965f,45.3232f,282.08487f),R.Atomos(1)).Point.Y-45.3232f)<.2f,"Shared eastern corridor retains its lower plateau");

if(args.Length>0)
{
    int frames=0,allianceVotes=0,detectedCasts=0,phase=-1;double firstHold=0,lastHold=0,release=0;
    var replay=new P.FlareState();var groups=new Dictionary<uint,int>();
    // The passive recorder may still own its append handle after duty exit. Shared reads
    // preserve that session; this test never stops ATB merely to obtain an offline replay.
    using var stream=new FileStream(args[0],FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
    using var reader=new StreamReader(stream);
    while(reader.ReadLine() is {} line)
    {
        using var doc=JsonDocument.Parse(line);var r=doc.RootElement;
        if(!r.TryGetProperty("kind",out var kind))continue;
        if(kind.GetString()=="environment")
        {
            if(r.TryGetProperty("director",out var d)&&d.ValueKind==JsonValueKind.Object&&d.GetProperty("DungeonId").GetInt32()==30001)
                phase=P.Phase(d.GetProperty("progress")[0].GetInt32());
            continue;
        }
        if(kind.GetString()!="frame"||r.GetProperty("zone").GetInt32()!=174)continue;
        frames++;
        double t=DateTime.Parse(r.GetProperty("utc").GetString()!,null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime().Ticks/(double)TimeSpan.TicksPerSecond;
        var actors=r.GetProperty("actors").EnumerateArray().Where(a=>a.TryGetProperty("BaseId",out _)).Select(a=>
        {
            var p=a.GetProperty("position");var c=a.GetProperty("cast");bool casting=c.ValueKind==JsonValueKind.Object;
            var ground=casting?c.GetProperty("location"):default;
            return new P.Actor(a.GetProperty("ObjectId").GetUInt32(),a.GetProperty("BaseId").GetUInt32(),new(p[0].GetSingle(),p[2].GetSingle()),p[1].GetSingle(),a.GetProperty("Heading").GetSingle(),1,
                a.GetProperty("alive").ValueKind==JsonValueKind.True,a.GetProperty("CanAttack").GetBoolean()&&a.GetProperty("IsTargetable").GetBoolean(),a.GetProperty("target").ValueKind==JsonValueKind.Number?a.GetProperty("target").GetUInt32():0,
                casting?c.GetProperty("ActionId").GetUInt32():0,casting?t+c.GetProperty("remainingMs").GetDouble()/1000:0,
                casting?new(ground[0].GetSingle(),ground[2].GetSingle()):default);
        }).ToArray();
        foreach(var a in actors)
        {
            if(a.Base is P.Claw or P.FinalGiant&&!groups.ContainsKey(a.Id))groups[a.Id]=P.Nearest(a.P,P.FinalPads);
            if(P.Cast(a)!=null)detectedCasts++;
        }
        if(phase==2)
        {
            var ids=r.GetProperty("roster").EnumerateArray().Select(a=>a.GetProperty("ObjectId").GetUInt32()).ToHashSet();
            var party=actors.Where(a=>ids.Contains(a.Id)).Select(a=>new P.Ally(a.Id,a.P,a.Y,P.Role.Ranged,a.Alive)).ToArray();
            int vote=P.InferAlliance(party,2);
            Check(vote is -1 or 1,"Capture's B roster must never infer A/C");if(vote==1)allianceVotes++;
        }
        if(phase==6)
        {
            bool before=replay.Holding;
            replay.Update(t,actors.FirstOrDefault(a=>a.Base==P.Phlegethon),actors.Where(a=>a.Base==P.FinalGiant&&groups[a.Id]==1).ToArray());
            if(replay.Holding){if(firstHold==0)firstHold=t;lastHold=t;}
            if(before&&!replay.Holding)release=t;
        }
    }
    Check(frames>=3500&&allianceVotes>50&&detectedCasts>100,"Replay must exercise the real instance, alliance and helper casts");
    Check(release-firstHold>15&&release-firstHold<20,"Recorded Flare hold must include delayed invulnerability and own giant kill");
    Console.WriteLine($"Replay: {frames} in-duty frames; {allianceVotes} B consensus frames; {detectedCasts} cast-bearing frames; Flare hold {release-firstHold:F2}s.");
}
Console.WriteLine($"Passed {checks} invariant/replay assertions. Live movement is not validated by this test.");
