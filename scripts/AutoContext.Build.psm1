#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Build orchestration functions for AutoContext.

.DESCRIPTION
    Houses every compile / test / format / package / publish / tag / clean
    function used by build.ps1 and the granular scripts/*.ps1 wrappers.

    Shared discovery state (paths, server manifest, solution projects, the
    RID → vsce-target map) is computed once by Initialize-BuildContext and
    threaded explicitly through every function as a -Context object, so no
    function depends on hidden module- or script-scope globals.

    The functions are behaviour-identical to the inline versions that used
    to live in build.ps1; ShouldProcess target/operation strings and section
    headings are preserved verbatim so build.tests.ps1 keeps matching them.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Build context ────────────────────────────────────────────────────────────

function Initialize-BuildContext {
    <#
    .SYNOPSIS
        Discovers all build state (paths, manifests, solution projects, RID
        map) rooted at $RepoRoot and returns it as a single context object
        that every other function accepts via -Context.
    #>
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory)][string]$RepoRoot
    )

    # VS Code extension directory (fixed — used for packaging, publishing, assets)
    $extensionDir = Join-Path $RepoRoot 'src' 'AutoContext.VsCode'

    # Discover vitest configs
    $vitestConfigs = @(Get-ChildItem $RepoRoot -Filter 'vitest.config.ts' -Recurse -File -Depth 4)
    $vitestConfigPath = if ($vitestConfigs.Count -gt 0) { $vitestConfigs[0].FullName } else { $null }

    $serversDir = Join-Path $extensionDir 'servers'
    $publishDir = Join-Path $extensionDir 'publish'

    # Read canonical version from version.json
    $versionJsonPath = Join-Path $RepoRoot 'version.json'
    $extensionVersion = if (Test-Path $versionJsonPath) {
        (Get-Content $versionJsonPath -Raw | ConvertFrom-Json).version
    }

    # Read server manifest (defines which servers to package and their type)
    $serversJsonPath = Join-Path $RepoRoot 'servers.json'
    $serverManifest = if (Test-Path $serversJsonPath) {
        @((Get-Content $serversJsonPath -Raw | ConvertFrom-Json).servers)
    } else { @() }

    $nodeServers = @($serverManifest | Where-Object type -eq 'node')
    $dotnetServers = @($serverManifest | Where-Object type -eq 'dotnet')

    # Shared TypeScript libraries (no entry point) compiled before extension/servers.
    # Consumers reference them via npm `file:` deps. Paths are repo-relative so
    # libraries can live under either src/ (production) or tests/ (test-only).
    $tsLibraries = @(
        'tests/AutoContext.Nodejs.Tests.Support',
        'src/AutoContext.Nodejs.Core'
    )

    # In CI, use 'npm ci' for deterministic lockfile-exact installs
    $npmInstallCmd = if ($env:CI) { 'ci' } else { 'install' }

    # Derive .NET server project paths from manifest (convention: src/<name>/<name>.csproj)
    $serverProjectPaths = @($dotnetServers | ForEach-Object {
        Join-Path $RepoRoot 'src' $_.name "$($_.name).csproj"
    })

    # Discover solution file (.slnx preferred, .sln fallback)
    $solutionFile = Get-ChildItem $RepoRoot -Filter '*.slnx' -File | Select-Object -First 1
    if (-not $solutionFile) {
        $solutionFile = Get-ChildItem $RepoRoot -Filter '*.sln' -File | Select-Object -First 1
    }

    # Discover all .NET project paths from solution (for build, test, and clean)
    $dotnetProjects = @()
    if ($solutionFile -and $solutionFile.Extension -eq '.slnx') {
        [xml]$solutionXml = Get-Content $solutionFile.FullName
        $dotnetProjects = @($solutionXml.SelectNodes('//Project/@Path') |
            ForEach-Object { Join-Path $RepoRoot $_.Value })
    }

    # RID → vsce target mapping (ordered so platform iteration is deterministic)
    $ridToTarget = [ordered]@{
        'win-x64'          = 'win32-x64'
        'win-arm64'        = 'win32-arm64'
        'linux-x64'        = 'linux-x64'
        'linux-arm64'      = 'linux-arm64'
        'linux-arm'        = 'linux-armhf'
        'linux-musl-x64'   = 'alpine-x64'
        'linux-musl-arm64' = 'alpine-arm64'
        'osx-x64'          = 'darwin-x64'
        'osx-arm64'        = 'darwin-arm64'
    }

    return [pscustomobject]@{
        RepoRoot           = $RepoRoot
        ExtensionDir       = $extensionDir
        VitestConfigs      = $vitestConfigs
        VitestConfigPath   = $vitestConfigPath
        ServersDir         = $serversDir
        PublishDir         = $publishDir
        VersionJsonPath    = $versionJsonPath
        ExtensionVersion   = $extensionVersion
        ServersJsonPath    = $serversJsonPath
        ServerManifest     = $serverManifest
        NodeServers        = $nodeServers
        DotnetServers      = $dotnetServers
        TsLibraries        = $tsLibraries
        NpmInstallCmd      = $npmInstallCmd
        ServerProjectPaths = $serverProjectPaths
        SolutionFile       = $solutionFile
        DotnetProjects     = $dotnetProjects
        RidToTarget        = $ridToTarget
        EngineProjectPath  = (Join-Path $RepoRoot 'src' 'AutoContext.Engine' 'AutoContext.Engine.csproj')
        EngineStagingRoot  = (Join-Path $RepoRoot 'artifacts' 'engine')
        EngineBundleDir    = (Join-Path $extensionDir 'engine')
    }
}

# ── Output helpers ───────────────────────────────────────────────────────────

function Write-Header {
    <#
    .SYNOPSIS
        Prints a title centred inside a full-width rule.
    #>
    param(
        [Parameter(Mandatory)][string]$Title,
        [ConsoleColor]$Color = 'Cyan'
    )

    $width = 64
    $padding = $width - $Title.Length
    $leftPad = [math]::Floor($padding / 2)
    $rightPad = [math]::Ceiling($padding / 2)
    $centeredTitle = (' ' * $leftPad) + $Title + (' ' * $rightPad)

    Write-Host ('=' * $width) -ForegroundColor $Color
    Write-Host $centeredTitle -ForegroundColor $Color
    Write-Host ('=' * $width) -ForegroundColor $Color
}

function Write-Section {
    <#
    .SYNOPSIS
        Prints a section heading introducing the step about to run.
    #>
    param([Parameter(Mandatory)][string]$Title)
    Write-Host ("`n=== {0}" -f $Title) -ForegroundColor Cyan
}

function Write-Status {
    <#
    .SYNOPSIS
        Prints an indented status line carrying an outcome icon and colour.
    #>
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('OK', 'FAIL', 'INFO')][string]$Status
    )

    $icon = switch ($Status) {
        'OK'   { 'v' }
        'FAIL' { 'x' }
        'INFO' { '*' }
    }

    $color = switch ($Status) {
        'OK'   { 'Green' }
        'FAIL' { 'Red' }
        'INFO' { 'Gray' }
    }

    Write-Host ('  [{0}] {1}' -f $icon, $Message) -ForegroundColor $color
}

function Invoke-WithRetry {
    <#
    .SYNOPSIS
        Runs a scriptblock, retrying only while its output matches the
        caller's retryable predicate, and returns the final output and
        exit code.
    #>
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [scriptblock]$IsRetryable = { $false },
        [int]$MaxAttempts = 3,
        [int]$DelaySeconds = 30
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if ($MaxAttempts -gt 1) {
            Write-Status "Attempt $attempt/$MaxAttempts starting..." 'INFO'
        }

        # Stream the command output to the host as it happens (so CI
        # logs show progress in real time) while also capturing it for
        # the retry decision and the success/failure regex match.
        # `2>&1` merges the error stream with success so stderr lines
        # also appear in `$output` (and on the host).
        $null = & $ScriptBlock 2>&1 | Tee-Object -Variable streamed | Out-Host
        $exitCode = $LASTEXITCODE
        $output = $streamed

        if ($exitCode -eq 0) {
            return @{ Output = $output; ExitCode = 0 }
        }

        $retryable = & $IsRetryable $output

        if (-not $retryable) {
            Write-Status "Command failed with exit code $exitCode (non-retryable)." 'FAIL'
            return @{ Output = $output; ExitCode = $exitCode }
        }

        if ($attempt -eq $MaxAttempts) {
            Write-Status "Command failed with exit code $exitCode after $MaxAttempts attempts." 'FAIL'
            return @{ Output = $output; ExitCode = $exitCode }
        }

        Write-Status "Attempt $attempt/$MaxAttempts failed (exit $exitCode, retryable), waiting ${DelaySeconds}s..." 'INFO'
        Start-Sleep -Seconds $DelaySeconds
    }
}

# ── Validation ───────────────────────────────────────────────────────────────

function Assert-ExternalCommand {
    <#
    .SYNOPSIS
        Throws when a required external command is not on PATH.
    #>
    param([Parameter(Mandatory)][string]$Command)

    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) {
        throw "'$Command' is not installed or not on PATH."
    }
}

function Compare-SemVer {
    <#
    .SYNOPSIS
        Compares two semver strings. Returns positive if New > Current,
        negative if New < Current, zero if equal.
    #>
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string]$Current,
        [Parameter(Mandatory)][string]$New
    )

    $semverPattern = '^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$'

    if ($Current -notmatch $semverPattern) { throw "Invalid current version: '$Current'" }
    $curBase = [System.Version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
    $curPre = $Matches[4]

    if ($New -notmatch $semverPattern) { throw "Invalid new version: '$New'" }
    $newBase = [System.Version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
    $newPre = $Matches[4]

    $baseCmp = $newBase.CompareTo($curBase)
    if ($baseCmp -ne 0) { return $baseCmp }

    # Same base: release (no prerelease) beats any prerelease
    if (-not $curPre -and -not $newPre) { return 0 }
    if ($curPre -and -not $newPre) { return 1 }
    if (-not $curPre -and $newPre) { return -1 }

    # Both have prerelease — compare dot-separated identifiers per semver spec
    $curIds = $curPre -split '\.'
    $newIds = $newPre -split '\.'
    $count = [math]::Max($curIds.Count, $newIds.Count)

    for ($i = 0; $i -lt $count; $i++) {
        if ($i -ge $curIds.Count) { return 1 }
        if ($i -ge $newIds.Count) { return -1 }

        $curId = $curIds[$i]
        $newId = $newIds[$i]
        $curIsNum = $curId -match '^\d+$'
        $newIsNum = $newId -match '^\d+$'

        if ($curIsNum -and $newIsNum) {
            $cmp = ([int]$newId).CompareTo([int]$curId)
            if ($cmp -ne 0) { return $cmp }
        }
        elseif ($curIsNum) { return 1 }
        elseif ($newIsNum) { return -1 }
        else {
            $cmp = [string]::Compare($newId, $curId, [System.StringComparison]::Ordinal)
            if ($cmp -ne 0) { return [math]::Sign($cmp) }
        }
    }

    return 0
}

# ── Version sync ─────────────────────────────────────────────────────────────

function Get-CanonicalVersion {
    <#
    .SYNOPSIS
        Reads the canonical version string fresh from version.json. Read on
        each call (not the cached $Context.ExtensionVersion) so callers that
        have just rewritten version.json — e.g. Update-ProjectVersion during
        tagging — stamp the new value.
    #>
    [OutputType([string])]
    param([Parameter(Mandatory)][psobject]$Context)

    if (-not (Test-Path $Context.VersionJsonPath)) {
        throw "version.json not found at $($Context.VersionJsonPath)"
    }

    $version = (Get-Content $Context.VersionJsonPath -Raw | ConvertFrom-Json).version
    if (-not $version) {
        throw 'version.json does not contain a "version" property.'
    }

    return $version
}

function Export-VersionConstant {
    <#
    .SYNOPSIS
        Writes a TypeScript `export const VERSION` constant carrying the
        canonical version to $TargetPath. A relative path resolves against the
        current working directory; an absolute path is used as-is.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$TargetPath
    )

    $version = Get-CanonicalVersion -Context $Context

    $resolvedPath = if ([System.IO.Path]::IsPathRooted($TargetPath)) {
        $TargetPath
    }
    else {
        Join-Path (Get-Location).Path $TargetPath
    }

    if ($PSCmdlet.ShouldProcess($resolvedPath, "Write version constant $version")) {
        $content = "export const VERSION = `"$version`";" + [System.Environment]::NewLine
        Set-Content -LiteralPath $resolvedPath -Value $content -NoNewline
        Write-Host "Exported version $version -> $resolvedPath"
    }
}

function Sync-ProjectFileVersions {
    <#
    .SYNOPSIS
        Stamps the canonical version into every package.json,
        package-lock.json, and .csproj discovered from the repository and the
        solution.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    $version = Get-CanonicalVersion -Context $Context
    $repoRoot = $Context.RepoRoot

    if (-not $PSCmdlet.ShouldProcess('project files', "Stamp version $version")) { return }

    # ── Discover npm directories (any src/*/ with package.json) ──
    $npmDirs = @(Get-ChildItem (Join-Path $repoRoot 'src') -Filter 'package.json' -Recurse -Depth 1 |
        ForEach-Object { $_.Directory.FullName })

    # ── Pass 1: update package.json files and collect our local package names ──
    $ourNames = @{}
    foreach ($dir in $npmDirs) {
        $pkgPath = Join-Path $dir 'package.json'
        $dirName = Split-Path $dir -Leaf

        if (Test-Path $pkgPath) {
            $raw = Get-Content $pkgPath -Raw
            $pkgJson = $raw | ConvertFrom-Json -AsHashtable
            if ($pkgJson.ContainsKey('name')) { $ourNames[$pkgJson['name']] = $true }

            $updated = $raw -replace '"version":\s*"[^"]*"', "`"version`": `"$version`""
            if ($updated -ne $raw) {
                Set-Content $pkgPath $updated -NoNewline
                Write-Host "Synced $dirName/package.json -> $version"
            }
        }
    }

    # ── Pass 2: update package-lock.json files ──
    # Each version slot in a lockfile (root, packages[""], packages["../<sibling>"])
    # is preceded by a "name": "<our-package>" entry. Anchoring on our known names
    # avoids touching transitive deps that may coincidentally share the version.
    foreach ($dir in $npmDirs) {
        $lockPath = Join-Path $dir 'package-lock.json'
        $dirName = Split-Path $dir -Leaf

        if (-not (Test-Path $lockPath)) { continue }

        $lockRaw = Get-Content $lockPath -Raw
        $original = $lockRaw

        foreach ($name in $ourNames.Keys) {
            $pattern = [regex]::new(
                '("name":\s*"' + [regex]::Escape($name) + '",\s*\r?\n\s*"version":\s*")[^"]*(")')
            $lockRaw = $pattern.Replace($lockRaw, "`${1}$version`${2}")
        }

        if ($lockRaw -ne $original) {
            Set-Content $lockPath $lockRaw -NoNewline
            Write-Host "Synced $dirName/package-lock.json -> $version"
        }
    }

    # ── Update .NET projects discovered from the solution ──
    foreach ($projectPath in $Context.DotnetProjects) {
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        if (-not (Test-Path $projectPath)) { continue }

        $raw = Get-Content $projectPath -Raw

        if ($raw -match '<Version>[^<]*</Version>') {
            $updated = $raw -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
        }
        else {
            $propGroupRegex = [regex]::new('(<PropertyGroup>)(\r?\n)')
            $updated = $propGroupRegex.Replace($raw, "`${1}`${2}    <Version>$version</Version>`${2}", 1)
        }

        if ($updated -ne $raw) {
            Set-Content $projectPath $updated -NoNewline
            Write-Host "Synced $projectName.csproj -> $version"
        }
    }
}

