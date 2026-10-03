# Compile the maintained decision method, replaying the captured row/cone windows.
# Negative cases protect healing and normal casting from an overbroad interrupt.
$ErrorActionPreference='Stop'
$source=Get-Content "$PSScriptRoot/../../Dungeons/VariantDungeons/MerchantsTale.cs" -Raw
$match=[regex]::Match($source,'    private string SwordmasterCastEscape\(')
if(!$match.Success){throw 'Missing escape selector'}
$begin=$source.IndexOf('{',$match.Index);$depth=1;$end=$begin+1
while($depth){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--};$end++}
$method=$source.Substring($match.Index,$end-$match.Index).Replace('private string','public string')
$root=Join-Path ([IO.Path]::GetTempPath()) ('DutyMechanic-Replay-' + [Guid]::NewGuid().ToString('N'))
New-Item $root -ItemType Directory -Force | Out-Null
Set-Content "$root\Tests.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'
$body=@'
int checks=0;
void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
var now=new DateTime(2026,10,2,23,37,50,DateTimeKind.Utc);
var h=new Harness();
Check(h.SwordmasterCastEscape(now,7510,true)==null,"No forecast is not an interrupt");
h.Wounds.Add(1,new(){Action=46622,End=now.AddSeconds(2.9)});
Check(h.SwordmasterCastEscape(now,7510,true)=="Waiting Wounds","Verfire yields during captured row escape");
Check(h.SwordmasterCastEscape(now,7511,true)=="Waiting Wounds","Same immobile Verstone yields");
Check(h.SwordmasterCastEscape(now,7510,false)==null,"Safe staging retains casting");
foreach(uint action in new uint[]{7514,46939,46941,7505,7507,7503})
 Check(h.SwordmasterCastEscape(now,action,true)==null,"Healing, mitigation, instant and unconfirmed casts retained: "+action);
Check(h.SwordmasterCastEscape(now.AddSeconds(2.9),7510,true)==null,"Deadline releases row ownership");
h.Wounds.Clear();h.Casts.Add(1,new(){Action=46614,End=now.AddSeconds(1)});
Check(h.SwordmasterCastEscape(now,7511,true)=="Lash of Light","Verstone yields during captured Lash escape");
Check(h.SwordmasterCastEscape(now,7510,true)=="Lash of Light","Verfire shares the stationary damage-cast guard");
Check(h.SwordmasterCastEscape(now.AddSeconds(1),7511,true)==null,"Expired cone cannot cancel");
h.Casts[1].Action=47994;
Check(h.SwordmasterCastEscape(now,7511,true)==null,"Crusher does not inherit Lash cancellation");
h.Confluence.Add(1,new(){Action=46628,End=now.AddSeconds(2)});
Check(h.SwordmasterCastEscape(now,7511,true)=="Confluence","Existing Confluence coverage preserved");
h.Confluence.Clear();h.Casts.Clear();
Check(h.SwordmasterCastEscape(now,7511,true)==null,"Reset releases all encounter windows");
Console.WriteLine($"PASS {checks} Swordmaster escape checks");
public class CastShape {public uint Action;public DateTime End;}
public class Harness {
 private Dictionary<int,CastShape> _swordmasterWounds=new(),_swordmasterCasts=new(),_swordmasterConfluence=new();
 public Dictionary<int,CastShape> Wounds=>_swordmasterWounds;
 public Dictionary<int,CastShape> Casts=>_swordmasterCasts;
 public Dictionary<int,CastShape> Confluence=>_swordmasterConfluence;
'@
Set-Content "$root\Program.cs" ($body+"`n"+$method+"`n}")
dotnet run --project "$root\Tests.csproj"
if($LASTEXITCODE){throw 'Swordmaster escape replay failed'}
