<#
.SYNOPSIS
Checks Shishio's captured swipe waves against the production half-disc polygon.
.DESCRIPTION
Native omen axes already include the left/right turn. Replay both October2 waves
so a future facing change cannot invert the safe pocket or erase the required move.
#>
$ErrorActionPreference='Stop'
$source=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.cs')
$start=$source.IndexOf('    private static Vector2[] Sector(')
$open=$source.IndexOf('{',$start);$end=$open+1;$depth=1
while($depth -gt 0){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--};$end++}
$sector=$source.Substring($start,$end-$start)
$fixture=@"
using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
public static class SwipeChecks {
$sector
static int count;
static void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
static bool Hit(Vector2 p,(Vector2 origin,Vector2 tip) c){
 var forward=Vector2.Normalize(c.tip-c.origin);var d=p-(c.origin-forward*.5f);
 var local=new Vector2(d.X*forward.Y-d.Y*forward.X,Vector2.Dot(d,forward));
 var poly=Sector(180,40.5f);bool inside=false;
 for(int i=0,j=poly.Length-1;i<poly.Length;j=i++)
  if((poly[i].Y>local.Y)!=(poly[j].Y>local.Y)&&local.X<(poly[j].X-poly[i].X)*(local.Y-poly[i].Y)/(poly[j].Y-poly[i].Y)+poly[i].X)inside=!inside;
 return inside;
}
public static int Run(){
 var first=new (Vector2,Vector2)[]{(new(-28,-300),new(-28,-260)),(new(-40,-288),new(0,-288)),(new(-40,-312),new(-40,-352)),(new(-52,-300),new(-92,-300))};
 var second=new (Vector2,Vector2)[]{(new(-28,-300),new(-28,-340)),(new(-40,-288),new(-40,-248)),(new(-40,-312),new(0,-312)),(new(-52,-300),new(-92,-300))};
 var failed=new Vector2(-56.594f,-300.352f);
 Check(first.Any(c=>Hit(failed,c)),"Captured first hit is inside a swipe");
 Check(second.Count(c=>Hit(failed,c))==2,"Captured second wave overlaps twice at the failed point");
 Check(first.All(c=>!Hit(new(-46,-306),c)),"First native axes preserve the northwest pocket");
 Check(second.All(c=>!Hit(new(-46,-294),c)),"Second native axes preserve the southwest pocket");
 Check(second.Any(c=>Hit(new(-46,-306),c)),"Second wave requires leaving the first pocket");
 foreach(var wave in new[]{first,second}){
  int safe=0;
  for(float x=-59;x<=-21;x++)for(float z=-319;z<=-281;z++)
   if(Vector2.Distance(new(x,z),new(-40,-300))<19.5f&&wave.All(c=>!Hit(new(x,z),c)))safe++;
  Check(safe>20,"Padded simultaneous swipes retain usable in-bounds floor");
 }
 return count;
}
}
"@
Add-Type -TypeDefinition $fixture
"Passed $([SwipeChecks]::Run()) swipe replay checks."