function Sync-ProjectVersions {
    <#
    .SYNOPSIS
        Syncs the canonical version.json into every project file and exports
        each node server's version.ts. Extracted from the previously
        duplicated inline blocks in the compile/prepare paths.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    if ($PSCmdlet.ShouldProcess('version.json', 'Sync versions to all projects')) {
        Sync-ProjectFileVersions -Context $Context

        foreach ($server in $Context.NodeServers) {
            $versionTsPath = Join-Path $Context.RepoRoot 'src' $server.name 'src' 'version.ts'
            Export-VersionConstant -Context $Context -TargetPath $versionTsPath
        }
    }
}

# ── Core actions ─────────────────────────────────────────────────────────────

function Install-Npm {
    <#
    .SYNOPSIS
        Installs npm dependencies for a project, skipping the install when
        package-lock.json is unchanged since the last successful install.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectDir,
        [Parameter(Mandatory)][string]$InstallCommand,
        [Parameter(Mandatory)][string]$Label
    )

    $lockPath = Join-Path $ProjectDir 'package-lock.json'
    $nodeModulesDir = Join-Path $ProjectDir 'node_modules'
    $markerPath = Join-Path $nodeModulesDir '.autocontext-lock-hash'

    $lockHash = if (Test-Path -LiteralPath $lockPath) {
        (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
    } else { $null }

    # Skip when the lock file matches the hash recorded at the last successful
    # install AND node_modules is still present.
    if ($lockHash -and (Test-Path -LiteralPath $nodeModulesDir) -and (Test-Path -LiteralPath $markerPath)) {
        $recordedHash = (Get-Content -LiteralPath $markerPath -Raw).Trim()
        if ($recordedHash -ceq $lockHash) {
            Write-Status "$Label dependencies up to date (skipped install)" 'OK'
            return
        }
    }

    Write-Status "Installing $Label dependencies..." 'INFO'
    npm $InstallCommand
    if ($LASTEXITCODE -ne 0) { throw "$Label npm install failed." }

    # Record the lock hash so the next install for an unchanged lock is skipped.
    if ($lockHash) {
        Set-Content -LiteralPath $markerPath -Value $lockHash -NoNewline
    }
}

