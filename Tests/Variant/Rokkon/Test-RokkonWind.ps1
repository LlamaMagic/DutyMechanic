<#
.SYNOPSIS
Replays the first sentinel wind collision against the production forecast footprint.
.DESCRIPTION
A static circle reacted too late on October2. These detached geometry checks cover
that earlier warning, all travel headings and a real mud-free lateral escape pocket;
live steering and the subsequent knockback remain separate acceptance checks.
#>
$ErrorActionPreference='Stop'
$right=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.RightYozakura.cs')
$main=Get-Content -Raw (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.cs')
$footprint=[regex]::Match($right,'private static Vector2\[\] WitherwindFootprint\(\) => [^;]+;').Value
$rectangle=[regex]::Match($main,'(?s)private static Vector2\[\] Rectangle\(.*?\n    };').Value
if(!$footprint -or !$rectangle){throw 'Production geometry missing'}
$fixture=@"
using System;
using System.Numerics;
using System.Linq;
public static class WindChecks {
$footprint
$rectangle
static int count;
static void Check(bool value,string reason){if(!value)throw new Exception(reason);count++;}
static bool Contains(Vector2 p,Vector2 origin,float heading){
 var d=p-origin; var side=d.X*Math.Cos(heading)-d.Y*Math.Sin(heading);
 var forward=d.X*Math.Sin(heading)+d.Y*Math.Cos(heading); var poly=WitherwindFootprint();
 return side>=poly.Min(v=>v.X)&&side<=poly.Max(v=>v.X)&&forward>=poly.Min(v=>v.Y)&&forward<=poly.Max(v=>v.Y);
}
public static int Run(){
 var player=new Vector2(46.352f,97.381f);var wind=new Vector2(44.999f,89.884f);
 Check(Vector2.Distance(player,wind)>3.5f,"Old circle did not yet warn at00:48:14");
 Check(Contains(player,wind,0),"Forecast warns before the00:48:15.805 hit");
 var escape=new Vector2(39,99);
 var waves=new[]{28.977f,44.999f,52.995f,60.990f,64.988f};
 Check(waves.All(x=>!Contains(escape,new Vector2(x,89.884f),0)),"Captured lateral escape clears every first-wave lane");
 var mud=new[]{new Vector2(53,98.1f),new(34.5f,93.5f),new(33,81),new(33.5f,104),new(45,85),new(55,79),new(61,90.5f),new(61,106.5f)};
 Check(mud.All(m=>Vector2.Distance(m,escape)>5.5f),"Lateral escape also clears actual mud");
 for(int i=0;i<4;i++){
  var angle=(float)(i*Math.PI/2);var forward=new Vector2((float)Math.Sin(angle),(float)Math.Cos(angle));
  var side=new Vector2(forward.Y,-forward.X);
  Check(Contains(forward*8,Vector2.Zero,angle),"Rotated forecast includes impending contact");
  Check(!Contains(side*4,Vector2.Zero,angle),"Eight-yalm lane spacing retains a lateral gap");
  Check(!Contains(-forward*4,Vector2.Zero,angle),"Passed wind releases rear floor");
 }
 return count;
}
}
"@
Add-Type -TypeDefinition $fixture
"Passed $([WindChecks]::Run()) whirlwind replay checks."
