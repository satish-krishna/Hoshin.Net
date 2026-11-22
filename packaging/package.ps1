<#
.SYNOPSIS
Builds all Hoshin.Net v2.0.0 release artifacts under artifacts/:
  - artifacts/portable/            Portable CLI DLL bundle + skills/ folder
  - artifacts/nuget/               Hoshin.Workflow.<version>.nupkg (with embedded role texts)
  - artifacts/claude-code/         Claude Code plugin bundle (.claude-plugin/, hooks/, lib/, roles/, skills/)
  - artifacts/antigravity/.agents/ Google Antigravity bundle (hooks.json, lib/, roles/, skills/)
#>
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -Raw -Path (Join-Path $repoRoot "Directory.Build.props")
    $Version = $props.Project.PropertyGroup.VersionPrefix.Trim()
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Could not read VersionPrefix from Directory.Build.props"
    }
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}

$portableDir = Join-Path $OutputRoot "portable"
$portableLibDir = Join-Path $portableDir "lib"
$portableSkillsDir = Join-Path $portableDir "skills"
$nugetDir = Join-Path $OutputRoot "nuget"
$claudeDir = Join-Path $OutputRoot "claude-code"
$agyDir = Join-Path (Join-Path $OutputRoot "antigravity") ".agents"

foreach ($dir in @($portableDir, $nugetDir, $claudeDir, $agyDir)) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}
New-Item -ItemType Directory -Force -Path $portableLibDir, $portableSkillsDir | Out-Null

Write-Host "Publishing portable Hoshin.Cli ($Configuration, v$Version)..."
& dotnet publish (Join-Path (Join-Path (Join-Path $repoRoot "src") "Hoshin.Cli") "Hoshin.Cli.csproj") `
    -c $Configuration `
    -o $portableLibDir `
    --nologo `
    -p:UseAppHost=false `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$requiredDlls = @("hoshin.dll", "Hoshin.Workflow.dll", "Markdig.dll", "Gherkin.dll", "hoshin.runtimeconfig.json", "hoshin.deps.json")
foreach ($file in $requiredDlls) {
    $full = Join-Path $portableLibDir $file
    if (-not (Test-Path $full)) {
        throw "Expected portable artifact missing: $full"
    }
}

# Copy harness-neutral skills/ alongside portable lib/
Copy-Item -Path (Join-Path (Join-Path $repoRoot "skills") "*") -Destination $portableSkillsDir -Recurse -Force

Write-Host "Packing Hoshin.Workflow NuGet package ($Configuration, v$Version)..."
& dotnet pack (Join-Path (Join-Path (Join-Path $repoRoot "src") "Hoshin.Workflow") "Hoshin.Workflow.csproj") `
    -c $Configuration `
    -o $nugetDir `
    --nologo `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

$expectedNupkg = Join-Path $nugetDir "Hoshin.Workflow.$Version.nupkg"
if (-not (Test-Path $expectedNupkg)) {
    throw "Expected NuGet package missing: $expectedNupkg"
}

$skillMeta = @(
    @{
        Name = "intent"
        Description = "Interviews a product owner to capture a change's problem, proposed outcome, constraints, and decisions in plain language."
    },
    @{
        Name = "spec"
        Description = "Translates an accepted intent into testable Gherkin requirements, concrete scenarios, and policy-checked areas of concern."
    },
    @{
        Name = "plan"
        Description = "Drafts ordered, test-first engineering tasks with scenario coverage, shared decisions, and scoped test commands from an accepted spec."
    },
    @{
        Name = "build"
        Description = "Builds the accepted plan of the change one task at a time, test first. Use when the engineer asks to build or continue building the change."
    },
    @{
        Name = "review"
        Description = "Conducts an independent read-only review of a built change for spec alignment, bugs, and repository conventions."
    },
    @{
        Name = "fix"
        Description = "Light-track authoring for small single-repository fixes of at most three tasks, drafting intent, spec, and plan in one pass."
    },
    @{
        Name = "done"
        Description = "Declares a built and reviewed change done and reports publication results or blocking issues."
    },
    @{
        Name = "accept"
        Description = "Records acceptance or approval of the current change document or built change so the workflow can advance or publish."
    },
    @{
        Name = "setup"
        Description = "Sets up or updates a workspace, system home, policy areas, and workspace instructions."
    },
    @{
        Name = "context"
        Description = "Extracts or refreshes plain-language business context from a system's repositories with provenance tags."
    }
)

function Copy-BundleLibAndRoles {
    param([string]$TargetRoot)
    $libDir = Join-Path $TargetRoot "lib"
    $rolesDir = Join-Path $TargetRoot "roles"
    New-Item -ItemType Directory -Force -Path $libDir, $rolesDir | Out-Null
    Copy-Item -Path (Join-Path $portableLibDir "*") -Destination $libDir -Recurse -Force
    Copy-Item -Path (Join-Path (Join-Path $repoRoot "skills") "*") -Destination $rolesDir -Recurse -Force
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
        $srcSkillDir = Join-Path (Join-Path $repoRoot "skills") $name
        Copy-Item -Path (Join-Path $srcSkillDir "*") -Destination $refsDir -Recurse -Force

        # Emit one-line SKILL.md wrapper (YAML front matter is required by Claude Code / Antigravity skill loaders).
        $userOnly = if ($name -in @("accept", "done")) { "`ndisable-model-invocation: true" } else { "" }
        $wrapper = "---`nname: $name`ndescription: $desc$userOnly`n---`n`nRead ``references/procedure.md`` and follow it.`n"
        [System.IO.File]::WriteAllText((Join-Path $skillDir "SKILL.md"), $wrapper, [System.Text.UTF8Encoding]::new($false))
    }
}

# 1. Claude Code plugin bundle
Write-Host "Generating Claude Code plugin bundle..."
Copy-Item -Path (Join-Path (Join-Path (Join-Path $repoRoot "packaging") "claude-code") "*") -Destination $claudeDir -Recurse -Force
$pluginJsonPath = Join-Path (Join-Path $claudeDir ".claude-plugin") "plugin.json"
$pluginManifest = Get-Content -Raw -Path $pluginJsonPath | ConvertFrom-Json
$pluginManifest.version = $Version
$pluginJson = ($pluginManifest | ConvertTo-Json -Depth 10) + "`n"
[System.IO.File]::WriteAllText($pluginJsonPath, $pluginJson, [System.Text.UTF8Encoding]::new($false))
Copy-BundleLibAndRoles -TargetRoot $claudeDir
Write-SkillWrappers -SkillsTargetDir (Join-Path $claudeDir "skills")

# 2. Google Antigravity (.agents/) bundle
Write-Host "Generating Antigravity bundle..."
Copy-Item -Path (Join-Path (Join-Path (Join-Path $repoRoot "packaging") "antigravity") "hooks.json") -Destination (Join-Path $agyDir "hooks.json") -Force
Copy-BundleLibAndRoles -TargetRoot $agyDir
Write-SkillWrappers -SkillsTargetDir (Join-Path $agyDir "skills")

Write-Host "Packaging complete (v$Version):"
Write-Host "  Portable:    $portableDir (lib/ + skills/)"
Write-Host "  NuGet:       $expectedNupkg"
Write-Host "  Claude Code: $claudeDir"
Write-Host "  Antigravity: $agyDir"