function Build-TypeScript {
    <#
    .SYNOPSIS
        Compiles every TypeScript project — shared libraries first, then the
        extension with its generated manifests and staged hook scripts, then
        each node server.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Compile TypeScript'

    if (-not (Test-Path $Context.ExtensionDir)) { throw "Extension directory not found: $($Context.ExtensionDir)" }

    if ($PSCmdlet.ShouldProcess('chat-instructions manifest + tsc', 'Compile TypeScript')) {
        Assert-ExternalCommand 'npx'

        # Build shared TS libraries first so consumers can resolve them via file: deps.
        foreach ($libRelPath in $Context.TsLibraries) {
            $libDir = Join-Path $Context.RepoRoot $libRelPath
            if (-not (Test-Path $libDir)) { continue }
            $libName = Split-Path $libRelPath -Leaf

            Push-Location $libDir
            try {
                Install-Npm -ProjectDir $libDir -InstallCommand $Context.NpmInstallCmd -Label $libName

                Write-Status "Compiling $libName (src + tests)..." 'INFO'
                npx tsc -b ./tsconfig.json
                if ($LASTEXITCODE -ne 0) { throw "$libName compilation failed." }
                Write-Status "$libName compiled" 'OK'
            }
            finally {
                Pop-Location
            }
        }

        Push-Location $Context.ExtensionDir
        try {
            Install-Npm -ProjectDir $Context.ExtensionDir -InstallCommand $Context.NpmInstallCmd -Label 'extension'

            Write-Status 'Generating instructions files metadata...' 'INFO'
            npx tsx src/instructions-files-metadata-generator.ts
            if ($LASTEXITCODE -ne 0) { throw 'Instructions files metadata generation failed.' }
            Write-Status 'Instructions files metadata generated' 'OK'

            Write-Status 'Generating chat-instructions manifest...' 'INFO'
            npx tsx src/package-instructions-manifest-generator.ts
            if ($LASTEXITCODE -ne 0) { throw 'Chat-instructions manifest generation failed.' }
            Write-Status 'Chat-instructions manifest generated' 'OK'

            Write-Status 'Compiling TypeScript (src + tests)...' 'INFO'
            npx tsc -b ./tsconfig.json
            if ($LASTEXITCODE -ne 0) { throw 'TypeScript compilation failed.' }
            Write-Status 'TypeScript compiled' 'OK'

            # Stage compiled hook scripts (`*.cts` → `*.cjs`) into the
            # bundled agent-plugin folder so they sit alongside the
            # plugin manifest the VSIX ships.
            $hookNames = @(
                'autocontext-session-start.cjs',
                'autocontext-user-prompt-submit.cjs'
            )
            $hookDstDir = Join-Path 'plugin' 'scripts'
            if (-not (Test-Path $hookDstDir)) {
                New-Item -ItemType Directory -Path $hookDstDir | Out-Null
            }
            foreach ($hookName in $hookNames) {
                $hookSrc = Join-Path 'dist' 'hooks' $hookName
                $hookDst = Join-Path $hookDstDir $hookName
                if (Test-Path $hookSrc) {
                    Copy-Item -Path $hookSrc -Destination $hookDst -Force
                    Write-Status "Hook script staged ($hookDst)" 'OK'
                } else {
                    throw "Compiled hook script not found at $hookSrc."
                }
            }
        }
        finally {
            Pop-Location
        }

        foreach ($server in $Context.NodeServers) {
            $serverDir = Join-Path $Context.RepoRoot 'src' $server.name
            if (-not (Test-Path $serverDir)) { continue }

            $serverLabel = $server.name
            Push-Location $serverDir
            try {
                Install-Npm -ProjectDir $serverDir -InstallCommand $Context.NpmInstallCmd -Label $serverLabel

                $versionTsPath = Join-Path $serverDir 'src' 'version.ts'
                Write-Status "Generating $serverLabel version..." 'INFO'
                Export-VersionConstant -Context $Context -TargetPath $versionTsPath

                Write-Status "Compiling $serverLabel (src + tests)..." 'INFO'
                npx tsc -b ./tsconfig.json
                if ($LASTEXITCODE -ne 0) { throw "$serverLabel compilation failed." }
                Write-Status "$serverLabel compiled" 'OK'
            }
            finally {
                Pop-Location
            }
        }
    }
}

function Build-DotNet {
    <#
    .SYNOPSIS
        Compiles the .NET solution in Release.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Compile .NET'

    if (-not $Context.SolutionFile) { throw 'No .slnx or .sln file found in the repository root.' }

    if ($PSCmdlet.ShouldProcess($Context.SolutionFile.Name, 'dotnet build')) {
        Assert-ExternalCommand 'dotnet'

        dotnet build $Context.SolutionFile.FullName -c Release
        if ($LASTEXITCODE -ne 0) { throw '.NET compilation failed.' }
        Write-Status ".NET solution compiled ($($Context.SolutionFile.Name))" 'OK'
    }
}

function Test-TypeScript {
    <#
    .SYNOPSIS
        Runs the vitest suite for every discovered vitest config.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Test TypeScript'

    if ($Context.VitestConfigs.Count -eq 0) { throw 'No vitest.config.ts found — cannot locate TypeScript tests.' }

    if ($PSCmdlet.ShouldProcess('vitest', 'Run TypeScript tests')) {
        Assert-ExternalCommand 'npx'

        foreach ($config in $Context.VitestConfigs) {
            # Resolve project root as the nearest ancestor containing package.json
            $searchDir = $config.Directory
            while ($searchDir -and -not (Test-Path (Join-Path $searchDir.FullName 'package.json'))) {
                $searchDir = $searchDir.Parent
            }
            $projectDir = if ($searchDir) { $searchDir.FullName } else { $config.Directory.FullName }
            $projectName = Split-Path $projectDir -Leaf

            Push-Location $projectDir
            try {
                $relativeConfig = [System.IO.Path]::GetRelativePath($projectDir, $config.FullName)
                npx vitest run --config $relativeConfig
                if ($LASTEXITCODE -ne 0) { throw "TypeScript tests failed ($projectName)." }
                Write-Status "TypeScript tests passed ($projectName)" 'OK'
            }
            finally {
                Pop-Location
            }
        }
    }
}

function Test-DotNet {
    <#
    .SYNOPSIS
        Runs the .NET unit tests against the existing build, excluding the
        smoke category.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Test .NET'

    if (-not $Context.SolutionFile) { throw 'No .slnx or .sln file found in the repository root.' }

    if ($PSCmdlet.ShouldProcess($Context.SolutionFile.Name, 'dotnet test --no-build (unit)')) {
        Assert-ExternalCommand 'dotnet'

        # `--logger console;verbosity=normal` prints each failed test's
        # error message and stack trace inline; without it the default
        # runner reports only a pass/fail count, hiding why a test failed.
        dotnet test $Context.SolutionFile.FullName -c Release --no-build --filter 'Category!=Smoke' --logger 'console;verbosity=normal'
        # Carry the runner's exit code: 1 means tests asserted false and the
        # inline logger has already named them, while a crash code means the
        # host died and named nothing.
        if ($LASTEXITCODE -ne 0) { throw ".NET tests failed (dotnet test exit code $LASTEXITCODE)." }
        Write-Status '.NET tests passed' 'OK'
    }
}

function Test-DotNetFormat {
    <#
    .SYNOPSIS
        Verifies .NET formatting, failing on any deviation rather than
        rewriting the file.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Format .NET'

    if (-not $Context.SolutionFile) { throw 'No .slnx or .sln file found in the repository root.' }

    if ($PSCmdlet.ShouldProcess($Context.SolutionFile.Name, 'dotnet format --verify-no-changes')) {
        Assert-ExternalCommand 'dotnet'

        dotnet format $Context.SolutionFile.FullName --verify-no-changes --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw ".NET format verification failed. Run 'dotnet format' to fix, or pass -NoLint to skip."
        }
        Write-Status '.NET format verified' 'OK'
    }
}

function Test-DotNetSmoke {
    <#
    .SYNOPSIS
        Runs the smoke-category tests of every project that contains a
        *.Smoke.cs file, against the staged layout the caller prepared.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Smoke-test .NET'

    # The caller (Invoke-SmokeTests, reached via scripts/test.ps1 -Smoke) is
    # responsible for compiling and staging the packaged extension layout
    # before invoking this function.

    $smokeTestProjects =
        Get-ChildItem (Join-Path $Context.RepoRoot 'tests') -Recurse -File -Filter '*.Smoke.cs' |
        ForEach-Object {
            $dir = $_.Directory
            while ($dir -and -not (Get-ChildItem $dir.FullName -File -Filter '*.csproj' | Select-Object -First 1)) {
                $dir = $dir.Parent
            }

            if ($dir) {
                Get-ChildItem $dir.FullName -File -Filter '*.csproj' | Select-Object -First 1
            }
        } |
        Sort-Object FullName -Unique

    if (-not $smokeTestProjects) {
        throw 'No .NET smoke test projects found (*.Smoke.cs under tests).'
    }

    $projectList = ($smokeTestProjects | ForEach-Object { $_.Name }) -join ', '

    if ($PSCmdlet.ShouldProcess($projectList, 'dotnet test --no-build (smoke)')) {
        Assert-ExternalCommand 'dotnet'

        foreach ($project in $smokeTestProjects) {
            # `--logger console;verbosity=normal` surfaces each failed
            # test's error message and stack trace inline (smoke failures
            # are otherwise reported only as a bare count).
            dotnet test $project.FullName -c Release --no-build --filter 'Category=Smoke' --logger 'console;verbosity=normal'
            if ($LASTEXITCODE -ne 0) { throw ".NET smoke tests failed ($($project.Name), dotnet test exit code $LASTEXITCODE)." }
        }

        Write-Status '.NET smoke tests passed' 'OK'
    }
}

function Test-VsCodeSmoke {
    <#
    .SYNOPSIS
        Compiles the extension smoke tests and runs them inside a real VS
        Code instance, against the staged layout the caller prepared.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Smoke-test VS Code extension'

    # The caller (Invoke-SmokeTests, reached via scripts/test.ps1 -Smoke) is
    # responsible for compiling and staging the packaged extension layout
    # before invoking this function.

    if ($PSCmdlet.ShouldProcess('vscode-test', 'Run VS Code smoke tests')) {
        Assert-ExternalCommand 'npx'

        Push-Location $Context.ExtensionDir
        try {
            # Compile smoke-test TypeScript (Mocha/CJS)
            npx tsc -p tests/smoke-tests/tsconfig.json
            if ($LASTEXITCODE -ne 0) { throw 'Smoke-test TypeScript compilation failed.' }
            Write-Status 'Smoke-test TypeScript compiled' 'OK'

            # Emit CJS package.json for dist output
            $smokeDistDir = Join-Path $Context.ExtensionDir 'dist' 'tests' 'smoke-tests'
            $packageJsonPath = Join-Path $smokeDistDir 'package.json'
            Set-Content -LiteralPath $packageJsonPath -Value '{"type":"commonjs"}' -NoNewline

            # Ensure .git directory exists in mixed workspace fixture
            $mixedGitDir = Join-Path $Context.ExtensionDir 'tests' 'workspaces' 'mixed' '.git'
            if (-not (Test-Path $mixedGitDir)) {
                New-Item -ItemType Directory -Path $mixedGitDir -Force | Out-Null
            }

            # Run vscode-test
            npx --yes vscode-test --config tests/.vscode-test.mjs
            if ($LASTEXITCODE -ne 0) { throw 'VS Code smoke tests failed.' }
            Write-Status 'VS Code smoke tests passed' 'OK'
        }
        finally {
            Pop-Location
        }
    }
}

