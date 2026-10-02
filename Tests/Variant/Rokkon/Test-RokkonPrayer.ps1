<#
.SYNOPSIS
Replays cone containment and tower-transfer regressions without a game process.
.DESCRIPTION
Compiles the actual scalar geometry and timed-transfer methods from the encounter source. The fixture
replaces only Clio's vector with System.Numerics; it does not duplicate the formulas.
This checks unsafe intermediate travel and padding, not native timing or live acceptance.
#>
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\..\..\Dungeons\VariantDungeons\MountRokkon.GoraiPrayer.cs') -Raw
function Read-Method([string]$name) {
    $start = $source.IndexOf('    private static bool ' + $name + '(')
    if ($start -lt 0) { throw "Missing production method $name" }
    $open = $source.IndexOf('{', $start)
    $depth = 1
    $end = $open + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw 'Unbalanced source method' }
    $source.Substring($start, $end - $start)
}
$methods = (Read-Method 'InsideGoraiCone') + "`n" + (Read-Method 'GoraiPrayerSegmentSafe') + "`n" + (Read-Method 'GoraiPrayerTimedSegmentSafe')
$fixture = @'
using System;
using System.Linq;
using System.Numerics;
static class XzDistance {
    public static float Distance2D(this Vector3 a, Vector3 b) => new Vector2(a.X-b.X,a.Z-b.Z).Length();
}
public static class RokkonPrayerChecks {
    private sealed class Impact { internal Vector3 Location; internal float Heading; internal DateTime End; }
    private static int count;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); count++; }
    public static int Run() {
        var origin = new Vector3(741,91,-190);
        for (var rotation=0; rotation<8; rotation++) {
            var h = rotation * (float)Math.PI/4;
            var f = new Vector3((float)Math.Sin(h),0,(float)Math.Cos(h));
            var side = new Vector3(f.Z,0,-f.X);
            var cone = new Impact { Location=origin, Heading=h };
            Check(InsideGoraiCone(origin+f*10,cone), "Cone center must be unsafe");
            Check(!InsideGoraiCone(origin-f*10,cone), "Rear point must remain clear");
            Check(InsideGoraiCone(origin,cone), "The cone apex is not a safe hole");
            Check(!GoraiPrayerSegmentSafe(origin+side*10,origin-side*10,new[]{cone}), "Clear endpoints cannot cross the apex");
            Check(GoraiPrayerSegmentSafe(origin-f*10-side*2,origin-f*10+side*2,new[]{cone}), "A real rear corridor must remain reachable");
            var edge = origin + f*10 + side*(10*(float)Math.Tan(Math.PI/8)+.2f);
            Check(InsideGoraiCone(edge,cone), "Nominally clear edge needs half-yalm padding");
        }
        // Each four-cone wave leaves real central staging sectors; combining both
        // waves would erase them and reproduce the overlap deadlock.
        foreach (var offset in new[]{ Math.PI/8, 3*Math.PI/8 }) {
            var wave = Enumerable.Range(0,4).Select(i=>new Impact{Location=origin,Heading=(float)(offset+i*Math.PI/2)}).ToArray();
            var safe = Enumerable.Range(0,64).Select(i=>origin+new Vector3(2.5f*(float)Math.Sin(i*Math.PI/32),0,2.5f*(float)Math.Cos(i*Math.PI/32)))
                .Where(p=>wave.All(c=>!InsideGoraiCone(p,c))).ToArray();
            Check(safe.Length>0, "Earliest wave must retain central staging space");
            Check(safe.All(p=>p.Distance2D(origin)>1.4f), "Staging may not invent an apex hole");
        }
        // Replay21:23:25.00: the south soak is complete, north expires30.04,
        // and the two cone fences end26.05/28.05. Static segment rejection
        // deadlocks south of the apex; an early transfer can be safe at both hits.
        var now = new DateTime(2026,10,1,21,23,25,0,DateTimeKind.Utc);
        var timed = Enumerable.Range(0,8).Select(i=>new Impact {
            Location=origin, Heading=(float)(Math.PI/8+(i%4)*Math.PI/2+(i/4)*Math.PI/4),
            End=now.AddSeconds(i<4 ? 1.05 : 3.05)
        }).ToArray();
        var south = new Vector3(740.25977f,91,-181.44257f);
        var north = new Vector3(739,91,-201);
        Check(!GoraiPrayerSegmentSafe(south,north,timed), "A permanent-wall model rejects this transfer");
        Check(GoraiPrayerTimedSegmentSafe(south,north,timed,now), "Captured early transfer clears both timed cone windows");
        Check(!GoraiPrayerTimedSegmentSafe(origin,origin,timed,now), "Timing cannot make a stationary apex safe");
        Check(!GoraiPrayerTimedSegmentSafe(south,north,timed,now.AddSeconds(2.4)), "Late crossing cannot ignore an imminent cone");
        return count;
    }
'@
Add-Type -TypeDefinition ($fixture + "`n" + $methods + "`n}")
"Passed $([RokkonPrayerChecks]::Run()) Rokkon prayer geometry checks."
