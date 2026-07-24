[CmdletBinding()]
param(
    [ValidateSet('Public', 'Personal')]
    [string]$Mode = 'Public',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [string]$PersonalSettingsSource = '',

    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\GalleryBrowser.App\GalleryBrowser.App.csproj'
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactRoot 'release'
}
$outputRootPath = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not $outputRootPath.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be located under $artifactRoot"
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'The application version is not defined in GalleryBrowser.App.csproj.'
}
$displayVersion = if ($version.EndsWith('.0', [System.StringComparison]::Ordinal)) {
    $version.Substring(0, $version.Length - 2)
} else {
    $version
}
$packageSuffix = if ($Mode -eq 'Personal') { '-personal' } else { '' }
$packageName = "GalleryBrowser-$displayVersion$packageSuffix-$Runtime"
$packagePath = [System.IO.Path]::GetFullPath((Join-Path $outputRootPath $packageName))
if (-not $packagePath.StartsWith($outputRootPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe package path: $packagePath"
}

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Recurse -Force
}
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null

$publishArguments = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $packagePath,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $packagePath -Recurse -File |
    Where-Object { $_.Extension -in '.pdb', '.xml' } |
    Remove-Item -Force

$dataPath = Join-Path $packagePath 'data'
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
$portableMarkerPath = Join-Path $packagePath 'GalleryBrowser.portable'
if ($Mode -eq 'Public') {
    Set-Content -LiteralPath $portableMarkerPath -Value '%LOCALAPPDATA%\GalleryBrowser' -NoNewline -Encoding utf8
} else {
    New-Item -ItemType File -Path $portableMarkerPath -Force | Out-Null
}

if ($Mode -eq 'Personal') {
    if ([string]::IsNullOrWhiteSpace($PersonalSettingsSource)) {
        $PersonalSettingsSource = Join-Path $repositoryRoot 'data'
    }
    $settingsSourcePath = [System.IO.Path]::GetFullPath($PersonalSettingsSource)
    foreach ($fileName in @('appsettings.json', 'ui-state.json', 'storage.json', 'creator-tracking-metrics.json')) {
        $sourcePath = Join-Path $settingsSourcePath $fileName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Personal setting file was not found: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $dataPath $fileName) -Force
    }
}

if ($Mode -eq 'Public') {
    $requiredFiles = @(
        (Join-Path $packagePath 'GalleryBrowser.exe'),
        (Join-Path $packagePath 'GalleryBrowser.portable'),
        (Join-Path $packagePath 'webui\index.html')
    )
    $missingRequiredFiles = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missingRequiredFiles.Count -gt 0) {
        throw "Public package is incomplete: $($missingRequiredFiles -join ', ')"
    }

    $unexpectedDataFiles = @(Get-ChildItem -LiteralPath $dataPath -Force)
    if ($unexpectedDataFiles.Count -gt 0) {
        throw "Public package data directory must be empty: $($unexpectedDataFiles.Name -join ', ')"
    }

    $portableTarget = [System.IO.File]::ReadAllText($portableMarkerPath).Trim()
    if (-not [string]::Equals($portableTarget, '%LOCALAPPDATA%\GalleryBrowser', [System.StringComparison]::Ordinal)) {
        throw "Public package has an unsafe portable target: $portableTarget"
    }

    $forbiddenNames = @(
        'appsettings.json',
        'ui-state.json',
        'storage.json',
        'creator-tracking-metrics.json'
    )
    $forbiddenFiles = @(
        Get-ChildItem -LiteralPath $packagePath -Recurse -Force -File |
            Where-Object {
                $_.Name -in $forbiddenNames -or
                $_.Extension -in '.sqlite', '.db', '.pdb', '.xml'
            }
    )
    if ($forbiddenFiles.Count -gt 0) {
        throw "Public package contains private or generated files: $($forbiddenFiles.FullName -join ', ')"
    }
}

$files = Get-ChildItem -LiteralPath $packagePath -Recurse -File
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
[pscustomobject]@{
    Package = $packagePath
    Mode = $Mode
    Version = $version
    Runtime = $Runtime
    Files = $files.Count
    SizeMiB = [Math]::Round($totalBytes / 1MB, 2)
}