function Copy-AssetsToExtensionFolder {
    <#
    .SYNOPSIS
        Copies the licence, legal, and server-manifest files the VSIX ships
        into the extension directory.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    $assets = @(
        @{ Source = 'LICENSE';        Destination = 'LICENSE';                 Label = 'LICENSE' }
        @{ Source = 'TRADEMARKS.md';  Destination = 'TRADEMARKS.md';           Label = 'TRADEMARKS.md' }
        @{ Source = 'servers.json';   Destination = 'resources/servers.json';  Label = 'servers.json' }
    )

    foreach ($asset in $assets) {
        $source      = Join-Path $Context.RepoRoot $asset.Source
        $destination = Join-Path $Context.ExtensionDir $asset.Destination
        if ($PSCmdlet.ShouldProcess($destination, "Copy $($asset.Label)")) {
            Copy-Item -LiteralPath $source -Destination $destination -Force
            Write-Status "$($asset.Label) copied to extension" 'OK'
        }
    }
}

function Build-DotNetPackage {
    <#
    .SYNOPSIS
        Publishes every .NET server self-contained for one runtime into
        servers/<name>/.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$Rid
    )

    Write-Section "Package .NET servers ($Rid)"
    if ($Context.ServerProjectPaths.Count -eq 0) { throw 'No non-test .NET projects found in the solution.' }
    if ($PSCmdlet.ShouldProcess("$($Context.ServerProjectPaths.Count) project(s) → $Rid", 'dotnet publish')) {
        Assert-ExternalCommand 'dotnet'

        $publishArgs = @(
            'publish'
            '-c', 'Release'
            '-r', $Rid
            '--self-contained'
            '-p:PublishSingleFile=true'
            '-p:ContinuousIntegrationBuild=true'
        )

        foreach ($projectPath in $Context.ServerProjectPaths) {
            $serverName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
            $serverDir = Join-Path $Context.ServersDir $serverName
            if (Test-Path $serverDir) { Remove-Item $serverDir -Recurse -Force }
            dotnet @publishArgs $projectPath -o $serverDir
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $serverName ($Rid)." }
            Write-Status "$serverName packaged ($Rid)" 'OK'
        }
    }
}

function Get-DotNetReleaseOutputDir {
    <#
    .SYNOPSIS
        Resolves a project's bin/Release/<tfm> directory, reading the target
        framework from the csproj so the path never hard-codes a moniker.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$ProjectPath)

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    [xml]$csproj = Get-Content $ProjectPath
    $tfm = $csproj.SelectSingleNode('//TargetFramework')?.InnerText
    if (-not $tfm) { throw "Cannot determine TargetFramework for $projectName." }

    $binDir = Join-Path (Split-Path $ProjectPath -Parent) 'bin' 'Release' $tfm
    if (-not (Test-Path $binDir)) {
        throw ".NET Release output not found for $projectName ($binDir) — run Compile first."
    }

    return $binDir
}

function Get-EngineWorkerStagingPlan {
    <#
    .SYNOPSIS
        Resolves what to stage into each Workers/<id>/ directory, taking the
        roster from the engine's own generated workers.json so the staged
        layout and the engine's ${root} lookup cannot disagree.
    #>
    [CmdletBinding()]
    [OutputType([psobject[]])]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$ManifestPath
    )

    if (-not (Test-Path $ManifestPath)) {
        throw "Worker manifest not found at $ManifestPath — the engine build generates it."
    }

    $roster = @((Get-Content $ManifestPath -Raw | ConvertFrom-Json).workers)
    if ($roster.Count -eq 0) { throw "Worker manifest $ManifestPath declares no workers." }

    # Index the descriptors by the id they declare. Carrying a descriptor is
    # what makes a project a worker — the same rule the manifest generator
    # applies — so a worker that does not follow the AutoContext.Worker.*
    # naming convention is still staged. Any divergence between the two
    # surfaces as an unmatched id below.
    $byId = @{}
    foreach ($directory in Get-ChildItem (Join-Path $Context.RepoRoot 'src') -Directory) {
        $descriptorPath = Join-Path $directory.FullName '.autocontext-worker.json'
        if (-not (Test-Path $descriptorPath)) { continue }

        $id = (Get-Content $descriptorPath -Raw | ConvertFrom-Json).id
        if (-not $id) { continue }

        $projectPath = Join-Path $directory.FullName "$($directory.Name).csproj"

        $byId[$id] = [pscustomobject]@{
            Id          = $id
            Name        = $directory.Name
            ProjectPath = if (Test-Path $projectPath) { $projectPath } else { $null }
        }
    }

    $plan = foreach ($worker in $roster) {
        if (-not $byId.ContainsKey($worker.id)) {
            throw "Worker '$($worker.id)' is in $ManifestPath but no project under src/ declares that id."
        }

        $byId[$worker.id]
    }

    $unclaimed = @($byId.Keys | Where-Object { $_ -notin @($roster | ForEach-Object { $_.id }) })
    if ($unclaimed.Count -gt 0) {
        throw "Project(s) declaring worker id(s) '$($unclaimed -join ", ")' are absent from $ManifestPath — regenerate the engine build."
    }

    return @($plan)
}

