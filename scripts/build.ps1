[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("debug", "release", "push")]
    [string]$Mode = "release",

    [ValidateSet("q", "quiet", "m", "minimal", "n", "normal", "d", "detailed", "diag", "diagnostic")]
    [string]$Verbosity = "minimal"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$localConfig = Join-Path $repoRoot "Config.Build.user.props"
if (-not (Test-Path -LiteralPath $localConfig)) {
    throw "Missing Config.Build.user.props. Copy Config.Build.user.props.template to Config.Build.user.props and set PEAKGameRootDir for this machine."
}

$solution = Join-Path $repoRoot "BuddyClimb.slnx"
$normalizedMode = $Mode.ToLowerInvariant()
$configuration = if ($normalizedMode -eq "debug") { "Debug" } else { "Release" }

$buildArgs = @(
    "build",
    $solution,
    "-c",
    $configuration,
    "-v",
    $Verbosity
)

if ($normalizedMode -eq "push") {
    $buildArgs += "-p:PublishTS=true"
}

& dotnet @buildArgs

exit $LASTEXITCODE
