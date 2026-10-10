using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>Detached decisions for the Global territory 174 capture of October 7, 2026.</summary>
internal static class LabyrinthPlanner
{
    // Base rows identify actors, not translated NPC names. All three pad centers were present
    // in the B capture; only the B travel/hold behavior has been observed with a player.
    internal const uint Bone = 2347, Atomos = 2403, Thanatos = 2350, Bomb = 2407,
        Vassago = 2410, Behemoth = 2354, Comet = 2358, Phlegethon = 2360,
        Claw = 2362, FinalGiant = 2436;
    internal const float Margin = .5f;
    internal static readonly Vector2 BoneCenter = new(-451.2f, 23.93f);
    // Manual Oct7 11:07:00-02 held this Y42.4 point during the opening Valefor pack.
    // Oct9 SMN instead cast from Y41.6 and took repeated damage. Reuse measured
    // raised support without guessing the poison polygon or dictating melee/tank range.
    internal static readonly Vector2 OpeningPlatform = new(-390.6842f,289.42828f);
    internal static bool HoldOpeningPlatform(int phase, Vector2 player, bool inCombat, Role role, Actor[] actors) =>
        phase==0&&inCombat&&role is Role.Healer or Role.Ranged&&Vector2.Distance(player,OpeningPlatform)<40&&
        // Base2392-2397 are the captured elemental homunculi;2398 is opening Valefor.
        // Atomos reuses names but different base rows and must not retain this hold.
        actors.Any(a=>a.Base is >=2392 and <=2398&&a.Alive&&Vector2.Distance(a.P,OpeningPlatform)<45);
    // Manual SCH capture held this raised middle-room position from 11:07:18–39 UTC.
    // Use the measured point, not an invented polygon for the surrounding poisoned floor.
    internal static readonly Vector2 GreaterDemonPlatform = new(-446.57382f,279.7106f);

    // October 7 20:15:38–48 UTC: a ranged party member held this western raised floor
    // at Y42.4 while Tired remained on the poisoned Y41.6 floor. This is a measured
    // support position, not a platform polygon or a verified tank/melee engagement point.
    internal static readonly Vector2 DiraPlatform = new(-466.1784f,193.19446f);
    internal static bool HoldDiraPlatform(int phase, Vector2 player, bool inCombat, Role role, Actor[] actors) =>
        phase==0&&inCombat&&role is Role.Healer or Role.Ranged&&
        Vector2.Distance(player,DiraPlatform)<45&&
        actors.Any(a=>a.Base is 2401 or 2402&&a.Alive&&Vector2.Distance(a.P,DiraPlatform)<45);

    // The measured SCH support point must not lease movement for tanks or melee who need to engage the pack.
    internal static bool HoldGreaterDemonPlatform(int phase, Vector2 player, bool inCombat, Role role, Actor[] actors) =>
        phase==0&&inCombat&&role is Role.Healer or Role.Ranged&&Vector2.Distance(player,GreaterDemonPlatform)<40&&
        actors.Any(a=>a.Base==2400&&a.Alive&&Vector2.Distance(a.P,GreaterDemonPlatform)<45);
    internal static readonly Vector2 BehemothCenter = new(-110, -368.35f);
    internal static readonly Vector2 FinalCenter = new(-110, 181.6f);
    internal static readonly Vector2[] AtomosPads = [new(213.71f,244.90f),new(213.69f,280.13f),new(213.72f,315.11f)];
    internal static readonly Vector2[] FinalPads = [new(-148.65f,191.975f),new(-110,221.59f),new(-71.35f,191.975f)];
    internal static readonly Vector2[] FireSectors = [new(-131,-153),new(-110,-190),new(-89,-153)];
    // The researched Allagan Bomb arena is centered here with radius 33.2. Keep
    // 1.5 yalms inside that edge: the Oct 8 SCH report showed ally-distance alone
    // accepted the lava. Four simple sectors avoid a self-touching annulus polygon.
    internal static readonly Vector2 FireCenter = new(-110,-165.6f);
    internal const float FireFloorRadius = 31.7f;
    internal static readonly Hazard[] FireBoundary = Enumerable.Range(0,4).Select(i=>
        new Hazard(uint.MaxValue-(uint)i-1,0,FireCenter,i*MathF.PI/2,
            double.PositiveInfinity,Shape.Sector,65,FireFloorRadius,MathF.PI/4+.001f)).ToArray();
    internal static bool FireBoundaryActive(int phase,bool combat,Vector2 player) =>
        // The Oct 8 run oscillated at radius50 on the entrance: the exterior of
        // the finite donut was closer than its safe interior. Activate within40,
        // where the interior is nearer, leaving the entrance to normal navigation.
        phase==4&&combat&&Vector2.Distance(player,FireCenter)<40;
    internal static Vector2 FireRecovery(Vector2 player) => FireCenter+
        Vector2.Normalize(player-FireCenter)*(FireFloorRadius-1);