function Build-EngineBundle {
    <#
    .SYNOPSIS
        Publishes the engine and every worker into the per-RID staging tree
        artifacts/engine/<rid>/, the shape per-platform packaging copies into
        a shipped artefact's engine/ directory.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$Rid
    )

    Write-Section "Package engine ($Rid)"

    $stagingDir = Join-Path $Context.EngineStagingRoot $Rid

    if ($PSCmdlet.ShouldProcess("engine + workers → $Rid", 'dotnet publish')) {
        Assert-ExternalCommand 'dotnet'

        if (Test-Path $stagingDir) { Remove-BuildOutput -Path $stagingDir }

        $publishArgs = @(
            'publish'
            '-c', 'Release'
            '-r', $Rid
            '--self-contained'
            '-p:PublishSingleFile=true'
            '-p:ContinuousIntegrationBuild=true'
        )

        dotnet @publishArgs $Context.EngineProjectPath -o $stagingDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the engine ($Rid)." }
        Write-Status "autocontext-engine packaged ($Rid)" 'OK'

        # Each worker lands under Workers/<id>/ — the directory the engine's
        # workers.json ${root} placeholder expands to — so per-worker
        # self-contained runtimes never collide with each other or with the
        # engine's own runtime files at the bundle root.
        $workers = Get-EngineWorkerStagingPlan -Context $Context `
            -ManifestPath (Join-Path $stagingDir 'Resources' 'workers.json')

        foreach ($worker in $workers) {
            $workerDir = Join-Path $stagingDir 'Workers' $worker.Id

            if ($worker.ProjectPath) {
                dotnet @publishArgs $worker.ProjectPath -o $workerDir
                if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for worker '$($worker.Id)' ($Rid)." }
            }
            else {
                # Node workers run on the host's Node runtime, so their output
                # is RID-independent and is staged from the tree the Node
                # packaging step already produced.
                $bundledDir = Join-Path $Context.ServersDir $worker.Name
                if (-not (Test-Path $bundledDir)) {
                    throw "Node worker '$($worker.Id)' is not staged at $bundledDir — package the Node servers first."
                }

                New-Item $workerDir -ItemType Directory -Force | Out-Null
                Copy-Item (Join-Path $bundledDir '*') $workerDir -Recurse -Force
            }

            Write-Status "worker '$($worker.Id)' packaged ($Rid)" 'OK'
        }
    }
}

function Get-ExecutableRuntime {
    <#
    .SYNOPSIS
        Reads an executable's own header to report the operating system and
        architecture it was built for, as an '<os>-<arch>' string. Returns
        $null for a file that carries no recognised executable header.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 64) { return $null }

        $magic = $reader.ReadUInt32()

        # ELF: 0x7F 'E' 'L' 'F', with e_machine 18 bytes in.
        if ($magic -eq 0x464C457F) {
            $stream.Position = 0x12
            switch ($reader.ReadUInt16()) {
                0x3E { return 'linux-x64' }
                0xB7 { return 'linux-arm64' }
                0x28 { return 'linux-arm' }
                default { return $null }
            }
        }

        # Mach-O 64-bit, with cpu_type immediately after the magic. The
        # magic needs the unsigned suffix: it overflows Int32, which a
        # bare literal would silently become and never match.
        if ($magic -eq 0xFEEDFACFu) {
            switch ($reader.ReadUInt32()) {
                0x01000007 { return 'osx-x64' }
                0x0100000C { return 'osx-arm64' }
                default { return $null }
            }
        }

        # PE: 'MZ', with the COFF header located via e_lfanew at 0x3C.
        if (($magic -band 0xFFFF) -eq 0x5A4D) {
            $stream.Position = 0x3C
            $stream.Position = $reader.ReadUInt32()
            if ($reader.ReadUInt32() -ne 0x00004550) { return $null }

            switch ($reader.ReadUInt16()) {
                0x8664 { return 'win-x64' }
                0xAA64 { return 'win-arm64' }
                default { return $null }
            }
        }

        return $null
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-EngineBundleRuntime {
    <#
    .SYNOPSIS
        Fails when a staged engine bundle carries a binary built for a
        runtime other than the one being packaged.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BundleDir,
        [Parameter(Mandatory)][string]$Rid
    )

    $executableName = if ($Rid.StartsWith('win-')) { 'autocontext-engine.exe' } else { 'autocontext-engine' }
    $enginePath = Join-Path $BundleDir $executableName
    if (-not (Test-Path $enginePath)) {
        throw "Engine bundle for $Rid carries no '$executableName' ($BundleDir)."
    }

    # An executable header records the operating system and architecture but
    # not the C library, so a musl build is indistinguishable from a glibc
    # one. Comparison drops the '-musl' infix and holds the rest exactly.
    $expectedRuntime = $Rid -replace '-musl', ''

    # Only apphost-shaped files are probed: '.exe' on Windows and
    # extensionless elsewhere. Anything else in a worker directory is a
    # script or data file with no runtime of its own.
    $workersDir = Join-Path $BundleDir 'Workers'
    $candidates = @($enginePath)
    if (Test-Path $workersDir) {
        $candidates += @(
            Get-ChildItem $workersDir -Recurse -File |
                Where-Object { if ($Rid.StartsWith('win-')) { $_.Extension -eq '.exe' } else { $_.Extension -eq '' } } |
                Select-Object -ExpandProperty FullName
        )
    }

    foreach ($candidate in $candidates) {
        $actual = Get-ExecutableRuntime -Path $candidate
        if ($null -eq $actual) { continue }

        if ($actual -ne $expectedRuntime) {
            throw "'$candidate' targets $actual but the bundle is being packaged for $Rid."
        }
    }

    Write-Status "bundle verified as $Rid" 'OK'
}

function Assert-EngineCorpusIdenticalAcrossRuntimes {
    <#
    .SYNOPSIS
        Fails when the instructions corpus staged for one runtime differs
        from the corpus staged for another in the same build.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Verify corpus'

    if ($PSCmdlet.ShouldProcess('staged instructions corpus', 'Compare across runtimes')) {
        $digests = [ordered]@{}

        foreach ($rid in $Context.RidToTarget.Keys) {
            $corpusDir = Join-Path $Context.EngineStagingRoot $rid 'Instructions'
            if (-not (Test-Path $corpusDir)) {
                throw "Instructions corpus not staged for $rid ($corpusDir)."
            }

            $entries = Get-ChildItem $corpusDir -Recurse -File | Sort-Object FullName | ForEach-Object {
                $relativePath = $_.FullName.Substring($corpusDir.Length + 1).Replace('\', '/')
                '{0} {1}' -f $relativePath, (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            }

            $digests[$rid] = [Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData(
                    [System.Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))))
        }

        $distinct = @($digests.Values | Sort-Object -Unique)
        if ($distinct.Count -gt 1) {
            $summary = ($digests.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value.Substring(0, 12))" }) -join ', '
            throw "The instructions corpus differs across runtimes: $summary."
        }

        Write-Status "corpus identical across $($digests.Count) runtimes" 'OK'
    }
}

function Copy-EngineBundleToExtension {
    <#
    .SYNOPSIS
        Copies one RID's staged engine tree into the extension's engine/
        directory — the shipped shape, which carries no <rid>/ segment.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$Rid
    )

    Write-Section "Copy engine to extension ($Rid)"

    $stagingDir = Join-Path $Context.EngineStagingRoot $Rid

    if ($PSCmdlet.ShouldProcess($Context.EngineBundleDir, "Copy engine ($Rid)")) {
        if (-not (Test-Path $stagingDir)) {
            throw "Engine staging for $Rid not found ($stagingDir)."
        }

        if (Test-Path $Context.EngineBundleDir) { Remove-BuildOutput -Path $Context.EngineBundleDir }
        New-Item $Context.EngineBundleDir -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $stagingDir '*') $Context.EngineBundleDir -Recurse -Force

        Write-Status "engine/ populated ($Rid)" 'OK'

        # Verified after the copy so the check covers the tree that is
        # actually packaged, not the staging it was cut from.
        Assert-EngineBundleRuntime -BundleDir $Context.EngineBundleDir -Rid $Rid
    }
}

function Copy-EngineToExtension {
    <#
    .SYNOPSIS
        Local-dev counterpart of Build-EngineBundle: lays out engine/ from
        framework-dependent build output so a debug host resolves an engine
        without a self-contained per-RID publish.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Copy engine (local)'

    if ($PSCmdlet.ShouldProcess($Context.EngineBundleDir, 'Copy engine build output')) {
        if (Test-Path $Context.EngineBundleDir) { Remove-BuildOutput -Path $Context.EngineBundleDir }
        New-Item $Context.EngineBundleDir -ItemType Directory -Force | Out-Null

        $engineOutput = Get-DotNetReleaseOutputDir -ProjectPath $Context.EngineProjectPath
        Copy-Item (Join-Path $engineOutput '*') $Context.EngineBundleDir -Recurse -Force
        Write-Status 'autocontext-engine copied (local)' 'OK'

        $workers = Get-EngineWorkerStagingPlan -Context $Context `
            -ManifestPath (Join-Path $Context.EngineBundleDir 'Resources' 'workers.json')

        foreach ($worker in $workers) {
            $sourceDir = if ($worker.ProjectPath) {
                Get-DotNetReleaseOutputDir -ProjectPath $worker.ProjectPath
            }
            else {
                Join-Path $Context.ServersDir $worker.Name
            }

            if (-not (Test-Path $sourceDir)) {
                throw "Worker '$($worker.Id)' output not found ($sourceDir) — run Compile first."
            }

            $workerDir = Join-Path $Context.EngineBundleDir 'Workers' $worker.Id
            New-Item $workerDir -ItemType Directory -Force | Out-Null
            Copy-Item (Join-Path $sourceDir '*') $workerDir -Recurse -Force

            Write-Status "worker '$($worker.Id)' copied (local)" 'OK'
        }
    }
}

function Copy-DotNetToServersFolder {
    <#
    .SYNOPSIS
        Local-dev counterpart of Build-DotNetPackage: lays out servers/<name>/
        from framework-dependent build output instead of a per-RID publish.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Copy .NET servers (local)'
    if ($Context.ServerProjectPaths.Count -eq 0) { throw 'No non-test .NET projects found in the solution.' }

    foreach ($projectPath in $Context.ServerProjectPaths) {
        $serverName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        $binDir = Get-DotNetReleaseOutputDir -ProjectPath $projectPath

        $serverDir = Join-Path $Context.ServersDir $serverName
        if ($PSCmdlet.ShouldProcess($serverDir, "Copy $serverName build output")) {
            if (Test-Path $serverDir) { Remove-Item $serverDir -Recurse -Force }
            New-Item $serverDir -ItemType Directory -Force | Out-Null
            Copy-Item (Join-Path $binDir '*') $serverDir -Recurse -Force
            Write-Status "$serverName copied (local)" 'OK'
        }
    }
}

function Copy-NodeJsToServersFolder {
    <#
    .SYNOPSIS
        Stages each node server's compiled output and production dependencies
        into servers/<name>/, replacing npm's workspace links with real copies
        so the tree is self-contained.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    foreach ($server in $Context.NodeServers) {
        $serverName = $server.name
        $serverSourceDir = Join-Path $Context.RepoRoot 'src' $serverName

        Write-Section "Package $serverName"

        if (-not (Test-Path $serverSourceDir)) {
            Write-Status "$serverName directory not found — skipping" 'INFO'
            continue
        }

        $outDir = Join-Path $serverSourceDir 'dist'
        if (-not (Test-Path $outDir)) { throw "$serverName not compiled — run Compile first." }

        $targetDir = Join-Path $Context.ServersDir $serverName

        if ($PSCmdlet.ShouldProcess($targetDir, "Copy $serverName")) {
            New-Item $targetDir -ItemType Directory -Force | Out-Null
            Copy-Item (Join-Path $outDir '*') $targetDir -Recurse -Force

            Copy-Item (Join-Path $serverSourceDir 'package.json') $targetDir -Force
            Copy-Item (Join-Path $serverSourceDir 'package-lock.json') $targetDir -Force

            Push-Location $targetDir
            try {
                npm ci --omit=dev
                if ($LASTEXITCODE -ne 0) { throw "npm ci for $serverName failed." }
            }
            finally {
                Pop-Location
            }

            # vsce's dependency walker on Linux fails with
            # "currentLevel is undefined" when it encounters npm's
            # symlinks for `file:` dependencies. On Windows npm even
            # creates a *broken* junction, since the relative path
            # in package.json (e.g. `file:../AutoContext.Nodejs.Core`)
            # is resolved against the staging cwd. To produce a
            # self-contained, packageable tree, replace any link
            # entries under the staged node_modules with real copies
            # of the corresponding workspace source.
            $stagedNodeModules = Join-Path $targetDir 'node_modules'
            if (Test-Path $stagedNodeModules) {
                Get-ChildItem $stagedNodeModules -Force `
                    | Where-Object { $_.LinkType -in 'SymbolicLink', 'Junction' } `
                    | ForEach-Object {
                        $linkPath = $_.FullName
                        $depName = $_.Name
                        $depSourceDir = $null
                        foreach ($candidate in (Get-ChildItem (Join-Path $Context.RepoRoot 'src') -Directory)) {
                            $pj = Join-Path $candidate.FullName 'package.json'
                            if (Test-Path $pj) {
                                $manifest = Get-Content $pj -Raw | ConvertFrom-Json
                                if ($manifest.name -eq $depName) {
                                    $depSourceDir = $candidate.FullName
                                    break
                                }
                            }
                        }
                        if (-not $depSourceDir) {
                            throw "Could not locate workspace source for symlinked dep '$depName' in $linkPath."
                        }
                        Remove-Item $linkPath -Force -Recurse
                        Copy-Item $depSourceDir $linkPath -Recurse -Force
                        # Drop nested node_modules and source/test files —
                        # the framework's own published surface is just
                        # `dist/` + `package.json`.
                        foreach ($prune in @('node_modules', 'src', 'tests', 'tsconfig.json', 'tsconfig.src.json', 'tsconfig.tests.json', 'vitest.config.ts')) {
                            $prunePath = Join-Path $linkPath $prune
                            if (Test-Path $prunePath) { Remove-Item $prunePath -Recurse -Force }
                        }
                    }
            }

            Remove-Item (Join-Path $targetDir 'package-lock.json') -Force

            Write-Status "$serverName packaged" 'OK'
        }
    }
}

