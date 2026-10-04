# Replay production Nights selection and geometry against captured orientations and
# offset omens. The combined opening must contain both hazards with reachable space,
# while later sweeps and unknown-omen fallbacks retain their original sequence.
$ErrorActionPreference='Stop'
$source=Get-Content "$PSScriptRoot/../../Dungeons/VariantDungeons/MerchantsTale.cs" -Raw
function Extract([string]$name) {
 $m=[regex]::Match($source,'    private [^\r\n]+ '+$name+'\(')
 if(!$m.Success){throw "Missing $name"}
 $b=$source.IndexOf('{',$m.Index);$d=1;$i=$b+1
 while($d){if($source[$i] -eq '{'){$d++};if($source[$i] -eq '}'){$d--};$i++}
 $source.Substring($m.Index,$i-$m.Index).Replace('private ','public ')
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('DutyMechanic-PariNights-' + [Guid]::NewGuid().ToString('N'))
New-Item $root -ItemType Directory -Force | Out-Null
Set-Content "$root\Tests.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>'
$body=@"
int checks=0;
void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
var h=new Harness();
bool Inside(PariShape s,double x,double z){var a=-s.Heading;var pts=Harness.PariShapePoints(s).Select(v=>(X:s.Position.X+v.X*Math.Cos(a)-v.Y*Math.Sin(a),Z:s.Position.Z+v.X*Math.Sin(a)+v.Y*Math.Cos(a))).ToArray();bool yes=false;for(int i=0,j=pts.Length-1;i<pts.Length;j=i++){var u=pts[i];var v=pts[j];if((u.Z>z)!=(v.Z>z)&&x<(v.X-u.X)*(z-u.Z)/(v.Z-u.Z)+u.X)yes=!yes;}return yes;}
foreach(float heading in new[]{0f,(float)Math.PI}) foreach(float line in new[]{(float)Math.PI/2,-(float)Math.PI/2}) foreach(float jitter in new[]{-.04f,0,.04f}) {
 h._nightInitialHeading=line+jitter;h._nightOrigin=new(-759.7f,-54,-805.2f);h._nightHeadings[0]=heading;h._nightFinish=DateTime.UtcNow.AddSeconds(10);
 var s=h.ActiveNightShape().Single();Check(s.Kind==PariShapeKind.Half,"One combined opening region");
 var beam=new PariShape{Kind=PariShapeKind.NightLine,Position=h._nightOrigin,Heading=h._nightInitialHeading};
 var half=new PariShape{Kind=PariShapeKind.Half,Position=Harness.PariCenter,Heading=heading};
 bool containment=true;int safe=0;
 for(double x=-778;x<=-742;x+=.5)for(double z=-823;z<=-787;z+=.5){bool combined=Inside(s,x,z);if((Inside(beam,x,z)||Inside(half,x,z))&&!combined)containment=false;if(!combined)safe++;}
 Check(containment,"Combined shape contains beam and first sweep");Check(safe>100,"Reachable safe half remains");
 h._nightFinish=DateTime.UtcNow.AddSeconds(-1.5);Check(h.ActiveNightShape().Single().RearPadding>2.5,"Staging persists between beam and first sweep");
}
h._nightFinish=DateTime.UtcNow.AddSeconds(-4);h._nightHeadings[1]=1;
var next=h.ActiveNightShape().Single();Check(next.Heading==1&&next.RearPadding==.5f,"Second sweep unchanged");
h._nightFinish=DateTime.UtcNow.AddSeconds(-7);Check(!h.ActiveNightShape().Any(),"Sequence expires");
h._nightFinish=DateTime.UtcNow.AddSeconds(10);h._nightLineReady=false;Check(!h.ActiveNightShape().Any(),"No invented geometry without omen");
h._nightLineReady=true;h._nightHeadings.Clear();Check(h.ActiveNightShape().Single().Kind==PariShapeKind.NightLine,"Unknown preview keeps line fallback");
h._nightHeadings[0]=0;h._nightInitialHeading=0;Check(h.ActiveNightShape().Single().Kind==PariShapeKind.NightLine,"Nonperpendicular omen keeps fallback");
h._nightInitialHeading=(float)Math.PI/2;h._nightOrigin=new(-750,-54,-805);Check(h.ActiveNightShape().Single().Kind==PariShapeKind.NightLine,"Displaced omen keeps fallback");
// Captured second-sweep position: Jolt must yield only inside the published half.
h._nightFinish=DateTime.UtcNow.AddSeconds(-4);h._nightHeadings[1]=0;
Check(h.PariNightCastEscape(7503,true,new(-752.821f,-54,-801.656f)),"Captured Jolt stall yields during second sweep");
Check(!h.PariNightCastEscape(7503,false,new(-752.821f,-54,-801.656f)),"No native escape keeps Jolt");
Check(!h.PariNightCastEscape(7503,true,new(-752.821f,-54,-809)),"Safe side keeps Jolt");
foreach(uint action in new uint[]{7514,7510,7511,7505,7507,46939})Check(!h.PariNightCastEscape(action,true,new(-752.821f,-54,-801.656f)),"Nights exception excludes other actions: "+action);
h._nightFinish=DateTime.UtcNow.AddSeconds(-10);Check(!h.PariNightCastEscape(7503,true,new(-752.821f,-54,-801.656f)),"Expired sweep keeps Jolt");
Console.WriteLine("PASS "+checks+" Pari opening selection/geometry and cast checks");
public record struct Vector2(float X,float Y);
public record struct Vector3(float X,float Y,float Z){public float Distance2D(Vector3 b)=>(float)Math.Sqrt((X-b.X)*(X-b.X)+(Z-b.Z)*(Z-b.Z));}
public enum PariShapeKind{Charge,Cross,Half,NightLine}
public class PariShape{public PariShapeKind Kind;public Vector3 Position;public float Heading,Length;public float RearPadding=.5f;public DateTime End;}
public class Harness {
 public static Vector3 PariCenter=new(-760,-54,-805);
 public int _nightCount=2;public bool _nightLineReady=true;public DateTime _nightFinish;
 public Vector3 _nightOrigin;public float _nightInitialHeading;public Dictionary<int,float> _nightHeadings=new();
"@
Set-Content "$root\Program.cs" ($body+"`n"+(Extract 'ActiveNightShape')+"`n"+(Extract 'CombinedNightOpening')+"`n"+(Extract 'PariShapePoints')+"`n"+(Extract 'PariNightCastEscape')+"`n}")
dotnet run --project "$root\Tests.csproj"
if($LASTEXITCODE){throw 'Pari opening replay failed'}