    // Oct 8 22:51 C/SCH repeatedly acquired/released movement at the 12-yalm
    // assist threshold. Finish the existing five-yalm follow before releasing;
    // the wider restart threshold lets the routine work without boundary chatter.
    internal static bool ContinueFireAssist(float distance,bool following) => distance>(following?5:12);

    // Phlegethon's outer apron occupies the southern 195-degree sector, not a full
    // 44.5-yalm disc. Clip the northern annulus at the central floor's half-yalm inset.
    internal static readonly Hazard FinalNorthBoundary = new(uint.MaxValue,0,FinalCenter,MathF.PI,
        double.PositiveInfinity,Shape.Sector,100,31.95f,82.5f*MathF.PI/180);

    internal enum Role { Tank, Healer, Melee, Ranged }
    internal sealed record Actor(uint Id, uint Base, Vector2 P, float Y, float Heading, float Radius,
        bool Alive, bool Attackable, uint Target, uint Action = 0, double Finish = 0, Vector2 Ground = default, bool InCombat = false);
    internal sealed record Ally(uint Id, Vector2 P, float Y, Role Role, bool Alive);
    internal sealed record Goal(Vector2 P, float Y, float Tolerance, string Reason, bool Urgent = false);
    internal enum Shape { Circle, Cone, Sector }

    /// <summary>One helper's damage footprint with a half-yalm margin, retained through its effect.</summary>
    internal sealed record Hazard(uint Owner, uint Action, Vector2 Origin, float Heading, double Finish,
        Shape Kind, float Outer, float Inner = 0, float HalfAngle = MathF.PI)
    {
        internal Vector2[] Polygon()
        {
            const int count = 64;
            List<Vector2> points = [];
            var forward = new Vector2(MathF.Sin(Heading), MathF.Cos(Heading));
            var center = Kind == Shape.Cone ? Origin - forward * Margin : Origin;
            if (Kind == Shape.Cone) points.Add(center);
            float arc = Kind == Shape.Circle ? MathF.PI : HalfAngle;
            float outer = Outer / MathF.Cos(arc / count);
            for (int i=0;i<=count;i++)
            {
                float angle=Heading-arc+2*arc*i/count;
                points.Add(center+new Vector2(MathF.Sin(angle),MathF.Cos(angle))*outer);
            }
            if (Kind == Shape.Sector)
                for (int i=count;i>=0;i--)
                {
                    float angle=Heading-arc+2*arc*i/count;
                    points.Add(center+new Vector2(MathF.Sin(angle),MathF.Cos(angle))*Inner);
                }
            return points.ToArray();
        }

        internal bool Contains(Vector2 p)
        {
            var forward=new Vector2(MathF.Sin(Heading),MathF.Cos(Heading));
            var d=p-Origin+(Kind==Shape.Cone?forward*Margin:Vector2.Zero);
            float length=d.Length();
            return length<=Outer && length>=Inner && (Kind==Shape.Circle || length<.001f ||
                Vector2.Dot(d/length,forward)>=MathF.Cos(HalfAngle));
        }
    }

