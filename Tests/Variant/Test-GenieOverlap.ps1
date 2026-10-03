# Replay the actual selector and polygons: the first two cannon waves must stay
# separate while both retain reachable space on the impending cleaves' safe side.
$ErrorActionPreference='Stop'
$source=Get-Content "$PSScriptRoot/../../Dungeons/VariantDungeons/MerchantsTale.cs" -Raw
function Extract([string]$name) {
    $m=[regex]::Match($source,'    private [^\r\n]+ '+$name+'\(')
    if(!$m.Success){throw "Missing $name"}
    $b=$source.IndexOf('{',$m.Index);$d=1;$i=$b+1
    while($d){if($source[$i] -eq '{'){$d++};if($source[$i] -eq '}'){$d--};$i++}
    $source.Substring($m.Index,$i-$m.Index).Replace('private ','public ')
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('DutyMechanic-Replay-' + [Guid]::NewGuid().ToString('N'))
New-Item $root -ItemType Directory -Force | Out-Null
Set-Content "$root\Tests.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'
$body=@'
int checks=0;
void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
var h=new Harness();var start=DateTime.UtcNow;
CastShape Make(uint id,double seconds,double x,double z,double heading)=>new(){Action=id,End=start.AddSeconds(seconds),X=x,Z=z,Heading=heading};
// October 2 capture, relative to (-750,-415). Keep ample future time so the replay
// is independent of wall-clock execution speed; inter-wave offsets are measured.
var first=new[]{-9d,3d,15d}.Select(z=>Make(43349,30,18,z,-Math.PI/2)).ToArray();
var second=new[]{-15d,-3d,9d}.Select(z=>Make(43349,32,-18,z,Math.PI/2)).ToArray();
var cleaves=new[]{Make(43353,34.6,0,0,Math.PI/2),Make(43354,34.6,0,0,-Math.PI/2)};
h.Add(first.Concat(second).Concat(cleaves));
Check(h.ActiveCasts().ToHashSet().SetEquals(first.Concat(cleaves)),"First wave plus paired cleaves only");
bool Inside(CastShape c,double x,double z){var p=Harness.ShapePoints(c);var a=-c.Heading;var pts=p.Select(v=>(X:c.X+v.X*Math.Cos(a)-v.Y*Math.Sin(a),Z:c.Z+v.X*Math.Sin(a)+v.Y*Math.Cos(a))).ToArray();bool inside=false;for(int i=0,j=pts.Length-1;i<pts.Length;j=i++){var u=pts[i];var v=pts[j];if((u.Z>z)!=(v.Z>z)&&x<(v.X-u.X)*(z-u.Z)/(v.Z-u.Z)+u.X)inside=!inside;}return inside;}
int SafeCount()=>Enumerable.Range(0,65).SelectMany(i=>Enumerable.Range(0,65).Select(j=>(X:-16d+i*.5,Z:-16d+j*.5))).Count(p=>!h.ActiveCasts().Any(c=>Inside(c,p.X,p.Z)));
Check(SafeCount()>0,"First-wave/cleave intersection remains navigable");
Check(h.ActiveCasts().Any(c=>Inside(c,12.9,1.45)),"Captured wrong-half position rejected before final handoff");
foreach(var c in first)c.End=start.AddSeconds(-1);
Check(h.ActiveCasts().ToHashSet().SetEquals(second.Concat(cleaves)),"Second wave plus paired cleaves only");
Check(SafeCount()>0,"Second-wave/cleave intersection remains navigable");
foreach(var c in second)c.End=start.AddSeconds(-1);
Check(h.ActiveCasts().ToHashSet().SetEquals(cleaves),"Only cleaves remain after cannons");
h.Voyage=true;Check(!h.ActiveCasts().Any(),"Voyage retains ownership");h.Voyage=false;
h.Clear();var fan=Make(44254,30,0,0,0);var later=Make(44255,32,0,0,0);h.Add(new[]{fan,later});
Check(h.ActiveCasts().SequenceEqual(new[]{fan}),"Alternating fans remain separate");
h.Clear();h.Add(first.Select(c=>{c.End=start.AddSeconds(30);return c;}).Concat(new[]{Make(43353,34.6,0,0,0)}));
Check(h.ActiveCasts().All(c=>c.Action==43349),"Unpaired cleave does not invent combined topology");
h.Clear();h.Add(first.Concat(cleaves.Select(c=>{c.End=start.AddSeconds(40);return c;})));
Check(h.ActiveCasts().All(c=>c.Action==43349),"Unrelated distant cleaves remain pending");
Console.WriteLine($"PASS {checks} Genie selection/geometry checks");
public record struct Vector2(float X,float Y);
public class CastShape {public uint Action;public DateTime End;public double X,Z,Heading;}
public class Harness {
 public bool Voyage;private bool VoyageActive()=>Voyage;
 private Dictionary<int,CastShape> _casts=new();private static Vector2[] RainbowCircle=Array.Empty<Vector2>();
 public void Add(IEnumerable<CastShape> a){foreach(var c in a)_casts[_casts.Count]=c;}public void Clear()=>_casts.Clear();
'@
Set-Content "$root\Program.cs" ($body+"`n"+(Extract 'ActiveCasts')+"`n"+(Extract 'ShapePoints')+"`n}")
dotnet run --project "$root\Tests.csproj"
if($LASTEXITCODE){throw 'Genie overlap replay failed'}
