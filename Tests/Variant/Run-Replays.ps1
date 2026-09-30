<#
.SYNOPSIS
Runs the Variant geometry and sequence regressions without RebornBuddy.
.DESCRIPTION
Extracts the pure helpers from the owning dungeon files so tests exercise the
published implementation. Generated C# stays outside the plugin tree because RB
recursively compiles source files. Fixtures use .cs.test for the same reason.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path ([IO.Path]::GetTempPath()) ('DutyMechanic-VariantReplay-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$sources = @{
    SildihnSubterrane = 'SilkieKnockbackPlan'
    AloaloIsland = 'AloaloStatueGazePlan'
}
foreach ($name in $sources.Keys) {
    $path = Join-Path $repository "Dungeons\VariantDungeons\$name.cs"
    $source = Get-Content -LiteralPath $path -Raw
    # Pure helpers follow the dungeon class. Stop on layout changes rather than
    # silently testing a copied algorithm or accidentally loading native RB APIs.
    $marker = "internal static class $($sources[$name])"
    $start = $source.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $source.IndexOf($marker, $start + 1, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected one helper boundary in $path"
    }
    $header = "using System.Numerics; using PlanPoint = System.Numerics.Vector2; namespace DutyMechanic.Dungeons;`n"
    Set-Content -LiteralPath (Join-Path $output "$name.cs") -Value ($header + $source.Substring($start))
}
$fixtures = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot '*.cs.test'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Source extraction above keeps the replay coupled to production geometry. -->
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Compile Include="$fixtures" /></ItemGroup>
</Project>
"@
$projectPath = Join-Path $output 'Replay.csproj'
Set-Content -LiteralPath $projectPath -Value $project
& dotnet run --project $projectPath
if ($LASTEXITCODE -ne 0) { throw "Variant replay failed; generated sources: $output" }
