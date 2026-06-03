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

function Test-IsWindows {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Test-IsAdministrator {
    if (-not (Test-IsWindows)) {
        return $true
    }

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '"', '\"') + '"'
}

function Get-PowerShellExecutable {
    $executableName = if ($PSVersionTable.PSEdition -eq "Core") { "pwsh.exe" } else { "powershell.exe" }
    $executablePath = Join-Path $PSHOME $executableName
    if (Test-Path -LiteralPath $executablePath) {
        return $executablePath
    }

    return $executableName
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$localConfig = Join-Path $repoRoot "Config.Build.user.props"
if (-not (Test-Path -LiteralPath $localConfig)) {
    throw "Missing Config.Build.user.props. Copy Config.Build.user.props.template to Config.Build.user.props and set PEAKGameRootDir for this machine."
}

$solution = Join-Path $repoRoot "BuddyClimb.slnx"
$normalizedMode = $Mode.ToLowerInvariant()
$configuration = if ($normalizedMode -eq "debug") { "Debug" } else { "Release" }

if ($normalizedMode -eq "push" -and (Test-IsWindows) -and -not (Test-IsAdministrator)) {
    Write-Host "Push mode requires an Administrator PowerShell. Requesting elevation..."

    $scriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
    $argumentList = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (ConvertTo-ProcessArgument $scriptPath),
        "push",
        "-Verbosity",
        (ConvertTo-ProcessArgument $Verbosity)
    ) -join " "

    $process = Start-Process `
        -FilePath (Get-PowerShellExecutable) `
        -ArgumentList $argumentList `
        -WorkingDirectory $repoRoot `
        -Verb RunAs `
        -Wait `
        -PassThru

    exit $process.ExitCode
}

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