    internal static Hazard Cast(Actor a) => (a.Base,a.Action) switch
    {
        (2348,749) => new(a.Id,a.Action,a.Ground,a.Heading,a.Finish,Shape.Circle,6+Margin),
        (Bone,750) => new(a.Id,a.Action,a.P,a.Heading,a.Finish,Shape.Cone,105+Margin,0,MathF.PI/3),
        (2351,760) => new(a.Id,a.Action,a.Ground,a.Heading,a.Finish,Shape.Circle,6+Margin),
        (Thanatos,762) => new(a.Id,a.Action,a.P,a.Heading,a.Finish,Shape.Cone,11+Margin,0,MathF.PI/3),
        (2352,1829) or (2406,1829) => new(a.Id,a.Action,a.Ground,a.Heading,a.Finish,Shape.Circle,5+Margin),
        (2356,1789) => new(a.Id,a.Action,a.P,a.Heading,a.Finish,Shape.Circle,8.4f+Margin),
        (2361,1736) => new(a.Id,a.Action,a.P,a.Heading,a.Finish,Shape.Cone,80+Margin,0,26*MathF.PI/180),
        (2361,1737) => new(a.Id,a.Action,a.P,a.Heading,a.Finish,Shape.Sector,7.5f+Margin,2.2f-Margin,MathF.PI/2+.03f),
        (2361,1739) => new(a.Id,a.Action,a.P,a.Heading,a.Finish,Shape.Sector,17.5f+Margin,12.5f-Margin,MathF.PI/2+.03f),
        (2361,>=1741 and <=1744) => new(a.Id,a.Action,a.Ground,a.Heading,a.Finish,Shape.Circle,a.Action-1738+Margin),
        _ => null
    };

    // UI8A was 0,1,3,7,15,31,63,127,255 at the eight actual objectives. Unknown director
    // data is not progress; bit tests alone must never run against another instance.
    internal static int Phase(int? mask) => mask switch
    { 0 or 1 => 0,3=>1,7=>2,15=>3,31=>4,63=>5,127=>6,255=>7,_=>-1 };
    internal static int Nearest(Vector2 p, Vector2[] anchors) => Enumerable.Range(0,anchors.Length)
        .OrderBy(i=>Vector2.DistanceSquared(p,anchors[i])).First();

    internal static int InferAlliance(Ally[] party, int phase)
    {
        // A native alliance slot mapping was not captured. Require three OWN party members
        // inside one actual lane/sector; never infer from the nearest unrelated alliance.
        var anchors=phase==2?AtomosPads:phase==4?FireSectors:FinalPads;
        var votes=party.Where(a=>a.Alive && (phase!=6||a.Y>600) && (phase!=2||a.P.X>193&&a.P.X<270))
            .Select(a=>new { Index=Nearest(a.P,anchors),Distance=anchors.Min(p=>Vector2.Distance(p,a.P)) })
            .Where(a=>a.Distance<(phase==2?46:phase==6?4.2f:15)).GroupBy(a=>a.Index).OrderByDescending(g=>g.Count()).ToArray();
        return votes.Length>0&&votes[0].Count()>=(phase==6?4:3)&&(votes.Length==1||votes[1].Count()<votes[0].Count()) ? votes[0].Key : -1;
    }

