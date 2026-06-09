$ErrorActionPreference = 'SilentlyContinue'

$gameName = 'Casualties Unknown Demo'
$steamRoots = New-Object System.Collections.Generic.List[string]

function Add-SteamRoot {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (Test-Path $expanded) {
        $steamRoots.Add((Resolve-Path $expanded).Path)
    }
}

function Add-LibraryPathsFromVdf {
    param(
        [string] $SteamRoot,
        [System.Collections.Generic.List[string]] $Libraries
    )

    $vdfPath = Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'
    if (!(Test-Path $vdfPath)) {
        return
    }

    $content = Get-Content $vdfPath -Raw
    foreach ($match in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
        $Libraries.Add(($match.Groups[1].Value -replace '\\\\', '\'))
    }

    foreach ($match in [regex]::Matches($content, '"\d+"\s+"([^"]+)"')) {
        $Libraries.Add(($match.Groups[1].Value -replace '\\\\', '\'))
    }
}

function Test-GameDir {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    return (Test-Path (Join-Path $Path 'CasualtiesUnknown_Data\Managed\UnityEngine.CoreModule.dll')) `
        -and (Test-Path (Join-Path $Path 'BepInEx\core\BepInEx.dll'))
}

if ($env:CASUALTIES_UNKNOWN_DIR -and (Test-GameDir $env:CASUALTIES_UNKNOWN_DIR)) {
    Write-Output $env:CASUALTIES_UNKNOWN_DIR
    exit 0
}

$steamPath = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam').SteamPath
Add-SteamRoot $steamPath
Add-SteamRoot (Join-Path ${env:ProgramFiles(x86)} 'Steam')
Add-SteamRoot (Join-Path $env:ProgramFiles 'Steam')

$libraries = New-Object System.Collections.Generic.List[string]
foreach ($root in ($steamRoots | Select-Object -Unique)) {
    $libraries.Add($root)
    Add-LibraryPathsFromVdf -SteamRoot $root -Libraries $libraries
}

foreach ($library in ($libraries | Select-Object -Unique)) {
    $candidate = Join-Path $library "steamapps\common\$gameName"
    if (Test-GameDir $candidate) {
        Write-Output $candidate
        exit 0
    }
}

exit 1
