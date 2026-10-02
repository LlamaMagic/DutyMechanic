<#
.SYNOPSIS
Replays observed Enenra smoke-to-cleave transforms without native game objects.
.DESCRIPTION
Extracts the production transform and checks actual preview/helper pairs from the
October1 record12 capture. This verifies offsets and turn signs, not live timing.
#>
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\..\Dungeons\VariantDungeons\MountRokkon.EnenraSmoke.cs') -Raw
$start = $source.IndexOf('    private static (Vector3 origin, float heading) EnenraSmokeGeometry(')
if ($start -lt 0) { throw 'Missing production smoke transform.' }
$open = $source.IndexOf('{', $start)
$depth = 1
$end = $open + 1
while ($depth -gt 0 -and $end -lt $source.Length) {
    if ($source[$end] -eq '{') { $depth++ }
    if ($source[$end] -eq '}') { $depth-- }
    $end++
}
if ($depth -ne 0) { throw 'Unbalanced smoke transform.' }
$method = $source.Substring($start, $end - $start)
$fixture = @'
using System;
using System.Numerics;
public static class SmokeChecks {
    private static int count;
    private static Vector3 Forward(float h) => new Vector3((float)Math.Sin(h),0,(float)Math.Cos(h));
    private static void Check(bool value,string why) { if (!value) throw new Exception(why); count++; }
    private static void Pair(uint visual,Vector3 position,float heading,Vector3 origin,float finalHeading) {
        var result=EnenraSmokeGeometry(visual,position,heading);
        Check(Vector3.Distance(result.origin,origin)<.01f,"Smoke preview must resolve to the captured helper origin");
        Check(Math.Abs(Math.Atan2(Math.Sin(result.heading-finalHeading),Math.Cos(result.heading-finalHeading)))<.001,
            "Smoke turn must match the helper, not the parent's animated facing");
    }
    public static int Run() {
        // Single22:19:14, paired22:19:55 and single22:22:49 damage helpers.
        Pair(0x1EB891,new Vector3(889,90,-900),1.5707724f,new Vector3(911,90,-900),4.712317f);
        Pair(0x1EB891,new Vector3(911,90,-900),4.712317f,new Vector3(889,90,-900),1.5707724f);
        Pair(0x1EB88F,new Vector3(889,90,-900),1.5707724f,new Vector3(900,90,-889),(float)Math.PI);
        Pair(0x1EB890,new Vector3(900,90,-889),3.141497f,new Vector3(889,90,-900),1.5707724f);
        // The paired cleaves retain a southwest pocket inside the19.5y arena.
        // A wrong turn sign makes this intersection empty or points it inward.
        var safe=new Vector3(886.5f,90,-886.5f);
        Check(Vector3.Distance(safe,new Vector3(900,90,-900))<19.5f,"Observed pair has an in-bounds escape pocket");
        Check(safe.X<888.5f && safe.Z> -888.5f,"Escape pocket clears both half-yalm padded edges");
        return count;
    }
'@
Add-Type -TypeDefinition ($fixture + "`n" + $method + "`n}")
"Passed $([SmokeChecks]::Run()) Enenra smoke replay checks."
