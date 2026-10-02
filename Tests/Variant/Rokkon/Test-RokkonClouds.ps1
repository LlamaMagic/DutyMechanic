<#
.SYNOPSIS
Replays Shishio's captured lightning-chain order through the production predictor.
.DESCRIPTION
The October 1 first attempt supplies six observed pairs and the surviving thrice
clouds. These checks validate geometry/order; live avoidance timing is separate.
#>
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\..\Dungeons\VariantDungeons\MountRokkon.ShishioClouds.cs') -Raw
$start = $source.IndexOf('    private static IEnumerable<(uint id, Vector3 point, double delay)> PredictShishioClouds(')
if ($start -lt 0) { throw 'Missing production cloud predictor.' }
$open = $source.IndexOf('{', $start)
$depth = 1
$end = $open + 1
while ($depth -gt 0 -and $end -lt $source.Length) {
    if ($source[$end] -eq '{') { $depth++ }
    if ($source[$end] -eq '}') { $depth-- }
    $end++
}
if ($depth -ne 0) { throw 'Unbalanced cloud predictor.' }
$method = $source.Substring($start, $end - $start)
$fixture = @'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
public static class CloudChecks {
    private static int count;
    private static float Distance2D(this Vector3 a,Vector3 b) => Vector2.Distance(new(a.X,a.Z),new(b.X,b.Z));
    private static Vector3 Forward(float h) => new((float)Math.Sin(h),0,(float)Math.Cos(h));
    private static void Check(bool value,string why) { if(!value) throw new Exception(why); count++; }
    public static int Run() {
        var center=new Vector3(-40,360,-300);
        // Native casts began 23:23:03.820,04.898,05.968,07.041,08.117,09.187.
        // Input is deliberately reversed so enumeration order cannot fake the chain.
        var points=new Vector3[] {new(-45,360,-285),new(-35,360,-315),
            new(-51,360,-289),new(-29,360,-311),new(-55,360,-295),new(-25,360,-305),
            new(-50.5f,360,-300),new(-29.5f,360,-300),new(-55,360,-305),new(-25,360,-295),
            new(-51,360,-311),new(-29,360,-289)};
        var clouds=points.Select((p,i)=>(id:(uint)i+1,point:p)).Reverse().ToArray();
        var result=PredictShishioClouds(33757,center,0,clouds).ToArray();
        Check(result.Length==12,"Every surviving once-cloud appears exactly once");
        for(var i=0;i<12;i++) Check(result.Single(c=>c.id==i+1).delay==1+i/2,"Forecast pair matches the captured cast wave");
        // An unrelated disconnected cloud must not force an invented extra wave.
        var withExtra=clouds.Append((99u,new Vector3(-10,360,-270))).ToArray();
        Check(PredictShishioClouds(33757,center,0,withExtra).All(c=>c.id!=99),"Disconnected cloud does not invent a chain");
        var triple=PredictShishioClouds(34726,center,0,new[] {(1u,new Vector3(-40,360,-318)),(2u,new Vector3(-40,360,-282))}).ToArray();
        Check(triple.Length==2 && triple.All(c=>Math.Abs(c.delay-7.1)<.001),"Thrice uses the two actual survivors after absorption");
        var east=new Vector3(-21.2f,360,-300);
        Check(east.Distance2D(center)<19.5f && triple.All(c=>east.Distance2D(c.point)>23.5f),"Thrice retains an in-bounds east pocket");
        foreach(var heading in new[] {0f,(float)Math.PI/3,5*(float)Math.PI/3}) {
            var d=east-center; var f=Forward(heading);
            Check(Math.Abs(d.X*f.Z-d.Z*f.X)>7.5f,"Thrice pocket clears every captured centered line");
        }
        return count;
    }
'@
Add-Type -TypeDefinition ($fixture + "`n" + $method + "`n}")
"Passed $([CloudChecks]::Run()) Shishio cloud replay checks."