function Build-NodeJsBundle {
    <#
    .SYNOPSIS
        Bundles each staged node server into a single ESM entry point and
        removes everything the bundle inlined.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    foreach ($server in $Context.NodeServers) {
        $serverName = $server.name
        $targetDir = Join-Path $Context.ServersDir $serverName

        if (-not (Test-Path $targetDir)) {
            Write-Status "$serverName not found in servers — skipping bundle" 'INFO'
            continue
        }

        Write-Section "Bundle $serverName"

        $entryPoint = Join-Path $targetDir 'index.js'
        if (-not (Test-Path $entryPoint)) { throw "Entry point not found: $entryPoint" }

        if ($PSCmdlet.ShouldProcess($targetDir, "Bundle $serverName with esbuild")) {
            $bundleFile = Join-Path $targetDir 'index.bundle.js'
            $serverSourceDir = Join-Path $Context.RepoRoot 'src' $serverName

            # --yes so a machine without esbuild already cached installs it
            # instead of blocking the build on npx's confirmation prompt.
            Push-Location $serverSourceDir
            try {
                npx --yes esbuild $entryPoint --bundle --platform=node --format=esm --external:typescript --outfile=$bundleFile
                if ($LASTEXITCODE -ne 0) { throw "esbuild bundle failed for $serverName." }
            }
            finally {
                Pop-Location
            }

            # Replace original with bundle
            Remove-Item $entryPoint -Force
            Rename-Item $bundleFile 'index.js'

            # Remove everything except the bundle and its remaining dependencies
            $keep = @('index.js', 'node_modules', 'package.json')
            Get-ChildItem $targetDir -Exclude $keep | Remove-Item -Recurse -Force

            # Prune node_modules to only externalized packages
            $nodeModulesDir = Join-Path $targetDir 'node_modules'
            if (Test-Path $nodeModulesDir) {
                $externalPackages = @('typescript')
                Get-ChildItem $nodeModulesDir -Directory |
                    Where-Object { $_.Name -notin $externalPackages } |
                    Remove-Item -Recurse -Force
            }

            # ESM bundle requires a package.json with type=module so Node.js
            # treats .js as ES modules regardless of parent directory layout.
            '{"type":"module"}' | Set-Content (Join-Path $targetDir 'package.json') -Encoding utf8NoBOM

            Write-Status "$serverName bundled" 'OK'
        }
    }
}

function Build-ExtensionBundle {
    <#
    .SYNOPSIS
        Bundles the compiled extension into a single ESM entry point and
        removes the outputs and incremental caches it inlined.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    $distDir = Join-Path $Context.ExtensionDir 'dist'
    $entryPoint = Join-Path $distDir 'extension.js'

    if (-not (Test-Path $entryPoint)) { throw "Extension entry point not found: $entryPoint" }

    Write-Section 'Bundle extension'

    if ($PSCmdlet.ShouldProcess($distDir, 'Bundle extension with esbuild')) {
        $bundleFile = Join-Path $distDir 'extension.bundle.js'

        Push-Location $Context.ExtensionDir
        try {
            npx --yes esbuild $entryPoint --bundle --platform=node --format=esm --external:vscode --outfile=$bundleFile
            if ($LASTEXITCODE -ne 0) { throw 'esbuild bundle failed for extension.' }
        }
        finally {
            Pop-Location
        }

        # Replace original with bundle
        Remove-Item $entryPoint -Force
        Rename-Item $bundleFile 'extension.js'

        # Remove all other files — they are now inlined
        Get-ChildItem $distDir -Recurse -File |
            Where-Object { $_.FullName -ne (Join-Path $distDir 'extension.js') } |
            Remove-Item -Force
        # Remove subdirectories leaf-first so a parent's -Recurse delete cannot race
        # ahead of children still in the streaming pipeline (StrictMode would throw).
        Get-ChildItem $distDir -Recurse -Directory |
            Sort-Object -Property { $_.FullName.Length } -Descending |
            Remove-Item -Force -ErrorAction SilentlyContinue

        # Bundling destroyed outputs tsc still believes it produced. Leaving the
        # incremental caches behind would make the next `tsc -b` skip the
        # rebuild and never restore them, so drop them with their outputs.
        Get-ChildItem $Context.ExtensionDir -Filter '*.tsbuildinfo' -File -ErrorAction SilentlyContinue |
            Remove-Item -Force

        Write-Status 'Extension bundled' 'OK'
    }
}

function Build-VscePackage {
    <#
    .SYNOPSIS
        Packages the staged extension into a VSIX for one platform target and
        moves it into publish/.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$Rid
    )

    $vsceTarget = $Context.RidToTarget[$Rid]
    if (-not $vsceTarget) {
        throw "No VS Code target mapping for runtime identifier '$Rid'."
    }

    Write-Section "Package VSIX ($vsceTarget)"

    if ($PSCmdlet.ShouldProcess("vsce package --target $vsceTarget", 'Package extension')) {
        Assert-ExternalCommand 'npx'

        $env:AUTOCONTEXT_VSCE_BYPASS = '1'
        Push-Location $Context.ExtensionDir
        try {
            # --no-dependencies: the extension is already bundled into
            # dist/extension.js by esbuild, so vsce does not need to
            # walk node_modules. Walking it on Windows follows the
            # junction to the workspace-linked autocontext-nodejs-core
            # package and produces invalid '../' paths.
            # --yes (on npx): auto-accept the install prompt when vsce
            # is not yet cached, so unattended runs don't block.
            npx --yes vsce package --target $vsceTarget --allow-missing-repository --no-dependencies
            if ($LASTEXITCODE -ne 0) { throw 'vsce package failed.' }

            New-Item $Context.PublishDir -ItemType Directory -Force | Out-Null
            Move-Item (Join-Path $Context.ExtensionDir '*.vsix') $Context.PublishDir -Force
            Write-Status "VSIX packaged ($vsceTarget)" 'OK'
        }
        finally {
            Pop-Location
            Remove-Item Env:\AUTOCONTEXT_VSCE_BYPASS -ErrorAction SilentlyContinue
        }
    }
}

function Publish-VscePackage {
    <#
    .SYNOPSIS
        Publishes every built VSIX to the Visual Studio Marketplace, skipping
        versions already published.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Publish to Marketplace'

    # Wrapped so a single match stays an array: under StrictMode a scalar
    # FileInfo has no Count, which would fault exactly the one-platform
    # publish this branch exists to serve.
    $vsixFiles = @(Get-ChildItem (Join-Path $Context.PublishDir '*.vsix') -ErrorAction SilentlyContinue)
    if ($vsixFiles.Count -eq 0) {
        if ($WhatIfPreference) {
            Write-Status 'No VSIX files (skipped in WhatIf)' 'INFO'
            return
        }
        throw 'No VSIX files found in publish/ directory.'
    }

    if ($PSCmdlet.ShouldProcess("$($vsixFiles.Count) VSIX file(s)", 'Publish to Marketplace')) {
        Assert-ExternalCommand 'npx'

        Push-Location $Context.ExtensionDir
        try {
            foreach ($vsix in $vsixFiles) {
                Write-Status "Publishing $($vsix.Name)..." 'INFO'
                $result = Invoke-WithRetry -ScriptBlock {
                    npx --yes vsce publish --packagePath $vsix.FullName
                } -IsRetryable {
                    param($output)
                    ($output | Out-String) -match '\b(ETIMEDOUT|ECONNRESET|ECONNREFUSED|EAI_AGAIN|ENOTFOUND|timed out)\b|HTTP\s*5\d\d'
                } -MaxAttempts 3 -DelaySeconds 60

                if ($result.ExitCode -ne 0) {
                    if ($result.Output -match 'already exists') {
                        Write-Status "Skipped $($vsix.Name) (already published)" 'INFO'
                    }
                    else {
                        throw "Failed to publish $($vsix.Name)."
                    }
                }
                else {
                    Write-Status "Published $($vsix.Name)" 'OK'
                }
            }
        }
        finally {
            Pop-Location
        }
    }
}

function Publish-OvsxPackage {
    <#
    .SYNOPSIS
        Publishes every built VSIX to Open VSX, skipping versions already
        published.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Section 'Publish to Open VSX'

    # Wrapped so a single match stays an array: under StrictMode a scalar
    # FileInfo has no Count, which would fault exactly the one-platform
    # publish this branch exists to serve.
    $vsixFiles = @(Get-ChildItem (Join-Path $Context.PublishDir '*.vsix') -ErrorAction SilentlyContinue)
    if ($vsixFiles.Count -eq 0) {
        if ($WhatIfPreference) {
            Write-Status 'No VSIX files (skipped in WhatIf)' 'INFO'
            return
        }
        throw 'No VSIX files found in publish/ directory.'
    }

    if ($PSCmdlet.ShouldProcess("$($vsixFiles.Count) VSIX file(s)", 'Publish to Open VSX')) {
        Assert-ExternalCommand 'npx'

        Push-Location $Context.ExtensionDir
        try {
            foreach ($vsix in $vsixFiles) {
                Write-Status "Publishing $($vsix.Name) to Open VSX..." 'INFO'
                $result = Invoke-WithRetry -ScriptBlock {
                    npx --yes ovsx publish $vsix.FullName
                } -IsRetryable {
                    param($output)
                    ($output | Out-String) -match '\b(ETIMEDOUT|ECONNRESET|ECONNREFUSED|EAI_AGAIN|ENOTFOUND|timed out)\b|HTTP\s*5\d\d'
                } -MaxAttempts 3 -DelaySeconds 60

                if ($result.ExitCode -ne 0) {
                    if ($result.Output -match 'already exists|already published') {
                        Write-Status "Skipped $($vsix.Name) (already published on Open VSX)" 'INFO'
                    }
                    else {
                        throw "Failed to publish $($vsix.Name) to Open VSX."
                    }
                }
                else {
                    Write-Status "Published $($vsix.Name) to Open VSX" 'OK'
                }
            }
        }
        finally {
            Pop-Location
        }
    }
}

