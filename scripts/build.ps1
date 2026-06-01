[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration,

    [switch]$Deploy,

    [ValidateSet("quiet", "minimal", "normal", "detailed", "diagnostic")]
    [string]$Verbosity = "minimal"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = "Debug"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$localConfig = Join-Path $repoRoot "Config.Build.user.props"
if (-not (Test-Path -LiteralPath $localConfig)) {
    throw "Missing Config.Build.user.props. Copy Config.Build.user.props.template to Config.Build.user.props and set PEAKGameRootDir for this machine."
}

$solution = Join-Path $repoRoot "BuddyClimb.slnx"
$deployModFiles = if ($Deploy) { "true" } else { "false" }

& dotnet build $solution `
    -c $Configuration `
    "-p:DeployModFiles=$deployModFiles" `
    "-v:$Verbosity"

exit $LASTEXITCODE
