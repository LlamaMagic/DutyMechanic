<#
.SYNOPSIS
Replays the first captured Haunting Thrall layout against its production forecast.
.DESCRIPTION
The old moving circles warned only at contact and repeatedly stopped escape at
their edge. Verify early warning, a usable lateral pocket and rotational symmetry.
#>
$ErrorActionPreference='Stop'
$source=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.Shishio.cs')
$start=$source.IndexOf('    private static Vector2[] BuildGhostFootprint(')
$open=$source.IndexOf('{',$start);$end=$open+1;$depth=1
while($depth -gt 0){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--};$end++}
$method=$source.Substring($start,$end-$start)
$fixture=@'
using System;
using System.Numerics;
using System.Linq;
public static class GhostChecks {
 static int count;
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
 static bool Hit(Vector2 p,(Vector2 point,float heading) ghost){
  var f=new Vector2((float)Math.Sin(ghost.heading),(float)Math.Cos(ghost.heading));var d=p-ghost.point;
  var local=new Vector2(d.X*f.Y-d.Y*f.X,Vector2.Dot(d,f));var poly=BuildGhostFootprint();bool inside=false;
  for(int i=0,j=poly.Length-1;i<poly.Length;j=i++)
   if((poly[i].Y>local.Y)!=(poly[j].Y>local.Y)&&local.X<(poly[j].X-poly[i].X)*(local.Y-poly[i].Y)/(poly[j].Y-poly[i].Y)+poly[i].X)inside=!inside;
  return inside;
 }
 public static int Run(){
  var ghosts=new (Vector2,float)[]{(new(-22.507141f,-317.52502f),5.2037735f),(new(-57.51129f,-282.5208f),3.0548255f),(new(-57.51129f,-300.0077f),1.281996f),(new(-22.507141f,-282.5208f),4.2415695f)};
  var start=new Vector2(-50.904404f,-299.39725f);
  Check(ghosts.Any(g=>Hit(start,g)),"Captured waiting point is warned before movement begins");
  Check(ghosts.All(g=>!Hit(new(-49,-309),g)),"A northward lateral departure remains available");
  int safe=0;
  for(float x=-59;x<-20;x++)for(float z=-319;z<-280;z++)
   if(Vector2.Distance(new(x,z),new(-40,-300))<19.5f&&ghosts.All(g=>!Hit(new(x,z),g)))safe++;
  Check(safe>100,"Forecast preserves usable arena floor between all four ghosts");
  foreach(var angle in new[]{0f,(float)Math.PI/2,(float)Math.PI,3*(float)Math.PI/2}){
   var f=new Vector2((float)Math.Sin(angle),(float)Math.Cos(angle));var side=new Vector2(f.Y,-f.X);
   Check(Hit(f*11,(Vector2.Zero,angle)),"Every heading reserves the short forward horizon");
   Check(!Hit(side*7,(Vector2.Zero,angle)),"Every heading retains the lateral gap");
  }
  return count;
 }
'@
Add-Type -TypeDefinition ($fixture+"`n"+$method+"`n}")
"Passed $([GhostChecks]::Run()) moving-ghost checks."