function Resolve-RuntimeIdentifier {
    <#
    .SYNOPSIS
        Returns the supplied runtime identifier, falling back to the one the
        host .NET installation reports.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([string]$RuntimeIdentifier)

    if ($RuntimeIdentifier) { return $RuntimeIdentifier }

    $detected = dotnet --info |
        Select-String 'RID:\s+(\S+)' |
        ForEach-Object { $_.Matches[0].Groups[1].Value }

    if (-not $detected) {
        throw 'Could not detect the runtime identifier. Pass -RuntimeIdentifier explicitly.'
    }

    Write-Status "Detected runtime identifier: $detected" 'INFO'
    return $detected
}

function Update-ProjectVersion {
    <#
    .SYNOPSIS
        Writes a new canonical version into version.json and stamps it across
        every project file and generated version constant.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$NewVersion
    )

    Write-Section 'Update versions'

    if ($PSCmdlet.ShouldProcess($Context.VersionJsonPath, "Update version to $NewVersion")) {
        $raw = Get-Content $Context.VersionJsonPath -Raw
        $raw = $raw -replace '"version":\s*"[^"]*"', "`"version`": `"$NewVersion`""
        Set-Content $Context.VersionJsonPath $raw -NoNewline
        Write-Status "version.json -> $NewVersion" 'OK'

        Sync-ProjectFileVersions -Context $Context

        foreach ($server in $Context.NodeServers) {
            $versionTsPath = Join-Path $Context.RepoRoot 'src' $server.name 'src' 'version.ts'
            Export-VersionConstant -Context $Context -TargetPath $versionTsPath
        }
    }
}

# ── Composite actions ────────────────────────────────────────────────────────

function Invoke-Build {
    <#
    .SYNOPSIS
        The gate: compiles the selected stack(s), verifies .NET formatting,
        and runs the unit tests.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [string]$Scope = 'All',
        [switch]$NoLint,
        [switch]$NoTest
    )

    Write-Header 'Compile'
    if ($Scope -in 'All', 'TS')     { Build-TypeScript -Context $Context }
    if ($Scope -in 'All', 'DotNet') { Build-DotNet -Context $Context }

    if (-not $NoLint -and $Scope -in 'All', 'DotNet') {
        Write-Header 'Lint'
        Test-DotNetFormat -Context $Context
    }

    if ($NoTest) { return }

    Write-Header 'Test'
    if ($Scope -in 'All', 'TS')     { Test-TypeScript -Context $Context }
    if ($Scope -in 'All', 'DotNet') { Test-DotNet -Context $Context }
}

function Invoke-Prepare {
    <#
    .SYNOPSIS
        Cleans, syncs versions, runs the full gate, and copies the shipped
        assets — the staged tree every packaging path starts from.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Invoke-Clean -Context $Context

    # Sync all project files to the canonical version before compiling
    Sync-ProjectVersions -Context $Context

    Invoke-Build -Context $Context -Scope 'All'

    Write-Header 'Prepare'
    Copy-AssetsToExtensionFolder -Context $Context
}

function Invoke-Package {
    <#
    .SYNOPSIS
        Produces the packaged layout: a framework-dependent local dev copy,
        one platform's VSIX, or all six.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [string]$Scope,
        [switch]$Local,
        [string]$RuntimeIdentifier
    )

    Invoke-Prepare -Context $Context

    Write-Header 'Package'

    Copy-NodeJsToServersFolder -Context $Context

    if ($Local) {
        # Local dev: copy framework-dependent build output (no publish, no VSIX)
        Copy-DotNetToServersFolder -Context $Context
        Copy-EngineToExtension -Context $Context
    }
    elseif ($Scope -eq 'All') {
        Build-NodeJsBundle -Context $Context
        Build-ExtensionBundle -Context $Context

        # Explicit "Package All" — build all six platforms
        foreach ($rid in $Context.RidToTarget.Keys) {
            Build-EngineBundle -Context $Context -Rid $rid
            Copy-EngineBundleToExtension -Context $Context -Rid $rid
            Build-DotNetPackage -Context $Context -Rid $rid
            Build-VscePackage -Context $Context -Rid $rid
        }

        # Only an all-platform run has more than one staging tree to
        # compare, so the corpus invariant is checked here.
        Assert-EngineCorpusIdenticalAcrossRuntimes -Context $Context

        # Drop the per-artefact staging each VSIX already carries a copy of.
        # artifacts/engine/<rid>/ deliberately survives: it is the per-RID
        # build output the shipped bundles are cut from, and Clean owns it.
        if (Test-Path $Context.ServersDir) { Remove-Item $Context.ServersDir -Recurse -Force }
        if (Test-Path $Context.EngineBundleDir) { Remove-Item $Context.EngineBundleDir -Recurse -Force }
    }
    else {
        Build-NodeJsBundle -Context $Context
        Build-ExtensionBundle -Context $Context

        # Single platform: explicit -RuntimeIdentifier or auto-detect
        $rid = Resolve-RuntimeIdentifier -RuntimeIdentifier $RuntimeIdentifier
        Build-EngineBundle -Context $Context -Rid $rid
        Copy-EngineBundleToExtension -Context $Context -Rid $rid
        Build-DotNetPackage -Context $Context -Rid $rid
        Build-VscePackage -Context $Context -Rid $rid
    }
}

function Invoke-TestStress {
    <#
    .SYNOPSIS
        Runs a test scriptblock repeatedly, surfacing intermittent
        failures as a rate instead of aborting on the first flake.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][scriptblock]$Run,
        [ValidateRange(1, 100000)][int]$Times = 1
    )

    if ($Times -le 1) {
        & $Run
        return
    }

    $failedIterations = [System.Collections.Generic.List[int]]::new()

    for ($iteration = 1; $iteration -le $Times; $iteration++) {
        Write-Header ('{0} - iteration {1} / {2}' -f $Label, $iteration, $Times)

        try {
            & $Run
        }
        catch [System.Management.Automation.PipelineStoppedException] {
            # A stop request (Ctrl-C) must abort the whole loop rather than
            # be recorded as a failed iteration and continued past.
            throw
        }
        catch {
            $failedIterations.Add($iteration)
            Write-Status ('iteration {0} failed: {1}' -f $iteration, $_.Exception.Message) 'FAIL'
        }
    }

    $failedCount = $failedIterations.Count
    Write-Header ('{0} - {1} run(s), {2} failed' -f $Label, $Times, $failedCount)

    if ($failedCount -gt 0) {
        $failedList = $failedIterations -join ', '
        throw ('{0}: {1} of {2} iterations failed (iterations: {3}).' -f $Label, $failedCount, $Times, $failedList)
    }

    Write-Status ('all {0} iterations passed' -f $Times) 'OK'
}

function Invoke-SmokeTests {
    <#
    .SYNOPSIS
        Stages the packaged layout once, then runs the selected smoke
        suite(s) against it.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [string]$Scope = 'All',
        [ValidateRange(1, 100000)][int]$Times = 1
    )

    # Smoke runs against the packaged extension layout, so stage both stacks
    # via the same Package -Local pipeline used for local F5 (clean, version
    # sync, the full compile/lint/test gate, asset copy, and a
    # framework-dependent server copy). $Scope only narrows which smoke
    # suite(s) actually run at the end. Staging happens once; -Times only
    # repeats the smoke run so a flake surfaces without re-packaging.
    Invoke-Package -Context $Context -Local

    Write-Header 'Smoke Test'
    Invoke-TestStress -Label 'Smoke' -Times $Times -Run ({
        if ($Scope -in 'All', 'TS')     { Test-VsCodeSmoke -Context $Context }
        if ($Scope -in 'All', 'DotNet') { Test-DotNetSmoke -Context $Context }
    }.GetNewClosure())
}

function Invoke-Publish {
    <#
    .SYNOPSIS
        Builds any per-platform VSIX not already present, then publishes every
        one to both marketplaces.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [string]$Scope,
        [string]$RuntimeIdentifier
    )

    Invoke-Prepare -Context $Context

    Write-Header 'Publish'

    # Read after Prepare so the manifest is up to date
    $packageJson = Get-Content (Join-Path $Context.ExtensionDir 'package.json') -Raw | ConvertFrom-Json
    $name = $packageJson.name
    $version = $packageJson.version

    New-Item $Context.PublishDir -ItemType Directory -Force | Out-Null

    # Explicit "Publish All" = all platforms; otherwise single platform
    $rids = if ($Scope -eq 'All') { $Context.RidToTarget.Keys } else { @(Resolve-RuntimeIdentifier -RuntimeIdentifier $RuntimeIdentifier) }

    Copy-NodeJsToServersFolder -Context $Context
    Build-NodeJsBundle -Context $Context
    Build-ExtensionBundle -Context $Context

    foreach ($rid in $rids) {
        $vsceTarget = $Context.RidToTarget[$rid]
        $vsixName = "$name-$vsceTarget-$version.vsix"
        $vsixPath = Join-Path $Context.PublishDir $vsixName

        if (-not $WhatIfPreference -and (Test-Path $vsixPath)) {
            Write-Status "Found existing $vsixName — skipping build for $rid" 'INFO'
        }
        else {
            Build-EngineBundle -Context $Context -Rid $rid
            Copy-EngineBundleToExtension -Context $Context -Rid $rid
            Build-DotNetPackage -Context $Context -Rid $rid
            Build-VscePackage -Context $Context -Rid $rid
        }
    }

    Publish-VscePackage -Context $Context
    Publish-OvsxPackage -Context $Context
}

