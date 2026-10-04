<#
.SYNOPSIS
Replays Kember's disappearing Levinblossom omen through the recorded damage interval.
.DESCRIPTION
The October2 capture moved an active circle to world zero before the HP drop.
Validate the candidate's origin gate and fence against that detached evidence;
this does not certify movement through the simultaneous wind donut.
#>
$ErrorActionPreference='Stop'
$source=Get-Content "$PSScriptRoot/../../../Dungeons/VariantDungeons/MountRokkon.cs" -Raw
if($source -notmatch 'impact.Action is not \(LevinblossomStrike or Icebloom or 34196 or Clearout or 33775\) \|\| origin.Distance2D\(center\) < (\d+)') { throw 'Origin gate missing.' }
$scope=[double]$Matches[1]
if($source -notmatch 'if \(impact.Action is LevinblossomStrike or 34196\)\s+impact.End = now \+ actor.SpellCastInfo.RemainingCastTime \+ TimeSpan.FromMilliseconds\((\d+)\)') { throw 'Impact fence missing.' }
$retention=[double]$Matches[1]
$last=@(-776.18066,18.997375); $arena=@(-775,16); $player=@(-775.5926,17.504982)
function Distance($a,$b) { [math]::Sqrt([math]::Pow($a[0]-$b[0],2)+[math]::Pow($a[1]-$b[1],2)) }
if((Distance $last $arena) -ge $scope) { throw 'Valid captured origin rejected.' }
if((Distance @(0,0) $arena) -lt $scope) { throw 'Zero omen accepted.' }
if((Distance $last $player) -ge 3.25) { throw 'Replay no longer contains the damaged player.' }
# Native end45.581, first observed large HP loss46.793: preserve the full
# sampled interval rather than using the later vulnerability publication as damage time.
if($retention -lt 1212 -or $retention -ge 3000) { throw 'Fence misses damage or overlaps the next wave.' }
foreach($otherArena in @(@(737,220),@(47,93))) {
    if((Distance @(0,0) $otherArena) -lt $scope) { throw 'Zero is accepted in another Yozakura arena.' }
}
# Icebloom's two later placements lost their omens before the player was hit.
# The existing1.1s fence covers these impacts; only origin retention changes.
foreach($sample in @(@(-774.9905,19.485779,-774.21173,16.785398),@(-771.6945,16.80011,-772.1393,17.618507))) {
    $origin=@($sample[0],$sample[1]); $hit=@($sample[2],$sample[3])
    if((Distance $origin $arena) -ge $scope -or (Distance $origin $hit) -ge 6.5) { throw 'Icebloom damaged-point replay failed.' }
}
# Iron Rain: a safe edge position became unsafe when the omen vanished and
# ordinary combat moved inward. The old0.75s fence also preceded the1.13s hit.
$iron=@(-700.0076,552.9717); $before=@(-690.68744,557.8428); $after=@(-691.36725,557.63464)
if((Distance $iron $before) -le 10.5 -or (Distance $iron $after) -ge 10 -or $retention -lt 1130) { throw 'Iron Rain edge-return replay failed.' }
# Art of the Fireblossom: native end17:03:44.307, vulnerability45.631.
# Kember reached the padded edge but returned inside the actual9y circle before
# resolution. Keep this fence separate from helper flares and placed omens.
if($source -notmatch 'if \(impact.Action == 33640\)\s+impact.End = now \+ actor.SpellCastInfo.RemainingCastTime \+ TimeSpan.FromMilliseconds\((\d+)\)') { throw 'Fireblossom fence missing.' }
$fireRetention=[double]$Matches[1]
$fire=@(736.7512,216.69336); $safe=@(735.8014,226.32487); $hit=@(735.979,225.442)
if((Distance $fire $safe) -le 9.5 -or (Distance $fire $hit) -ge 9 -or $fireRetention -lt 1324 -or $fireRetention -gt 1800) { throw 'Fireblossom edge-return replay failed.' }
'Passed lightning, Icebloom, Iron Rain and Fireblossom captured timing/position replays.'

# Clearout's final native sample lost its omen at20:25:23, before the24.491 hit.
# Both opposing placed origins must survive zero; this does not validate escape selection.
foreach($claw in @(@(-700.0076,519.9817),@(-700.0076,559.9907))) {
    if((Distance $claw @(-700,540)) -ge $scope -or (Distance @(0,0) @(-700,540)) -lt $scope) { throw 'Clearout origin replay failed.' }
}
'Passed Clearout opposing-origin retention replay.'
