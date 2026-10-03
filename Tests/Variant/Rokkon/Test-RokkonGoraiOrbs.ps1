<#
Replays the October2 21:36 Gorai orb wipe after the last Hammer resolves.
Tests use production planning code: future circles permit timed travel, but late
arrivals, unknown shrink states and positions beyond the platform must fall back.
#>
$ErrorActionPreference='Stop'
$source=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.Gorai.cs')
$start=$source.IndexOf('    private static Vector3? PlanGoraiOrbs(')
$open=$source.IndexOf('{',$start);$end=$open+1;$depth=1
while($depth -gt 0){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--};$end++}
$method=$source.Substring($start,$end-$start)
$fixture=@"
using System;
using System.Linq;
using System.Numerics;
public static class OrbChecks {
 static int count;
 static float Distance2D(this Vector3 a,Vector3 b)=>Vector2.Distance(new(a.X,a.Z),new(b.X,b.Z));
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
 public static int Run(){
  var center=new Vector3(741,91,-190);
  var start=new Vector3(734.787f,91,-192.852f);
  var orbs=new[]{(new Vector3(731,91,-200),18.5f),(new Vector3(751,91,-180),18.5f),(new Vector3(731,91,-180),18.5f),(new Vector3(751,91,-200),8.5f)};
  for(int rotation=0;rotation<4;rotation++){
   var goal=PlanGoraiOrbs(start,null,orbs,6);
   Check(goal.HasValue,"Captured escape remains reachable in six seconds");
   Check(start.Distance2D(goal.Value)/4.5+.25<=6,"Conservative arrival precedes impact");
   Check(orbs.All(o=>goal.Value.Distance2D(o.Item1)>=o.Item2+.5f),"Safe from all four explosions");
   Check(PlanGoraiOrbs(goal.Value,goal,orbs,-.5)==goal,"Hold survives deadline through damage fence");
   Check(!PlanGoraiOrbs(start,null,orbs,.2).HasValue,"Reject late transfer");
   for(int i=0;i<4;i++){var d=orbs[i].Item1-center;orbs[i].Item1=center+new Vector3(-d.Z,0,d.X);}
   var s=start-center;start=center+new Vector3(-s.Z,0,s.X);
  }
  Check(!PlanGoraiOrbs(center,null,orbs.Take(3).ToArray(),10).HasValue,"Missing orb cannot establish ownership");
  Check(!PlanGoraiOrbs(center,null,orbs.Select(o=>(o.Item1,18.5f)).ToArray(),10).HasValue,"No confirmed small orb preserves native fallback");
  Check(!PlanGoraiOrbs(center+new Vector3(21,0,0),null,orbs,10).HasValue,"No manual transfer from outside arena");
  return count;
 }
"@
Add-Type -TypeDefinition ($fixture+"`n"+$method+"`n}")
"Passed $([OrbChecks]::Run()) Gorai orb checks."
