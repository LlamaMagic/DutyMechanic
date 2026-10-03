<#
.SYNOPSIS
Replays Tengu's observed alternating lanes and checks launch/landing safety.
.DESCRIPTION
Extracts the candidate rectangle geometry so a sign regression cannot silently
turn a required knockback lane into a full-floor avoid. Uses the October 2
03:17 Grasp placements and checks both cardinal directions against arena walls.
.PARAMETER Candidate
Directory containing the Mount Rokkon candidate partials.
#>
param([string]$Candidate = "$PSScriptRoot\..\..\..\Dungeons\VariantDungeons")
$ErrorActionPreference = 'Stop'
$main = Get-Content "$Candidate\MountRokkon.cs" -Raw
$boss = Get-Content "$Candidate\MountRokkon.BossAvoidance.cs" -Raw
$rectangle = [regex]::Match($main, '    private static Vector2\[\] Rectangle\([\s\S]*?\};').Value
$lane = [regex]::Match($boss, '    private static Vector2\[\] TenguUnsafeLandingLane\(\) => [^;]+;').Value
if (!$rectangle -or !$lane) { throw 'Production geometry was not found.' }
$fixture = @'
using System;
using System.Linq;
using System.Numerics;
public static class RokkonBossOwnershipReplay {
    public static int Run() {
        var polygon=TenguUnsafeLandingLane();
        float minX=polygon.Min(p=>p.X), maxX=polygon.Max(p=>p.X);
        float minZ=polygon.Min(p=>p.Y), maxZ=polygon.Max(p=>p.Y);
        if (minZ!=6.5f || maxZ!=40.5f || minX!=-3 || maxX!=3)
            throw new Exception("Knockback must leave its padded launch segment available");
        // Observed grasp positions relative to Moko's fixed arena center.
        var grasps=new[]{new Vector2(-16,17.5f),new Vector2(10.5f,5),
            new Vector2(-16,7.6f),new Vector2(-6.5f,2.6f),new Vector2(-11.5f,-7.5f),
            new Vector2(15.9f,-7.5f),new Vector2(-16+13, -2.4f),
            new Vector2(15.9f,-17.4f),new Vector2(-2,-17.5f),new Vector2(4,12.5f)};
        int checkedLaunches=0, safeLaunches=0;
        for(int row=0;row<8;row++) {
            float z=-17.5f+5*row;
            int direction=row%2==0?-1:1;
            for(float along=.75f;along<=6.25f;along+=.25f) {
                var launch=new Vector2(-20*direction+along*direction,z);
                var landing=launch+new Vector2(33*direction,0);
                checkedLaunches++;
                if(Math.Abs(landing.X)>19.5f || Math.Abs(landing.Y)>19.5f)
                    throw new Exception("Accepted launch would land beyond padded arena");
                bool projected=grasps.Any(g=>Vector2.Distance(launch,g-new Vector2(33*direction,0))<5.5f);
                bool hit=grasps.Any(g=>Vector2.Distance(landing,g)<5.5f);
                if(projected!=hit) throw new Exception("Projected grasp differs from actual landing hazard");
                if(!hit && grasps.All(g=>Vector2.Distance(launch,g)>5.5f)) safeLaunches++;
            }
        }
        if(safeLaunches==0) throw new Exception("Combined overlap eliminated every launch/landing pair");
        return checkedLaunches;
    }
'@
Add-Type -TypeDefinition ($fixture + "`n" + $rectangle + "`n" + $lane + "`n}")
"Passed $([RokkonBossOwnershipReplay]::Run()) captured Tengu launch/landing checks."

# Exercise the production ownership transition against inert containers: this
# catches sticky suspension and per-pulse toggles without contacting the live bot.
function Read-Method([string]$Name) {
    $start = $boss.IndexOf("    private void $Name(")
    if ($start -lt 0) { throw "Missing ownership method $Name" }
    $open = $boss.IndexOf('{', $start)
    $depth = 1
    $end = $open + 1
    while ($depth -gt 0) {
        if ($boss[$end] -eq '{') { $depth++ }
        if ($boss[$end] -eq '}') { $depth-- }
        $end++
    }
    $boss.Substring($start, $end - $start)
}
# Exercise the new gap-closer lease with the real ownership methods so a stop
# cannot leave movement capabilities borrowed after the boss releases SideStep.
$lifecycle = @'
using System;
using System.Linq;
public enum CapabilityFlags { GapCloser }
public static class CapabilityManager {
    public static bool Held;
    public static void Update(object handle, CapabilityFlags flags, TimeSpan duration, string reason) => Held = true;
    public static void Clear(object handle, CapabilityFlags flags, string reason) => Held = false;
}
namespace ff14bot.AClasses { public class BotBase {} }
namespace DutyMechanic.Logging { public static class Logger { public static void Information(string s) {} } }
public class TestPluginContainer { public bool Enabled=true; }
public static class TreeRoot { public static bool IsRunning=true; }
public class RokkonOwnerReplay {
    private TestPluginContainer _mechanicPlugin=new TestPluginContainer();
    private static TestPluginContainer SidestepPlugin=new TestPluginContainer();
    private bool _sideStepSuspended, bossActive;
    private object _fireGapCloser = new();
    private bool _fireGapCloserOwned, fireActive;
    private record Impact(uint Action);
    private Impact[] PendingImpacts() => fireActive ? new[] { new Impact(33640) } : Array.Empty<Impact>();
    private bool InYozakura()=>bossActive;
    private bool InMoko()=>false;
    private bool InGorai()=>false;
    private bool InShishio()=>false;
    private bool InEnenra()=>false;
    private void Check(bool condition,string reason) {if(!condition)throw new Exception(reason);}
    public static void Run() {
        var owner=new RokkonOwnerReplay();
        owner.UpdateBossAvoidanceOwner();
        owner.Check(SidestepPlugin.Enabled,"Trash must keep SideStep");
        owner.bossActive=true;
        owner.UpdateBossAvoidanceOwner();
        owner.Check(!SidestepPlugin.Enabled && owner._sideStepSuspended,"Boss must acquire suspension");
        owner.UpdateBossAvoidanceOwner();
        owner.Check(!SidestepPlugin.Enabled,"Unchanged boss must retain suspension");
        owner.fireActive=true;
        owner.UpdateBossAvoidanceOwner();
        owner.Check(CapabilityManager.Held,"Pending Fireblossom blocks gap closers");
        owner.fireActive=false;
        owner.UpdateBossAvoidanceOwner();
        owner.Check(!CapabilityManager.Held,"Resolved Fireblossom releases gap closers");
        owner.fireActive=true;
        owner.UpdateBossAvoidanceOwner();
        owner.bossActive=false;
        owner.UpdateBossAvoidanceOwner();
        owner.Check(!CapabilityManager.Held,"Boss exit releases an active gap-closer lease");
        owner.Check(SidestepPlugin.Enabled && !owner._sideStepSuspended,"Boss end must restore");
        SidestepPlugin.Enabled=false;
        owner.bossActive=true;
        owner.UpdateBossAvoidanceOwner();
        owner.RestoreBossSideStep(null);
        owner.Check(!SidestepPlugin.Enabled,"Do not enable an initially disabled plugin");
        SidestepPlugin.Enabled=true;
        owner.UpdateBossAvoidanceOwner();
        owner.RestoreBossSideStep(null);
        owner.Check(SidestepPlugin.Enabled,"Synchronous stop must restore without another pulse");
        owner.Check(!CapabilityManager.Held,"Stop releases the gap-closer lease without a pulse");
        owner.UpdateBossAvoidanceOwner();
        owner._mechanicPlugin.Enabled=false;
        owner.UpdateBossAvoidanceOwner();
        owner.Check(SidestepPlugin.Enabled,"Disabled mechanic owner must release");
        owner._mechanicPlugin.Enabled=true;
        owner.UpdateBossAvoidanceOwner();
        TreeRoot.IsRunning=false;
        owner.UpdateBossAvoidanceOwner();
        owner.Check(SidestepPlugin.Enabled,"Stopped tree must release");
    }
'@
Add-Type -TypeDefinition ($lifecycle + "`n" + (Read-Method 'UpdateBossAvoidanceOwner') + "`n" + (Read-Method 'RestoreBossSideStep') + "`n" + (Read-Method 'ReleaseFireGapCloser') + "`n}")
[RokkonOwnerReplay]::Run()
'Passed twelve boss/trash/stop/disable and gap-closer ownership checks.'
