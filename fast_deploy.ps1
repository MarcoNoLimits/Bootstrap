param(
    [string]$SolutionPath = "Builds/UWP/Bootstrap.sln",
    [string]$Configuration = "Debug",
    [string]$Platform = "ARM64",
    [string]$ProjectName = "Bootstrap",
    [switch]$CleanArtifacts,
    [switch]$Deploy,
    [string]$RemoteMachine = "",
    [switch]$VerboseMsBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[fast-deploy] $Message" -ForegroundColor Cyan
}

function Resolve-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "vswhere not found. Install Visual Studio 2022 with UWP/C++ workloads."
    }

    $msbuildPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if (-not $msbuildPath) {
        throw "MSBuild.exe not found via vswhere."
    }
    return $msbuildPath
}

function Remove-IfExists {
    param([string]$PathToDelete)
    if (Test-Path $PathToDelete) {
        Write-Step "Removing $PathToDelete"
        Remove-Item -Path $PathToDelete -Recurse -Force -ErrorAction Stop
    }
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$fullSolutionPath = Join-Path $repoRoot $SolutionPath
if (-not (Test-Path $fullSolutionPath)) {
    throw "Solution not found: $fullSolutionPath. Export UWP from Unity first."
}

if ($CleanArtifacts) {
    $uwpRoot = Join-Path $repoRoot "Builds/UWP"
    Remove-IfExists (Join-Path $uwpRoot ".vs")
    Remove-IfExists (Join-Path $uwpRoot "build/bin")
    Remove-IfExists (Join-Path $uwpRoot "build/obj")
}

$msbuild = Resolve-MSBuild
Write-Step "Using MSBuild: $msbuild"
Write-Step "Building: $fullSolutionPath ($Configuration|$Platform)"

$targetArg = "/t:Build"
$verbosity = if ($VerboseMsBuild) { "minimal" } else { "quiet" }
$props = @(
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:AppxBundle=Never",
    "/p:AppxBundlePlatforms=$Platform",
    "/p:UapAppxPackageBuildMode=SideLoadOnly",
    "/p:GenerateAppInstallerFile=false"
)

if ($Deploy -and -not [string]::IsNullOrWhiteSpace($RemoteMachine)) {
    Write-Step "Deploy target: RemoteMachine ($RemoteMachine)"
    $props += "/p:AppxPackageSigningEnabled=true"
    $props += "/p:DeployTarget=RemoteMachine"
    $props += "/p:RemoteMachine=$RemoteMachine"
}

$args = @(
    "`"$fullSolutionPath`"",
    $targetArg,
    "/m",
    "/v:$verbosity"
) + $props

Write-Step "Running MSBuild..."
& $msbuild @args

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE."
}

if ($Deploy) {
    $solutionDir = Split-Path -Parent $fullSolutionPath
    $solutionDirWithSlash = $solutionDir
    if (-not $solutionDirWithSlash.EndsWith("\")) {
        $solutionDirWithSlash += "\"
    }
    $appProjectPath = Join-Path $solutionDir "$ProjectName/$ProjectName.vcxproj"
    if (-not (Test-Path $appProjectPath)) {
        throw "Deploy requested, but app project was not found at $appProjectPath. Verify -ProjectName."
    }

    Write-Step "Deploying app project: $appProjectPath"
    $deployArgs = @(
        "`"$appProjectPath`"",
        "/t:Build;Deploy",
        "/m",
        "/v:$verbosity",
        "/p:SolutionDir=$solutionDirWithSlash"
    ) + $props

    & $msbuild @deployArgs
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild deploy failed with exit code $LASTEXITCODE."
    }
}

Write-Step "Done."
if (-not $Deploy) {
    Write-Step "Build completed. Deploy from Visual Studio or rerun with -Deploy."
}