    // Four final-pad occupants avoid treating three allies passing the common B entrance
    // as an assignment. The runtime additionally requires a stationary formation window.
    // Departure is directional: healers/ranged follow three allies, while tanks may lead
    // a nearby group containing a healer. Different-floor catch-up is limited to the final drop.
    internal static bool CanApproach(Vector2 player,float y,Vector2 destination,Role role,uint me,Ally[] party)
    {
        var others=party.Where(a=>a.Alive&&a.Id!=me).ToArray();
        if(y<600&&others.Count(a=>a.Y>600)>=3)return true;
        if(role==Role.Tank)
        {
            var near=others.Where(a=>Math.Abs(a.Y-y)<25&&Vector2.Distance(a.P,player)<35).ToArray();
            if(near.Length>=3&&near.Any(a=>a.Role==Role.Healer))return true;
        }
        var delta=destination-player;
        if(delta.LengthSquared()<1)return true;
        var direction=Vector2.Normalize(delta);
        return others.Count(a=>Math.Abs(a.Y-y)<25&&Vector2.Dot(a.P-player,direction)> -2)>=3;
    }
    // October 8: alliance discovery held at the landing, then the pad mechanic bypassed
    // group travel and pulled early. Follow a witnessed three-person formation down the
    // stairs without assigning a lane; after assignment, use the same gate for pad movement.
    // Four yalms of arrival tolerance keep a follower behind the third ally. Combat releases
    // this entry-only gate so missing/dead allies cannot prevent mandatory pad support.
    internal static Goal AtomosEntry(Vector2 player,float y,int alliance,Role role,uint me,Ally[] party,bool combat)
    {
        var hold=new Goal(player,y,.5f,"Atomos: waiting for own-party entry");
        if(alliance>=0 && (combat || role==Role.Tank && CanApproach(player,y,AtomosPads[alliance],role,me,party)))
            return new(AtomosPads[alliance],51.05f,2.3f,"Atomos linked-lane pad");
        // Once three others actually occupy our pad, join as the fourth. Keeping the
        // stair-following tolerance here would stop outside the activation circle forever.
        if(alliance>=0 && party.Count(a=>a.Alive&&a.Id!=me&&Math.Abs(a.Y-51.05f)<2&&
            Vector2.Distance(a.P,AtomosPads[alliance])<=2.3f)>=3)
            return new(AtomosPads[alliance],51.05f,2.3f,"Atomos: joining own-party pad");
        var ahead=party.Where(a=>a.Alive&&a.Id!=me&&a.P.X>119&&a.P.X<270&&Math.Abs(a.Y-y)<25&&
            (alliance<0||Nearest(a.P,AtomosPads)==alliance)).OrderByDescending(a=>a.P.X).ToArray();
        // Require a coherent group, not three scattered members in different lanes.
        foreach(var ally in ahead)
        {
            if(ahead.Count(a=>Vector2.Distance(a.P,ally.P)<=12&&a.P.X>=ally.P.X)<3)continue;
            if(ally.P.X<=player.X+4)return hold;
            return new(ally.P,ally.Y,4,"Atomos: following own-party entry");
        }
        return hold;
    }

    // The sealed warp lands in physical A regardless of real alliance. An unknown
    // identity must not strand a support player at its entrance (Oct8 22:03 death).
    // Oct9 03:29 also stranded a known B member there: support physical A without
    // changing the actual assignment. Known A retains its normal role/linked-kill logic.
    // This chooses temporary local support only; it never assigns an alliance.
    internal static bool SupportForcedLanding(int phase,bool transferred,int alliance,Vector2 player,float y) =>
        phase==2&&transferred&&alliance!=0&&Math.Abs(y-51.05f)<3&&
        player.X>190&&player.X<230&&Math.Abs(player.Y-AtomosPads[0].Y)<10;

    internal static bool PadSupport(uint me, Ally[] party) => party.Where(a=>a.Alive)
        .OrderBy(a=>a.Role==Role.Healer?0:a.Role==Role.Ranged?1:a.Role==Role.Melee?2:3)
        .ThenBy(a=>a.Id).Take(4).Any(a=>a.Id==me);

    // The raid assembles on its alliance pads before the pull. Target selection by an ally
    // is not engagement; use actual combat/casting so merely targeting the boss cannot pull us off.
    // Missing boss data remains a staging state, while a dead boss must permit completion.
    internal static bool FinalStaging(Actor boss, bool playerInCombat) => !playerInCombat &&
        (boss==null || boss.Alive&&!boss.InCombat&&boss.Action==0);

