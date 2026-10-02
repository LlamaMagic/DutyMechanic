<#
.SYNOPSIS
Checks the production sand rotations against the captured dry/sinking boundary.
.DESCRIPTION
The north capture anchors the installed layout's four rotations. Geometry checks
prove each rotated dry quarter and Uzu shelter fit inside the arena; live regional
behavior and movement timing remain separate acceptance work.
#>
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../../Dungeons/VariantDungeons/MountRokkon.ShishioQuicksand.cs') -Raw
$start = $source.IndexOf('    private static (Vector3 origin, float heading) ShishioSandGeometry(')
if ($start -lt 0) { throw 'Missing production sand geometry.' }
$open = $source.IndexOf('{', $start)
$depth = 1
$end = $open + 1
while ($depth -gt 0 -and $end -lt $source.Length) {
    if ($source[$end] -eq '{') { $depth++ }
    if ($source[$end] -eq '}') { $depth-- }
    $end++
}
if ($depth -ne 0) { throw 'Unbalanced sand geometry.' }
$method = $source.Substring($start, $end - $start)
$fixture = @'
using System;
using System.Numerics;
using System.Linq;
public static class SandChecks {
    private static readonly Vector3 ShishioCenter = new(-40,360,-300);
    private static int count;
    private static void Check(bool value,string why) { if(!value) throw new Exception(why); count++; }
    private static bool InSand(Vector3 p,uint id) {
        var s=ShishioSandGeometry(id); var d=p-s.origin;
        var forward=d.X*Math.Sin(s.heading)+d.Z*Math.Cos(s.heading);
        var side=d.X*Math.Cos(s.heading)-d.Z*Math.Sin(s.heading);
        return Math.Abs(side)<=20.5 && forward>=-.5 && forward<=30.5;
    }
    public static int Run() {
        Check(!InSand(new(-47.887f,360,-286.024f),9835903),"Captured stationary point remained dry");
        Check(InSand(new(-52.764f,360,-290.803f),9835903),"Captured circle escape acquired Six Fulms Under");
        var dry=new Vector3[] {new(-55,360,-300),new(-40,360,-285),new(-25,360,-300),new(-40,360,-315)};
        for(var i=0;i<4;i++) {
            var id=(uint)(9835902+i);
            var shelter=ShishioCenter-(dry[i]-ShishioCenter)*1.2f;
            Check(InSand(ShishioCenter,id),"Three-quarter sand covers center");
            Check(!InSand(dry[i],id),"Rotated dry quarter remains available");
            Check(InSand(shelter,id),"Uzu shelter enters sand");
            Check(Vector3.Distance(shelter,ShishioCenter)<19.5 && Vector3.Distance(shelter,dry[i])>23.5,
                "Uzu shelter clears the blast inside the arena");
        }
        // The captured successive Yoki grids must each retain a reachable dry
        // destination after the arena and ground padding are applied together.
        var grids=new[] {
            new Vector2[] {new(-38.057f,-314.474f),new(-22.057f,-314.474f),new(-46.057f,-306.474f),new(-30.057f,-306.474f),new(-54.057f,-298.474f),new(-38.057f,-298.474f),new(-46.057f,-290.474f),new(-30.057f,-290.474f),new(-38.057f,-282.474f),new(-22.057f,-282.474f)},
            new Vector2[] {new(-54.057f,-282.474f),new(-39.616f,-279.114f),new(-54.057f,-314.474f),new(-38.24f,-311.805f),new(-28.745f,-304.486f),new(-44.694f,-304.518f),new(-36.812f,-296.34f),new(-22.057f,-298.474f),new(-47.421f,-287.003f),new(-31.484f,-287.008f)}
        };
        foreach(var grid in grids) {
            var found=false;
            for(float x=-59.5f;x<=-20.5f;x+=.25f)
                for(float z=-319.5f;z<=-280.5f;z+=.25f) {
                    var p=new Vector3(x,360,z);
                    if(Vector3.Distance(p,ShishioCenter)<19.5f && !InSand(p,9835903) &&
                        grid.All(c=>Vector2.Distance(c,new Vector2(x,z))>6.5f)) found=true;
                }
            Check(found,"Captured Yoki wave retains a dry in-bounds pocket");
        }
        return count;
    }
'@
Add-Type -TypeDefinition ($fixture + "`n" + $method + "`n}")
"Passed $([SandChecks]::Run()) quicksand geometry checks."
