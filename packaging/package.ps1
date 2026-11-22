<#
.SYNOPSIS
Builds the portable Hoshin.Net CLI bundle and generates both the Claude Code plugin bundle
and the Google Antigravity (.agents/) bundle under artifacts/.
#>
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}

$portableDir = Join-Path $OutputRoot "portable"
$claudeDir = Join-Path $OutputRoot "claude-code"
$agyDir = Join-Path $OutputRoot "antigravity\.agents"

foreach ($dir in @($portableDir, $claudeDir, $agyDir)) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

Write-Host "Publishing portable Hoshin.Cli ($Configuration)..."
& dotnet publish (Join-Path $repoRoot "src\Hoshin.Cli\Hoshin.Cli.csproj") `
    -c $Configuration `
    -o $portableDir `
    --nologo `
    -p:UseAppHost=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$requiredDlls = @("hoshin.dll", "Hoshin.Workflow.dll", "Markdig.dll", "Gherkin.dll", "hoshin.runtimeconfig.json", "hoshin.deps.json")
foreach ($file in $requiredDlls) {
    $full = Join-Path $portableDir $file
    if (-not (Test-Path $full)) {
        throw "Expected portable artifact missing: $full"
    }
}

$skillMeta = @(
    @{
        Name = "build"
        Description = "Builds the accepted plan of the change one task at a time, test first. Use when the engineer asks to build or continue building the change."
    },
    @{
        Name = "accept"
        Description = "Records acceptance or approval of the current change document or built change so the workflow can advance or publish."
    }
)

function Copy-BundleLibAndRoles {
    param([string]$TargetRoot)
    $libDir = Join-Path $TargetRoot "lib"
    $rolesDir = Join-Path $TargetRoot "roles"
    New-Item -ItemType Directory -Force -Path $libDir, $rolesDir | Out-Null
    Copy-Item -Path (Join-Path $portableDir "*") -Destination $libDir -Recurse -Force
    Copy-Item -Path (Join-Path $repoRoot "skills\*") -Destination $rolesDir -Recurse -Force
}

function Write-SkillWrappers {
    param([string]$SkillsTargetDir)
    foreach ($skill in $skillMeta) {
        $name = $skill.Name
        $desc = $skill.Description
        $skillDir = Join-Path $SkillsTargetDir $name
        $refsDir = Join-Path $skillDir "references"
        New-Item -ItemType Directory -Force -Path $refsDir | Out-Null

        # Copy the harness-neutral skill folder verbatim into references/.
        $srcSkillDir = Join-Path $repoRoot "skills\$name"
        Copy-Item -Path (Join-Path $srcSkillDir "*") -Destination $refsDir -Recurse -Force

        # Emit one-line SKILL.md wrapper (YAML front matter is required by Claude Code / Antigravity skill loaders).
        $wrapper = "---`nname: $name`ndescription: $desc`n---`n`nRead ``references/procedure.md`` and follow it.`n"
        [System.IO.File]::WriteAllText((Join-Path $skillDir "SKILL.md"), $wrapper, [System.Text.UTF8Encoding]::new($false))
    }
}

# 1. Claude Code plugin bundle
Write-Host "Generating Claude Code plugin bundle..."
Copy-Item -Path (Join-Path $repoRoot "packaging\claude-code\*") -Destination $claudeDir -Recurse -Force
Copy-BundleLibAndRoles -TargetRoot $claudeDir
Write-SkillWrappers -SkillsTargetDir (Join-Path $claudeDir "skills")

# 2. Google Antigravity (.agents/) bundle
Write-Host "Generating Antigravity bundle..."
Copy-Item -Path (Join-Path $repoRoot "packaging\antigravity\hooks.json") -Destination (Join-Path $agyDir "hooks.json") -Force
Copy-BundleLibAndRoles -TargetRoot $agyDir
Write-SkillWrappers -SkillsTargetDir (Join-Path $agyDir "skills")

Write-Host "Packaging complete:"
Write-Host "  Portable:    $portableDir"
Write-Host "  Claude Code: $claudeDir"
Write-Host "  Antigravity: $agyDir"
