# Compile the actual handoff methods with fake plugin/avoidance surfaces. This
# regression harness verifies ownership isolation without touching any live host.
$ErrorActionPreference='Stop'
$source=Get-Content (Join-Path $PSScriptRoot '../../Dungeons/VariantDungeons/MerchantsTale.cs') -Raw
function Extract-Method([string]$name) {
    $match=[regex]::Match($source,'    private [^\r\n]+ '+$name+'\(')
    $start=if($match.Success){$match.Index}else{-1}
    if($start -lt 0){throw "Missing $name"}
    $brace=$source.IndexOf('{',$start); $depth=1; $i=$brace+1
    while($depth -gt 0){if($source[$i] -eq '{'){$depth++};if($source[$i] -eq '}'){$depth--};$i++}
    $source.Substring($start,$i-$start).Replace('private ','public ')
}
$methods=@('SuspendMerchantSideStep','RestoreMerchantSideStep','MerchantSideStepChanged','ActiveNativeBossHazards') | ForEach-Object {Extract-Method $_}
$root=Join-Path ([IO.Path]::GetTempPath()) ('DutyMechanic-MerchantOwnership-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
Set-Content "$root\Tests.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'
$body=@'
using System.ComponentModel;
var checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
var owned=new AvoidInfo();var other=new AvoidInfo();var zone=new AvoidInfo();
var impl=new FakeSideStep(owned,zone);var plugin=new PluginContainer(impl,true);
var test=new Harness(plugin);AvoidanceManager.Live.UnionWith(new[]{owned,other,zone});
test.SuspendMerchantSideStep();
Check(!plugin.Enabled,"SideStep is suspended");
Check(AvoidanceManager.Live.SetEquals(new[]{other}),"Only SideStep avoids are removed");
test.SuspendMerchantSideStep();Check(plugin.Disables==1,"Acquisition is idempotent");
test.RestoreMerchantSideStep();Check(plugin.Enabled,"Prior enabled state restored");
test.RestoreMerchantSideStep();Check(plugin.Enables==1,"Restore is idempotent");
var disabled=new PluginContainer(new FakeSideStep(),false);var d=new Harness(disabled);
d.SuspendMerchantSideStep();d.RestoreMerchantSideStep();Check(!disabled.Enabled,"Previously disabled stays disabled");
var alien=new PluginContainer(new object(),true);var a=new Harness(alien);bool rejected=false;
try{a.SuspendMerchantSideStep();}catch(InvalidOperationException){rejected=true;}
Check(rejected && alien.Enabled,"Unknown layout preserves plugin state");
test.SuspendMerchantSideStep();plugin.Enabled=false;test.RestoreMerchantSideStep();
Check(!plugin.Enabled,"Explicit user disable supersedes restoration");
var first=new NativeBossHazard(45486,100);var simultaneous=new NativeBossHazard(45486,100.3);var next=new NativeBossHazard(45486,103);
var overlap=new NativeBossHazard(45512,102);test.Add(first,simultaneous,next,overlap);
Check(test.ActiveNativeBossHazards().ToHashSet().SetEquals(new[]{first,simultaneous,overlap}),"Only earliest Sparks group plus simultaneous independent hazards");
first.End=simultaneous.End=DateTime.UtcNow.AddSeconds(-1);
Check(test.ActiveNativeBossHazards().ToHashSet().SetEquals(new[]{next,overlap}),"Next Sparks wave becomes active after prior impact tail");
var ship=new NativeBossHazard(47042,100);test.Add(ship);test.Voyage=true;
Check(!test.ActiveNativeBossHazards().Contains(ship),"Validated ship preview suppresses fallback rectangle");
test.Voyage=false;Check(test.ActiveNativeBossHazards().Contains(ship),"Unrecognized ship topology keeps cast fallback");
Console.WriteLine($"PASS {checks} ownership lifecycle checks");
public record Spec(uint Action);
public class NativeBossHazard {public Spec Spec;public DateTime Finish,End;public NativeBossHazard(uint action,double seconds){Spec=new(action);Finish=DateTime.UtcNow.AddSeconds(seconds);End=Finish.AddSeconds(1);}}
public class AvoidInfo {}
public static class AvoidanceManager { public static HashSet<AvoidInfo> Live=new();public static void RemoveAvoid(AvoidInfo a)=>Live.Remove(a); }
public class FakeSideStep {
 private List<AvoidInfo> _tracked;private List<AvoidInfo> _zoneTracked;
 public FakeSideStep(params AvoidInfo[] a){_tracked=a.Take(1).ToList();_zoneTracked=a.Skip(1).ToList();}
 public void Disabled()=>_tracked.Clear();
}
public class PluginContainer {
 public object Plugin;bool enabled;public int Enables,Disables;public event PropertyChangedEventHandler PropertyChanged;
 public PluginContainer(object plugin,bool on){Plugin=plugin;enabled=on;}
 public bool Enabled {get=>enabled;set{if(enabled!=value){if(value)Enables++;else{Disables++;(Plugin as FakeSideStep)?.Disabled();}}enabled=value;PropertyChanged?.Invoke(this,new("Enabled"));}}
}
namespace ff14bot.Helpers {public static class Logging {public static void Write(string message){}}}
public class Harness {
 private PluginContainer _merchantSideStep;private bool _sideStepSuspended,_changingSideStep;
 private Dictionary<ulong,NativeBossHazard> _nativeBossHazards=new();private uint[] VoyageActions={47042};public bool Voyage;
 private bool VoyageActive()=>Voyage;public void Add(params NativeBossHazard[] hazards){foreach(var h in hazards)_nativeBossHazards[(ulong)_nativeBossHazards.Count]=h;}
 public Harness(PluginContainer p){_merchantSideStep=p;p.PropertyChanged+=MerchantSideStepChanged;}
'@
Set-Content "$root\Program.cs" ($body+"`n"+($methods -join "`n")+"`n}")
dotnet run --project "$root\Tests.csproj"
if($LASTEXITCODE -ne 0){throw 'Ownership regression failed'}
# Verify only entry registers the listener; accidentally registering during exit
# leaves a stale dungeon instance attached after a profile reload.
if(([regex]::Matches($source,'RegisterNativeBossAvoidance\(\);')).Count -ne 1){throw 'Duplicate native registration'}
if($source.Contains('LlamaLibrary.Helpers.SideStep.Override')){throw 'Obsolete per-action overrides remain'}
foreach($id in @(44252,44344,45765,45758,45510,45512,45486,46619,46639,46640,46633,45605,47398,45802)) {
    if($source -notmatch "new\($id,"){throw "Unmigrated dependency $id"}
}
'PASS registration and 14 remaining mechanic coverage checks'
