[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryPath,

    [string]$Revision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This validator protects the developer-only diagnostic facility from broad cleanup passes while
# also preventing a high-volume diagnostic build from reaching GitHub.
$resolvedRepository = (Resolve-Path -LiteralPath $RepositoryPath).Path
$pluginRelativePath = 'DutyMechanicPlugin.cs'
$loggingRelativePath = 'Helpers/LoggingHelpers.cs'

function Get-SourceText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($Revision)) {
        $fullPath = Join-Path $resolvedRepository $RelativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Required DutyMechanic source file is missing: $fullPath"
        }

        return Get-Content -LiteralPath $fullPath -Raw
    }

    $source = & git -C $resolvedRepository show "$Revision`:$RelativePath" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read $RelativePath from revision '$Revision': $($source -join [Environment]::NewLine)"
    }

    return $source -join [Environment]::NewLine
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw $FailureMessage
    }
}

function Assert-Regex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if (-not [regex]::IsMatch($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw $FailureMessage
    }
}

$pluginSource = Get-SourceText -RelativePath $pluginRelativePath
$loggingSource = Get-SourceText -RelativePath $loggingRelativePath

Assert-Contains $pluginSource 'private const bool EnableMechanicDiagnostics = false;' `
    'DutyMechanicPlugin.cs must retain the internal diagnostic switch with a false default.'

if ($pluginSource.IndexOf('EnableMechanicDiagnostics = true;', [StringComparison]::Ordinal) -ge 0) {
    throw 'EnableMechanicDiagnostics is true. Published DutyMechanic revisions must keep it false.'
}

Assert-Regex $pluginSource 'private\s+async\s+Task<bool>\s+RunTrust\(\)[\s\S]*?LoggingHelpers\.UpdateMechanicDiagnostics\(EnableMechanicDiagnostics\);' `
    'The bot-thread encounter path no longer updates the shared diagnostic collector.'
Assert-Regex $pluginSource 'OnDisabled\(\)[\s\S]*?LoggingHelpers\.UpdateMechanicDiagnostics\(false\);' `
    'The plugin-disable path no longer clears diagnostic state.'
Assert-Regex $pluginSource 'OnBotStop\([^)]*\)[\s\S]*?LoggingHelpers\.UpdateMechanicDiagnostics\(false\);' `
    'The bot-stop path no longer clears diagnostic state.'

Assert-Contains $loggingSource 'public static void UpdateMechanicDiagnostics(bool enabled)' `
    'LoggingHelpers.UpdateMechanicDiagnostics(bool) is missing.'
# The retained-history refactor expires entries independently because long and short
# casts finish out of insertion order. Check the replacement's bounds and lifecycle,
# not the old Queue type name; behavioral expiry/overflow tests live beside this check.
$historySource = Get-SourceText -RelativePath 'Helpers/MechanicDiagnosticHistory.cs'
Assert-Contains $loggingSource 'MechanicDiagnosticHistory<string> RecentMechanicCasts' 'Bounded scalar cast history is missing.'
Assert-Contains $loggingSource 'MechanicContextMaximumEntries' 'Cast capacity bound is missing.'
Assert-Contains $loggingSource 'RecentMechanicCasts.Clear();' 'Cast history lifecycle reset is missing.'
Assert-Contains $loggingSource 'RecentMovement.Clear();' 'Movement history lifecycle reset is missing.'
Assert-Contains $loggingSource 'CastExpiry(nowUtc' 'Cast-finish expiry is no longer applied.'
Assert-Contains $historySource 'entries.Count == capacity' 'History capacity enforcement is missing.'
Assert-Contains $historySource 'entries.RemoveAt(0)' 'Oldest-entry eviction is missing.'
Assert-Contains $historySource 'entries.RemoveAll(e => e.ExpiresAtUtc < now)' 'Independent expiry is missing.'
Assert-Contains $historySource 'Math.Clamp' 'Malformed duration bound is missing.'
Assert-Contains $historySource 'Dropped++' 'Capacity loss must remain visible in diagnostics.'
foreach ($marker in @(
    '[MechanicDiag] Enabled',
    '[MechanicDiag] CAST_START',
    '[MechanicDiag] VULNERABILITY_GAIN',
    '[MechanicDiag] PLAYER_DEATH'
)) {
    Assert-Contains $loggingSource $marker "Required diagnostic marker is missing: $marker"
}

$sourceLabel = if ([string]::IsNullOrWhiteSpace($Revision)) { 'working tree' } else { "revision $Revision" }
Write-Output "DutyMechanic diagnostic invariant passed for $sourceLabel."
