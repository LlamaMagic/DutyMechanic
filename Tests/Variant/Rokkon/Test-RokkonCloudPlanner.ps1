<#
.SYNOPSIS
Replays the failed wide cloud wave through the production timed planner.
.DESCRIPTION
The eight lines below are detached from the October2 02:12:52.655 capture.
Sample every returned segment and hold independently at20ms intervals, including
startup and quantized waits, so future previews cannot erase valid timed travel.
#>
$ErrorActionPreference='Stop'
$source=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.CloudLines.cs')
$start=$source.IndexOf('    private static Vector3[] PlanCloudLines(')
$open=$source.IndexOf('{',$start);$end=$open+1;$depth=1
while($depth -gt 0){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--};$end++}
$method=$source.Substring($start,$end-$start)
$fixture=@'
using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
public static class CloudPlannerChecks {
 static int count;
 static float Distance2D(this Vector3 a,Vector3 b)=>Vector2.Distance(new(a.X,a.Z),new(b.X,b.Z));
 static Vector3 Forward(float h)=>new((float)Math.Sin(h),0,(float)Math.Cos(h));
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
 static readonly (Vector3 origin,float heading,float width,double end)[] Lines={
  (new(-50.003845f,360,-282.70392f),2.4865725f,3.5f,3.5667484),
  (new(-22.720703f,360,-290.02832f),4.217409f,3.5f,2.5302732),
  (new(-45.181946f,360,-280.6897f),3.0111063f,3.5f,2.5302732),
  (new(-25.833557f,360,-314.19855f),5.3668575f,3.5f,3.0336556),
  (new(-34.836426f,360,-319.32556f),6.021398f,3.5f,2.0625862),
  (new(-50.003845f,360,-317.2503f),.21490431f,3.5f,3.0336556),
  (new(-54.184875f,360,-314.19855f),.7111547f,3.5f,2.0625862),
  (new(-20.676025f,360,-294.8501f),4.5549855f,3.5f,3.5667484)
 };
 static void Safe(Vector3 p,double at){
  Check(p.Distance2D(new(-40,360,-300))<=19.251f,"Travel stays inside the arena");
  foreach(var line in Lines){
   if(at<line.end-1.3||at>line.end)continue;
   var f=Forward(line.heading);var d=p-line.origin;
   var along=Vector3.Dot(d,f);var perpendicular=Math.Abs(d.X*f.Z-d.Z*f.X);
   Check(along<-.5||along>100.5||perpendicular>=line.width-.001,"Path never crosses an active cloud line");
  }
 }
 public static int Run(){
  var start=new Vector3(-49.679424f,360,-298.37894f);
  var path=PlanCloudLines(start,start,Lines);
  Check(path.Length>0,"Captured wide-wave start has a timed escape");
  var from=start;double at=0;bool initial=true;
  foreach(var point in path){
   var travel=from.Distance2D(point)/5.5;
   var steps=Math.Max(1,(int)Math.Ceiling(travel/.15));
   if(initial&&travel>.001)steps++;
   var duration=steps*.15;
   for(double t=0;t<=duration;t+=.02){
    var progress=travel<.001?1:Math.Min(1,Math.Max(0,t-(initial?.15:0))/travel);
    Safe(Vector3.Lerp(from,point,(float)progress),at+t);
   }
   at+=duration;from=point;initial=false;
  }
  for(var t=at;t<4;t+=.02)Safe(from,t);
  var blocked=new[]{(new Vector3(-40,360,-330),0f,30f,1.1)};
  Check(PlanCloudLines(new(-40,360,-300),new(-40,360,-300),blocked).Length==0,"No safe initial state uses ordinary-escape fallback");
  return count;
 }
'@
Add-Type -TypeDefinition ($fixture+"`n"+$method+"`n}")
$time=Measure-Command { $script:result=[CloudPlannerChecks]::Run() }
"Passed $result timed cloud checks in $([math]::Round($time.TotalMilliseconds,1))ms."
