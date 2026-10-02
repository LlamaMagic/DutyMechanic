<#
.SYNOPSIS
Checks the combined right-arena knockback planner with captured mud placements.
.DESCRIPTION
Extracts the production planner and independently checks its returned travel,
hold and landing against a recorded moving-wind wave. No native game state is read.
#>
$ErrorActionPreference='Stop'
$source=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.RightPetals.cs')
$start=$source.IndexOf('    private static Vector3[] PlanRightPetals(')
$open=$source.IndexOf('{',$start);$end=$open+1;$depth=1
while($depth -gt 0){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--};$end++}
$method=$source.Substring($start,$end-$start)
$fixture=@'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
public static class PetalsChecks {
 static int count;
 static float Distance2D(this Vector3 a,Vector3 b)=>Vector2.Distance(new(a.X,a.Z),new(b.X,b.Z));
 static Vector3 Forward(float h)=>new((float)Math.Sin(h),0,(float)Math.Cos(h));
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
 static readonly Vector3 Center=new(47,309,93);
 static readonly Vector3[] Mud={new(53,309,98.1f),new(34.5f,309,93.5f),new(33,309,81),new(33.5f,309,104),new(45,309,85),new(55,309,79),new(61,309,90.5f),new(61,309,106.5f)};
 static void Route(Vector3 start,double impact,(Vector3 point,float heading)[] wind){
  var path=PlanRightPetals(start,Center,impact,Mud,wind);
  Check(path.Length>0,"Captured clear start must have a complete knockback plan");
  double at=.15;var from=start;
  foreach(var to in path){
   var duration=from.Distance2D(to)/5.5;
   for(double t=0;t<duration;t+=.025){
    var p=Vector3.Lerp(from,to,(float)(t/duration));
    Check(Mud.All(m=>p.Distance2D(m)>=Math.Min(5.5f,start.Distance2D(m))-.02f),"Approach clears every mud patch");
    Check(wind.All(w=>p.Distance2D(w.point+Forward(w.heading)*(float)(3*(at+t)))>=Math.Min(3.5f,start.Distance2D(w.point))-.02f),"Timed approach clears the moving wind");
   }
   at+=duration;from=to;
  }
  Check(at<=impact+.16,"Arrival precedes knockback");
  var stage=path.Last();var landing=stage+(stage-Center)*(15/stage.Distance2D(Center));
  Check(Math.Abs(landing.X-47)<=19.25&&Math.Abs(landing.Z-93)<=19.25,"Landing stays inside the arena");
  Check(Mud.All(m=>landing.Distance2D(m)>=5.49f),"Landing clears persistent mud");
  for(var t=at;t<=impact;t+=.025)
   Check(wind.All(w=>stage.Distance2D(w.point+Forward(w.heading)*(float)(3*t))>=3.48f),"Holding the destination does not meet moving wind");
 }
 public static int Run(){
  Route(new(39,309,99),5.6,Array.Empty<(Vector3,float)>());
  Route(new(47,309,93),5.6,Array.Empty<(Vector3,float)>());
  // Second wave observed east-to-west at22.115, X43.894. At the
  //20.238 Petals start it was approximately5.631y farther east.
  var wind=new[]{75f,83f,95f,99f,111f}.Select(z=>(new Vector3(49.525f,309,z),-(float)Math.PI/2)).ToArray();
  Route(new(39,309,99),5.6,wind);
  Check(PlanRightPetals(new(28,309,74),Center,.1,Mud,wind).Length==0,"Impossible arrival yields fallback instead of an unsafe shortcut");
  return count;
 }
'@
Add-Type -TypeDefinition ($fixture+"`n"+$method+"`n}")
"Passed $([PetalsChecks]::Run()) combined knockback checks."
