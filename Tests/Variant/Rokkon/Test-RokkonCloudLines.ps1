<#
.SYNOPSIS
Replays the captured Cloud to Cloud hit against the production footprint.
.DESCRIPTION
The native omen already describes the final line direction. Keep this recorded
hit and a nearby escape point so a heading or width regression is visible offline.
#>
$ErrorActionPreference='Stop'
$main=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.cs')
$shishio=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.Shishio.cs')
$rectangle=[regex]::Match($main,'private static Vector2\[\] Rectangle\([\s\S]+?\};').Value
$line=[regex]::Match($shishio,'private static Vector2\[\] CloudLineFootprint\([\s\S]+?;').Value
if(!$rectangle -or !$line){throw 'Production geometry was not found.'}
$fixture=@"
using System;
using System.Numerics;
public static class CloudLineChecks {
$rectangle
$line
static int count;
static void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
static bool Hit(Vector2 p,uint action,Vector2 origin,Vector2 tip){
 var f=Vector2.Normalize(tip-origin);var d=p-origin;
 var local=new Vector2(d.X*f.Y-d.Y*f.X,Vector2.Dot(d,f));
 var poly=CloudLineFootprint(action);bool inside=false;
 for(int i=0,j=poly.Length-1;i<poly.Length;j=i++)
  if((poly[i].Y>local.Y)!=(poly[j].Y>local.Y)&&local.X<(poly[j].X-poly[i].X)*(local.Y-poly[i].Y)/(poly[j].Y-poly[i].Y)+poly[i].X)inside=!inside;
 return inside;
}
public static int Run(){
 var origin=new Vector2(-57.14508f,-310.01758f);
 var tip=new Vector2(-7.5670586f,-223.17278f);
 Check(Hit(new(-41.656628f,-281.93604f),33763,origin,tip),"The captured damage point remains covered");
 Check(!Hit(new(-45,-283),33763,origin,tip),"The first width preserves a lateral escape");
 foreach(var item in new[]{(33763u,1f),(33764u,3f),(33765u,6f)}){
  Check(Hit(new(item.Item2,20),item.Item1,Vector2.Zero,new(0,100)),"Actual edge receives padding");
  Check(!Hit(new(item.Item2+1,20),item.Item1,Vector2.Zero,new(0,100)),"Padding does not consume the next lane");
 }
 return count;
}
}
"@
Add-Type -TypeDefinition $fixture
"Passed $([CloudLineChecks]::Run()) cloud-line replay checks."