    internal static bool LinkedAtomosAlive(Actor[] actors, int alliance, bool observedDead=false)
    {
        var linked=actors.FirstOrDefault(a=>a.Base==Atomos&&Nearest(a.P,AtomosPads)==(alliance+1)%3);
        // A missing actor cannot establish a kill, but it cannot undo an observed death.
        // Live reappearance overrides the remembered death after a reset/respawn.
        return linked?.Alive ?? !observedDead;
    }

    internal static bool OnBonePlatform(Vector2 p)
    {
        // These islands are safe in both poison layouts. Without map-effect telemetry we use
        // their intersection, never invent a phase from temporary dragon deaths.
        if(Vector2.Distance(p,BoneCenter)<=7.5f)return true;
        for(int i=0;i<8;i++)
        {
            float angle=i*MathF.PI/4;
            var f=new Vector2(MathF.Sin(angle),MathF.Cos(angle));var s=new Vector2(f.Y,-f.X);
            foreach(float r in new[]{18.02f,32.13f,45.745f})
            {var d=p-(BoneCenter+f*r);if(MathF.Abs(Vector2.Dot(d,f))<=2.45f&&MathF.Abs(Vector2.Dot(d,s))<=2.45f)return true;}
        }
        return false;
    }

    // Two live deaths followed a predicted (action=0, untimed) giant frontal moving
    // a sheltered player into Meteor. During Meteor only, keep timed damage and arena
    // bounds hard constraints, but let shelter outrank this speculative facing footprint.
    // Actor identity scopes the exception: other persistent hazards remain effective.
    internal static Hazard[] MeteorHazards(Hazard[] hazards, Actor[] actors) =>
        hazards.Where(h=>!(h.Action==0&&double.IsPositiveInfinity(h.Finish)&&
            actors.Any(a=>a.Id==h.Owner&&a.Base==2357))).ToArray();

    internal static Vector2? Shelter(Vector2 player, Actor boss, Actor[] actors, Hazard[] hazards)
    {
        // A blocker inside Behemoth's hitbox is invalid. Stand just beyond a live comet,
        // then check the arena and ALL earlier/simultaneous damaging footprints as one plan.
        var options=new List<(Vector2 Position,bool Preferred)>();
        foreach(var rock in actors.Where(a=>a.Base==Comet&&a.Alive))
        {
            var delta=rock.P-boss.P;float d=delta.Length();
            // Oct 9 00:37 UTC: all four live comets failed the preferred margin;
            // the furthest had 0.31 yalms of hitbox separation and we died exposed.
            // If no preferred shelter exists, try a non-overlapping live blocker.
            // Never relax actual hitbox separation, arena bounds, or timed hazards.
            if(d<=boss.Radius+rock.Radius||d<.1f)continue;
            var p=rock.P+delta/d*(rock.Radius+1.0f);
            if(Vector2.Distance(p,BehemothCenter)<29 && !hazards.Any(h=>h.Contains(p)))options.Add((p,d>boss.Radius+rock.Radius+Margin));
        }
        return options.OrderByDescending(p=>p.Preferred).ThenBy(p=>Vector2.DistanceSquared(p.Position,player)).Select(p=>(Vector2?)p.Position).FirstOrDefault();
    }