function Undo-PreviousTag {
    <#
    .SYNOPSIS
        If a previous local-only tag attempt for the same version exists,
        automatically undo it so the Tag action can re-run cleanly.
        Returns $true if a previous attempt was undone, $false otherwise.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$Version
    )

    $existingTag = git tag -l $Version
    if (-not $existingTag) { return $false }

    # Tag exists — check safety conditions for auto-undo
    $tagSha = git rev-parse "refs/tags/$Version" 2>&1
    $headSha = git rev-parse HEAD 2>&1
    $tagPointsToHead = $tagSha -eq $headSha

    # An annotated tag's ref resolves to the tag object, not the commit.
    # Dereference to get the commit it points to.
    if (-not $tagPointsToHead) {
        $tagCommitSha = git rev-parse "${Version}^{commit}" 2>&1
        $tagPointsToHead = $tagCommitSha -eq $headSha
    }

    if (-not $tagPointsToHead) {
        throw "Tag '$Version' already exists but does not point to HEAD. Delete it manually."
    }

    $headMsg = git log -1 --format='%s' HEAD
    if ($headMsg -ne "chore: bump version to $Version") {
        throw "Tag '$Version' already exists but HEAD commit message does not match expected bump commit. Delete it manually."
    }

    # Check the tag is not on any remote
    $remoteTags = git ls-remote --tags origin "refs/tags/$Version" 2>&1
    if ($remoteTags) {
        throw "Tag '$Version' already exists and has been pushed to remote. Delete it manually."
    }

    # All safety checks passed — undo
    if ($PSCmdlet.ShouldProcess("tag $Version + bump commit", 'Undo previous tag attempt')) {
        Write-Section 'Undo previous tag attempt'

        git tag -d $Version 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to delete local tag '$Version'." }
        Write-Status "Deleted local tag $Version" 'OK'

        git reset --mixed HEAD~1 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to reset bump commit.' }
        Write-Status 'Reset bump commit' 'OK'

        git checkout -- . 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to restore working tree.' }
        Write-Status 'Restored working tree' 'OK'

        return $true
    }

    # WhatIf mode — nothing was actually undone
    return $false
}

function Remove-ExistingTag {
    <#
    .SYNOPSIS
        Force-delete an existing tag locally and on the 'origin' remote
        (if present). Used by `Invoke-Tag -Force` to clear stale tags
        before re-creating them. Does not modify any commit history.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Version
    )

    Write-Section 'Remove existing tag'

    $localTag = git tag -l $Version
    if ($localTag) {
        if ($PSCmdlet.ShouldProcess("local tag $Version", 'Delete')) {
            git tag -d $Version 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to delete local tag '$Version'." }
            Write-Status "Deleted local tag $Version" 'OK'
        }
    }
    else {
        Write-Status "No local tag $Version" 'INFO'
    }

    $remoteTag = git ls-remote --tags origin "refs/tags/$Version" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query remote tags: $remoteTag"
    }
    if ($remoteTag) {
        if ($PSCmdlet.ShouldProcess("remote tag $Version on origin", 'Delete')) {
            git push --delete origin "refs/tags/$Version" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to delete remote tag '$Version'." }
            Write-Status "Deleted remote tag $Version" 'OK'
        }
    }
    else {
        Write-Status "No remote tag $Version" 'INFO'
    }
}

function Invoke-Tag {
    <#
    .SYNOPSIS
        Validates the version and working tree, runs the gate, bumps and
        commits the version when it changed, and creates the annotated
        release tag.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][psobject]$Context,
        [Parameter(Mandatory)][string]$Version,
        [switch]$Force
    )

    Write-Header 'Tag'

    # ── Validate format ──
    $semverPattern = '^\d+\.\d+\.\d+(-[a-zA-Z0-9]+([.][a-zA-Z0-9]+)*)?$'
    if ($Version -notmatch $semverPattern) {
        throw "Invalid version '$Version'. Expected format: X.Y.Z or X.Y.Z-prerelease"
    }

    Assert-ExternalCommand 'git'

    # ── Force re-tag: delete local + remote tag, leaving any bump commit alone ──
    if ($Force) {
        Remove-ExistingTag -Version $Version
    }

    # ── Auto-undo previous local-only tag attempt ──
    # Skipped under -Force, since Remove-ExistingTag already cleared the tag
    # and -Force intentionally preserves any prior bump commit.
    $wasUndone = if ($Force) { $false } else { Undo-PreviousTag -Context $Context -Version $Version }
    $currentVersion = if ($wasUndone) {
        # Re-read version from disk since the undo reverted the bump
        (Get-Content $Context.VersionJsonPath -Raw | ConvertFrom-Json).version
    }
    else {
        $Context.ExtensionVersion
    }

    # ── Validate version ──
    if (-not $currentVersion) {
        throw "Cannot read current version from $($Context.VersionJsonPath)"
    }

    $versionCmp = Compare-SemVer -Current $currentVersion -New $Version
    if ($versionCmp -lt 0) {
        throw "Version '$Version' is less than current version '$currentVersion'."
    }

    $needsBump = $versionCmp -gt 0

    # ── Validate working tree ──
    $gitStatus = git status --porcelain 2>&1
    if ($gitStatus) {
        throw 'Working tree is not clean. Commit or stash your changes before tagging.'
    }

    # ── Build gate ──
    Invoke-Build -Context $Context -Scope 'All'

    # ── Bump versions + commit (only if version changed) ──
    if ($needsBump) {
        Write-Header 'Bump Versions'
        Update-ProjectVersion -Context $Context -NewVersion $Version

        Write-Section 'Git commit'
        if ($PSCmdlet.ShouldProcess("version $Version", 'Git commit')) {
            git add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

            git commit -m "chore: bump version to $Version"
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
            Write-Status "Committed version bump to $Version" 'OK'
        }
    }
    else {
        Write-Status "Version already at $Version — skipping bump" 'INFO'
    }

    # ── Git tag ──
    Write-Section 'Git tag'
    if ($PSCmdlet.ShouldProcess("version $Version", 'Create annotated tag')) {
        git tag -a $Version -m "Release $Version"
        if ($LASTEXITCODE -ne 0) { throw 'git tag failed.' }
        Write-Status "Created annotated tag $Version" 'OK'
    }

    Write-Host ''
    Write-Status 'Push with: git push origin main --follow-tags' 'INFO'
}

function Remove-BuildOutput {
    <#
    .SYNOPSIS
        Deletes a build output path, retrying briefly so a transient lock has
        time to clear.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$MaxAttempts = 5
    )

    # A virus scanner or a build server that has not finished releasing its
    # handles can hold a freshly written file open for a moment. The lock
    # clears itself, so retry briefly rather than failing the whole run; the
    # final attempt rethrows so a genuine lock still surfaces.
    for ($attempt = 1; ; $attempt++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -ge $MaxAttempts) { throw }
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }
}

function Invoke-Clean {
    <#
    .SYNOPSIS
        Deletes every build output: TypeScript dist trees and incremental
        caches, staged servers and engine, VSIX packages, generated
        instructions, and each .NET project's bin/ and obj/.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][psobject]$Context)

    Write-Header 'Clean'

    # MSBuild keeps worker nodes alive between builds (node reuse) and they
    # hold handles inside the bin/ and obj/ trees this function deletes, so a
    # clean that races them fails with "Access to the path is denied".
    # Shutting the servers down first releases those handles.
    if ($PSCmdlet.ShouldProcess('MSBuild worker nodes', 'Shut down build servers')) {
        if (Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue) {
            dotnet build-server shutdown *> $null
        }
    }

    $targets = @()

    # Build the (projectDir, displayName) seed for every TypeScript project we
    # own — extension + shared TS libraries + node servers. Used to add both
    # the dist/ target and the sibling tsconfig.*.tsbuildinfo incremental
    # caches: leaving the latter behind tricks the next `tsc -b` into
    # believing every output is current and silently skipping the rebuild.
    $tsProjects = @()
    $tsProjects += @{ Dir = $Context.ExtensionDir; Name = 'TypeScript' }
    foreach ($libRelPath in $Context.TsLibraries) {
        $libDir = Join-Path $Context.RepoRoot $libRelPath
        if (-not (Test-Path $libDir)) { continue }
        $tsProjects += @{ Dir = $libDir; Name = (Split-Path $libRelPath -Leaf) }
    }
    foreach ($server in $Context.NodeServers) {
        $tsProjects += @{
            Dir  = (Join-Path $Context.RepoRoot 'src' $server.name)
            Name = $server.name
        }
    }

    foreach ($project in $tsProjects) {
        $targets += @{
            Path  = (Join-Path $project.Dir 'dist')
            Label = "$($project.Name) output (dist/)"
        }
        if (Test-Path $project.Dir) {
            Get-ChildItem -Path $project.Dir -Filter '*.tsbuildinfo' -File -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $targets += @{
                        Path  = $_.FullName
                        Label = "$($project.Name) incremental cache ($($_.Name))"
                    }
                }
        }
    }
    $targets += @{ Path = $Context.ServersDir;                          Label = 'Servers (servers/)' }
    $targets += @{ Path = $Context.EngineBundleDir;                     Label = 'Engine bundle (engine/)' }
    $targets += @{ Path = $Context.EngineStagingRoot;                   Label = 'Engine staging (artifacts/engine/)' }
    $targets += @{ Path = $Context.PublishDir;                         Label = 'VSIX packages (publish/)' }
    $targets += @{ Path = (Join-Path $Context.ExtensionDir 'LICENSE');       Label = 'Extension LICENSE copy' }

    $instructionsDir = Join-Path $Context.ExtensionDir 'instructions'
    $targets += @{ Path = (Join-Path $instructionsDir '.generated');  Label = 'Generated instructions (.generated/)' }
    $targets += @{ Path = (Join-Path $instructionsDir '.workspaces'); Label = 'Workspace instructions (.workspaces/)' }

    foreach ($project in $Context.DotnetProjects) {
        $projectDir = Split-Path $project -Parent
        $projectName = Split-Path $projectDir -Leaf
        $targets += @{ Path = (Join-Path $projectDir 'bin'); Label = "$projectName bin/" }
        $targets += @{ Path = (Join-Path $projectDir 'obj'); Label = "$projectName obj/" }
    }

    foreach ($entry in $targets) {
        if (Test-Path $entry.Path) {
            if ($PSCmdlet.ShouldProcess($entry.Path, "Delete $($entry.Label)")) {
                Remove-BuildOutput -Path $entry.Path
                Write-Status "Deleted $($entry.Label)" 'OK'
            }
        }
        else {
            Write-Status "$($entry.Label) — not found, skipping" 'INFO'
        }
    }
}
