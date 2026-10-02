<#
.SYNOPSIS
Checks root-bait travel against the captured right-arena seed layout.
.DESCRIPTION
Extracts the production path planner and independently samples its segments.
This checks contact clearance and wall bounds, not movement timing in the client.
#>
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\..\Dungeons\VariantDungeons\MountRokkon.RightYozakura.cs') -Raw
$start = $source.IndexOf('    private static IEnumerable<Vector3> RootApproach(Vector3 start, Vector3 goal, Vector3[] seeds)')
if ($start -lt 0) { throw 'Missing production root planner.' }
$open = $source.IndexOf('{', $start)
$depth = 1
$end = $open + 1
while ($depth -gt 0 -and $end -lt $source.Length) {
    if ($source[$end] -eq '{') { $depth++ }
    if ($source[$end] -eq '}') { $depth-- }
    $end++
}
if ($depth -ne 0) { throw 'Unbalanced root planner.' }
$method = $source.Substring($start, $end - $start)
$fixture = @'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
public static class RootChecks {
    private static int count;
    private static float Distance2D(this Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X,a.Z),new Vector2(b.X,b.Z));
    private static void Check(bool value,string why) { if (!value) throw new Exception(why); count++; }
    private static readonly Vector3[] Seeds = {
        new(40,309,102),new(51,309,105.4f),new(33,309,99.5f),new(57,309,95.5f),
        new(37,309,92),new(31,309,84.5f),new(52.5f,309,82),new(60.5f,309,80),new(41.5f,309,77)
    };
    private static void Route(Vector3 start,Vector3 goal) {
        var initial=start;
        var path=RootApproach(start,goal,Seeds).ToArray();
        Check(path.Length>0,"A captured untrapped start must have a route");
        Check(path.Last().Distance2D(goal)<.01f,"Path must reach the staging corner");
        foreach(var to in path) {
            var distance=start.Distance2D(to);
            for(float d=0;d<=distance;d+=.1f) {
                var p=Vector3.Lerp(start,to,distance<.001f?0:d/distance);
                Check(p.X>=28.49f && p.X<=65.51f && p.Z>=74.49f && p.Z<=111.51f,"Travel stays inside the wall inset");
                Check(Seeds.All(s=>p.Distance2D(s)>=Math.Min(4.75f,initial.Distance2D(s))-.002f),"Travel never cuts through a seed or worsens initial padding overlap");
            }
            start=to;
        }
    }
    public static int Run() {
        Route(new(47.8f,309,102.66f),new(65.5f,309,111.5f));
        Route(new(47.64f,309,89.87f),new(65.5f,309,74.5f));
        Route(new(65.5f,309,74.5f),new(65.5f,309,111.5f));
        Route(new(65.5f,309,111.5f),new(65.5f,309,74.5f));
        Route(new(65.5f,309,74.5f),new(55,309,74.5f));
        Route(new(65.5f,309,111.5f),new(55,309,111.5f));
        Check(!RootApproach(Seeds[0],new(65.5f,309,111.5f),Seeds).Any(),"An imprisoned start cannot invent a collision-free path");
        return count;
    }
'@
Add-Type -TypeDefinition ($fixture + "`n" + $method + "`n}")
"Passed $([RootChecks]::Run()) root travel checks."