    // Repeated meteor deaths alternated with successful sheltering, while the old capture
    // omitted runtime CombatReach. Record each rejection input rather than guessing that
    // comets were destroyed or weakening collision/hazard checks without evidence.
    internal static string ShelterEvidence(Actor boss, Actor[] actors, Hazard[] hazards) =>
        $"boss={boss.Id:X}/{boss.P}/radius={boss.Radius:F2}; comets=["+
        string.Join("; ",actors.Where(a=>a.Base==Comet).Take(12).Select(rock=>
        {
            var delta=rock.P-boss.P;float d=delta.Length();
            var p=d>.001f?rock.P+delta/d*(rock.Radius+1):rock.P;
            var conflicts=hazards.Where(h=>h.Contains(p)).Select(h=>$"{h.Owner:X}:{h.Action}");
            return $"{rock.Id:X} pos={rock.P} alive={rock.Alive} radius={rock.Radius:F2} distance={d:F2} min={boss.Radius+rock.Radius:F2} preferredMin={boss.Radius+rock.Radius+Margin:F2} shelter={p} arenaDistance={Vector2.Distance(p,BehemothCenter):F2} hazards={string.Join(',',conflicts)}";
        }))+ "]";

    /// <summary>A cast-end alone is unsafe: the boss remained attackable for two seconds after Flare.</summary>
    internal sealed class FlareState
    {
        internal bool Holding { get; private set; }
        private bool sawHidden, sawOwnGiant;
        private double finish;
        internal void Reset(){Holding=false;sawHidden=false;sawOwnGiant=false;finish=0;}
        internal void Update(double now, Actor boss, Actor[] ownGiants)
        {
            if(boss==null||!boss.Alive){Reset();return;}
            if(boss.Action==1730&&!Holding){Holding=true;sawHidden=false;sawOwnGiant=false;finish=boss.Finish;}
            if(ownGiants.Any(a=>a.Alive)||!boss.Attackable&&ownGiants.Length>0)Holding=true; // Recover enabling during adds, including an already-dead own giant.
            if(!Holding)return;
            sawHidden|=!boss.Attackable;sawOwnGiant|=ownGiants.Length>0;
            if(boss.Action!=1730&&now>finish+.75&&boss.Attackable&&sawHidden&&sawOwnGiant&&
                ownGiants.All(a=>!a.Alive))Reset();
        }
    }

    internal static bool Eligible(Actor a, int phase, int alliance, bool astral, bool flareHold,
        IReadOnlyDictionary<uint,int> assignments)
    {
        if(!a.Alive||!a.Attackable)return false;
        return phase switch
        {
            0=>a.Base is >=2392 and <=2402,
            1=>a.Base is Bone or 2349 or 2434,
            2=>alliance>=0&&a.Base is >=2403 and <=2406&&Nearest(a.P,AtomosPads)==alliance,
            3=>a.Base is 2352 or 2435 || a.Base==Thanatos&&astral,
            4=>a.Base==Bomb || alliance>=0&&a.Base is >=2408 and <=2410&&
                (assignments.TryGetValue(a.Id,out int lane)?lane:Nearest(a.P,FireSectors))==alliance,
            5=>a.Base is Behemoth or 2356 or 2357,
            6=>a.Base==Phlegethon&&!flareHold || alliance>=0&&a.Base is Claw or FinalGiant&&
                assignments.TryGetValue(a.Id,out int group)&&group==alliance,
            _=>false
        };
    }

    internal static float Priority(uint actorBase, int phase, Role role, bool tankOwnsBoss, float authoredWeight)
    {
        // Tank assignments must survive an add spawn: dragging Vassago/Behemoth after a
        // balloon or claw is worse than a DPS target delay. Flare eligibility still removes
        // Phlegethon entirely before this scoring step, so tanks also switch to their giant.
        if(role==Role.Tank)
        {
            if(phase==4&&actorBase==Vassago)return 4000;
            if(tankOwnsBoss&&actorBase is Bone or Thanatos or Behemoth or Phlegethon)return 4000;
            if(actorBase is FinalGiant or 2357)return 3000;
            if(phase==2&&actorBase is >=2404 and <=2406)return 2000;
        }
        if(actorBase is Claw or FinalGiant)return 2500+authoredWeight;
        if(phase==2&&role==Role.Melee&&actorBase==Atomos)return 2000;
        return authoredWeight;
    }
}
