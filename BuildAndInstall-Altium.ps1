param(
    [string]$Configuration = "Release",
    [string]$AltiumProfile,
    [string]$AltiumExe,
    [string]$AltiumProcessName = "X2",
    [switch]$NoLaunch,
    [switch]$NoPauseOnError
)

try {
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Get-FileHash lives in the Microsoft.PowerShell.Utility module and can be
# missing on some hardened Windows PowerShell 5.1 hosts. Define a faithful
# fallback that mirrors the cmdlet's output (Algorithm/Hash/Path) so the
# install-reporting block works regardless. Only installed when absent, so a
# real cmdlet (when present) keeps running unchanged.
if (-not (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
    function Get-FileHash {
        [CmdletBinding()]
        param(
            [Parameter(ParameterSetName = "Path", Position = 0)]
            [string[]]$Path,
            [Parameter(ParameterSetName = "LiteralPath", Mandatory = $true)]
            [string[]]$LiteralPath,
            [ValidateSet("SHA1", "SHA256", "SHA384", "SHA512", "MD5")]
            [string]$Algorithm = "SHA256"
        )

        $candidates = if ($PSCmdlet.ParameterSetName -eq "LiteralPath") { $LiteralPath } else { $Path }

        foreach ($candidate in $candidates) {
            $absolute = if ($PSCmdlet.ParameterSetName -eq "LiteralPath") {
                $candidate
            } else {
                (Resolve-Path -LiteralPath $candidate).Path
            }

            if (-not (Test-Path -LiteralPath $absolute)) {
                throw "Cannot find path '$absolute' because it does not exist."
            }
            $absolute = (Get-Item -LiteralPath $absolute).FullName

            $hasher = [System.Security.Cryptography.HashAlgorithm]::Create($Algorithm)
            try {
                $stream = [System.IO.File]::OpenRead($absolute)
                try {
                    $hashBytes = $hasher.ComputeHash($stream)
                } finally {
                    $stream.Close()
                }
            } finally {
                $hasher.Dispose()
            }

            $hex = New-Object System.Text.StringBuilder
            foreach ($byte in $hashBytes) {
                [void]$hex.Append($byte.ToString("X2"))
            }

            [pscustomobject]@{
                Algorithm = $Algorithm
                Hash      = $hex.ToString()
                Path      = $absolute
            }
        }
    }
}

function Resolve-AltiumProfile {
    param([string]$ConfiguredProfile)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredProfile)) {
        if (-not (Test-Path -LiteralPath $ConfiguredProfile)) {
            throw "Configured Altium profile was not found: $ConfiguredProfile"
        }

        return (Resolve-Path -LiteralPath $ConfiguredProfile).Path
    }

    $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $altiumRoot = Join-Path $programData "Altium"
    $profileCandidates = @(Get-AltiumProfileCandidates $null)
    if (-not (Test-Path -LiteralPath $altiumRoot)) {
        throw "Could not auto-detect an Altium profile because the Altium ProgramData folder was not found: $altiumRoot. Pass -AltiumProfile explicitly."
    }

    if ($profileCandidates.Count -eq 0) {
        throw "Could not auto-detect an Altium profile under '$altiumRoot'. Pass -AltiumProfile explicitly."
    }

    if ($profileCandidates.Count -gt 1) {
        $candidateList = ($profileCandidates | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Multiple Altium profiles were detected. Pass -AltiumProfile explicitly." + [Environment]::NewLine + $candidateList
    }

    return $profileCandidates[0].FullName
}

function Add-AltiumExecutableCandidate {
    param(
        [System.Collections.Generic.List[string]]$Candidates,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $cleanPath = $Path.Trim()
    if ($cleanPath.StartsWith('"') -and $cleanPath.Contains('"', 1)) {
        $cleanPath = $cleanPath.Substring(1, $cleanPath.IndexOf('"', 1) - 1)
    } elseif ($cleanPath.Contains(",")) {
        $cleanPath = $cleanPath.Substring(0, $cleanPath.IndexOf(",")).Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($cleanPath) -and -not $Candidates.Contains($cleanPath)) {
        $Candidates.Add($cleanPath)
    }
}

function Get-RegistryPropertyValue {
    param(
        [object]$Properties,
        [string]$Name
    )

    if ($Properties -eq $null) {
        return $null
    }

    $property = $Properties.PSObject.Properties[$Name]
    if ($property -eq $null) {
        return $null
    }

    return $property.Value
}

function Get-AltiumProfileCandidates {
    param([string]$UniqueId)

    $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $altiumRoot = Join-Path $programData "Altium"
    if (-not (Test-Path -LiteralPath $altiumRoot)) {
        return @()
    }

    $pattern = if ([string]::IsNullOrWhiteSpace($UniqueId)) { "Altium Designer*" } else { "Altium Designer*$UniqueId*" }
    return @(
        Get-ChildItem -LiteralPath $altiumRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -like $pattern -and
                $_.Name -notlike "*_Security" -and
                (Test-Path -LiteralPath (Join-Path $_.FullName "Extensions\ExtensionsRegistry.xml"))
            } |
            Sort-Object -Property LastWriteTimeUtc -Descending
    )
}

function Get-AltiumRegistryInstallations {
    $installations = New-Object "System.Collections.Generic.List[object]"
    $uninstallRoots = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    foreach ($uninstallRoot in $uninstallRoots) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) {
            continue
        }

        Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $properties = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
            $displayName = Get-RegistryPropertyValue $properties "DisplayName"
            if ($properties -eq $null -or $displayName -notlike "*Altium*Designer*") {
                return
            }

            $uninstallString = Get-RegistryPropertyValue $properties "UninstallString"
            $uniqueId = $null
            foreach ($uniqueIdSource in @($_.PSChildName, $uninstallString)) {
                if ([string]::IsNullOrWhiteSpace($uniqueIdSource)) {
                    continue
                }

                $uniqueIdMatch = [regex]::Match($uniqueIdSource, '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}')
                if ($uniqueIdMatch.Success) {
                    $uniqueId = $uniqueIdMatch.Value
                    break
                }
            }

            if ([string]::IsNullOrWhiteSpace($uniqueId)) {
                return
            }

            $executableCandidates = New-Object "System.Collections.Generic.List[string]"
            $installLocation = Get-RegistryPropertyValue $properties "InstallLocation"
            if (-not [string]::IsNullOrWhiteSpace($installLocation)) {
                Add-AltiumExecutableCandidate -Candidates $executableCandidates -Path (Join-Path $installLocation "X2.EXE")
            }

            $displayIcon = Get-RegistryPropertyValue $properties "DisplayIcon"
            Add-AltiumExecutableCandidate -Candidates $executableCandidates -Path $displayIcon

            $existingExecutables = @(
                $executableCandidates |
                    ForEach-Object { Get-Item -Path $_ -ErrorAction SilentlyContinue } |
                    Where-Object { $_ -ne $null -and -not $_.PSIsContainer } |
                    Sort-Object -Property LastWriteTimeUtc -Descending
            )
            $profiles = Get-AltiumProfileCandidates $uniqueId

            foreach ($profile in $profiles) {
                foreach ($executable in $existingExecutables) {
                    $installations.Add([pscustomobject]@{
                        DisplayName = $displayName
                        DisplayVersion = Get-RegistryPropertyValue $properties "DisplayVersion"
                        UniqueId = $uniqueId
                        ProfilePath = $profile.FullName
                        ExecutablePath = $executable.FullName
                    })
                }
            }
        }
    }

    return @(
        $installations |
            Sort-Object -Property ProfilePath, ExecutablePath -Unique
    )
}

function Resolve-AltiumInstallation {
    param(
        [string]$ConfiguredProfile,
        [string]$ConfiguredExe,
        [string]$ProcessName
    )

    $resolvedProfile = $null
    $resolvedExe = $null

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredProfile)) {
        $resolvedProfile = Resolve-AltiumProfile $ConfiguredProfile
    }

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredExe)) {
        $resolvedExe = Resolve-AltiumExecutable $ConfiguredExe $ProcessName
    }

    if (-not [string]::IsNullOrWhiteSpace($resolvedProfile) -and -not [string]::IsNullOrWhiteSpace($resolvedExe)) {
        return [pscustomobject]@{
            ProfilePath = $resolvedProfile
            ExecutablePath = $resolvedExe
        }
    }

    $installations = @(Get-AltiumRegistryInstallations)
    if (-not [string]::IsNullOrWhiteSpace($resolvedProfile)) {
        $installations = @($installations | Where-Object { $_.ProfilePath -eq $resolvedProfile })
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedExe)) {
        $installations = @($installations | Where-Object { $_.ExecutablePath -eq $resolvedExe })
    }

    if ($installations.Count -eq 1) {
        return [pscustomobject]@{
            ProfilePath = $installations[0].ProfilePath
            ExecutablePath = $installations[0].ExecutablePath
        }
    }

    if ($installations.Count -gt 1) {
        $candidateList = ($installations | ForEach-Object {
            "$($_.DisplayName) $($_.DisplayVersion) $($_.UniqueId): Profile='$($_.ProfilePath)' Exe='$($_.ExecutablePath)'"
        }) -join [Environment]::NewLine
        throw "Multiple Altium installations were detected. Pass -AltiumProfile and -AltiumExe explicitly." + [Environment]::NewLine + $candidateList
    }

    if ([string]::IsNullOrWhiteSpace($resolvedProfile)) {
        $resolvedProfile = Resolve-AltiumProfile $ConfiguredProfile
    }
    if ([string]::IsNullOrWhiteSpace($resolvedExe)) {
        $resolvedExe = Resolve-AltiumExecutable $ConfiguredExe $ProcessName
    }

    return [pscustomobject]@{
        ProfilePath = $resolvedProfile
        ExecutablePath = $resolvedExe
    }
}

function Add-AltiumExecutableRegistryCandidates {
    param([System.Collections.Generic.List[string]]$Candidates)

    $appPathKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\X2.EXE",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\X2.EXE",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\X2.EXE"
    )

    foreach ($appPathKey in $appPathKeys) {
        $key = Get-Item -LiteralPath $appPathKey -ErrorAction SilentlyContinue
        if ($key -ne $null) {
            Add-AltiumExecutableCandidate -Candidates $Candidates -Path $key.GetValue("")
        }
    }

    $uninstallRoots = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    foreach ($uninstallRoot in $uninstallRoots) {
        if (-not (Test-Path -LiteralPath $uninstallRoot)) {
            continue
        }

        Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $properties = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
            $displayName = Get-RegistryPropertyValue $properties "DisplayName"
            if ($properties -eq $null -or $displayName -notlike "*Altium*Designer*") {
                return
            }

            $installLocation = Get-RegistryPropertyValue $properties "InstallLocation"
            if (-not [string]::IsNullOrWhiteSpace($installLocation)) {
                Add-AltiumExecutableCandidate -Candidates $Candidates -Path (Join-Path $installLocation "X2.EXE")
            }

            $displayIcon = Get-RegistryPropertyValue $properties "DisplayIcon"
            Add-AltiumExecutableCandidate -Candidates $Candidates -Path $displayIcon
        }
    }
}

function Resolve-AltiumExecutable {
    param(
        [string]$ConfiguredExe,
        [string]$ProcessName
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredExe)) {
        if (-not (Test-Path -LiteralPath $ConfiguredExe)) {
            throw "Configured Altium executable was not found: $ConfiguredExe"
        }

        return (Resolve-Path -LiteralPath $ConfiguredExe).Path
    }

    $candidates = New-Object "System.Collections.Generic.List[string]"

    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            Add-AltiumExecutableCandidate -Candidates $candidates -Path $_.Path
        } catch {
        }
    }

    Add-AltiumExecutableRegistryCandidates -Candidates $candidates

    $command = Get-Command ($ProcessName + ".exe") -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command -ne $null) {
        Add-AltiumExecutableCandidate -Candidates $candidates -Path $command.Source
    }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    foreach ($root in @($programFiles, $programFilesX86)) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        Add-AltiumExecutableCandidate -Candidates $candidates -Path (Join-Path $root "Altium\AD*\X2.EXE")
        Add-AltiumExecutableCandidate -Candidates $candidates -Path (Join-Path $root "Altium\Altium Designer*\X2.EXE")
        Add-AltiumExecutableCandidate -Candidates $candidates -Path (Join-Path $root "AD*\X2.EXE")
        Add-AltiumExecutableCandidate -Candidates $candidates -Path (Join-Path $root "ADAgile\X2.EXE")
    }

    $existingCandidates = @(
        $candidates |
            ForEach-Object {
                Get-Item -Path $_ -ErrorAction SilentlyContinue
            } |
            Where-Object { $_ -ne $null -and -not $_.PSIsContainer } |
            Sort-Object -Property FullName -Unique
    )

    if ($existingCandidates.Count -eq 0) {
        throw "Could not auto-detect Altium X2.EXE. Pass -AltiumExe explicitly."
    }

    if ($existingCandidates.Count -gt 1) {
        $candidateList = ($existingCandidates | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Multiple Altium executables were detected. Pass -AltiumExe explicitly." + [Environment]::NewLine + $candidateList
    }

    return $existingCandidates[0].FullName
}

function Resolve-F3DNativeLibrary {
    $configuredPath = [Environment]::GetEnvironmentVariable("STEPCLEANER_F3D_LIB")
    if (-not [string]::IsNullOrWhiteSpace($configuredPath) -and (Test-Path -LiteralPath $configuredPath)) {
        return (Resolve-Path -LiteralPath $configuredPath).Path
    }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $candidates = @(
        (Join-Path $programFiles "F3D\bin\f3d_c_api.dll"),
        (Join-Path $programFilesX86 "F3D\bin\f3d_c_api.dll")
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Get-FileVersionOrNull {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $fileVersion = (Get-Item -LiteralPath $Path).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($fileVersion)) {
        return $null
    }

    $match = [regex]::Match($fileVersion, '^\d+(\.\d+){1,3}')
    if (-not $match.Success) {
        return $null
    }

    return [version]$match.Value
}

function Install-F3DCompatibleMsvcRuntime {
    param(
        [string]$F3DRuntimeSourceDir,
        [string]$AltiumExecutablePath
    )

    if ([string]::IsNullOrWhiteSpace($F3DRuntimeSourceDir) -or -not (Test-Path -LiteralPath $F3DRuntimeSourceDir)) {
        return
    }

    $sourceMsvcp = Join-Path $F3DRuntimeSourceDir "MSVCP140.dll"
    if (-not (Test-Path -LiteralPath $sourceMsvcp)) {
        return
    }

    $altiumExeDir = Split-Path -Parent $AltiumExecutablePath
    $targetMsvcp = Join-Path $altiumExeDir "MSVCP140.dll"
    $sourceVersion = Get-FileVersionOrNull $sourceMsvcp
    $targetVersion = Get-FileVersionOrNull $targetMsvcp

    if ($sourceVersion -eq $null -or $targetVersion -eq $null) {
        Write-Warning "Could not compare MSVCP140.dll versions. F3D source='$sourceMsvcp' Altium target='$targetMsvcp'."
        return
    }

    if ($targetVersion -ge $sourceVersion) {
        Write-Host "Altium MSVCP140.dll is compatible: $targetVersion"
        return
    }

    $backupDirectory = Join-Path $altiumExeDir "EasyEDA-Loader-MsvcBackup"
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    $backupPath = Join-Path $backupDirectory ("MSVCP140.dll." + (Get-Date -Format "yyyyMMdd-HHmmss") + ".bak")
    Copy-Item -LiteralPath $targetMsvcp -Destination $backupPath -Force
    Copy-Item -LiteralPath $sourceMsvcp -Destination $targetMsvcp -Force

    $installedVersion = Get-FileVersionOrNull $targetMsvcp
    Write-Host "Updated Altium MSVCP140.dll for in-process F3D: $targetVersion -> $installedVersion"
    Write-Host "Altium MSVCP140.dll backup: $backupPath"
}

function Assert-AltiumClosed {
    $processes = @(Get-Process -Name $AltiumProcessName -ErrorAction SilentlyContinue)
    if ($processes.Count -gt 0) {
        $processList = ($processes | ForEach-Object {
            $startTime = try { $_.StartTime } catch { "unknown start time" }
            "PID $($_.Id), started $startTime"
        }) -join "; "

        throw "Altium is running ($processList). Close Altium and run this script again. Installation is allowed only when Altium is closed."
    }
}

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot "EasyEDA-Loader\EasyEDA-Loader.csproj"
$tronstolProjectPath = Join-Path $repoRoot "TronstolE1Pnp\TronstolE1Pnp.csproj"
$scanGerberProjectPath = Join-Path $repoRoot "TronstolE1ScanGerber\TronstolE1ScanGerber.csproj"
$shapeSvgProjectPath = Join-Path $repoRoot "EasyEDAShapeSvg\EasyEDAShapeSvg.csproj"
$helperProjectPath = Join-Path $repoRoot "StepOcctHlr\StepOcctHlr.csproj"
$f3dHelperProjectPath = Join-Path $repoRoot "StepF3DRender\StepF3DRender.csproj"
$sourceDir = Split-Path -Parent $projectPath
$tronstolSourceDir = Split-Path -Parent $tronstolProjectPath
$scanGerberSourceDir = Split-Path -Parent $scanGerberProjectPath
$shapeSvgSourceDir = Split-Path -Parent $shapeSvgProjectPath
$helperSourceDir = Split-Path -Parent $helperProjectPath
$f3dHelperSourceDir = Split-Path -Parent $f3dHelperProjectPath
$altiumInstallation = Resolve-AltiumInstallation -ConfiguredProfile $AltiumProfile -ConfiguredExe $AltiumExe -ProcessName $AltiumProcessName
$AltiumProfile = $altiumInstallation.ProfilePath
$AltiumExe = $altiumInstallation.ExecutablePath
$installDir = Join-Path $AltiumProfile "Extensions\EasyEDA-Loader"
$registryPath = Join-Path $AltiumProfile "Extensions\ExtensionsRegistry.xml"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $tronstolProjectPath)) {
    throw "Tronstol E1 PNP output project file was not found: $tronstolProjectPath"
}

if (-not (Test-Path -LiteralPath $shapeSvgProjectPath)) {
    throw "EasyEDA Shape SVG output project file was not found: $shapeSvgProjectPath"
}

if (-not (Test-Path -LiteralPath $scanGerberProjectPath)) {
    throw "Tronstol E1 Scan Gerber output project file was not found: $scanGerberProjectPath"
}

if (-not (Test-Path -LiteralPath $helperProjectPath)) {
    throw "OCCT HLR helper project file was not found: $helperProjectPath"
}

if (-not (Test-Path -LiteralPath $f3dHelperProjectPath)) {
    throw "F3D render helper project file was not found: $f3dHelperProjectPath"
}

if (-not (Test-Path -LiteralPath $registryPath)) {
    throw "Altium extensions registry was not found: $registryPath"
}

if (-not (Test-Path -LiteralPath $AltiumExe)) {
    throw "Altium executable was not found: $AltiumExe"
}

Write-Host "Altium profile: $AltiumProfile"
Write-Host "Altium executable: $AltiumExe"

Assert-AltiumClosed

$altiumSystemDirectory = Join-Path (Split-Path -Parent $AltiumExe) "System"
$devExpressDirectory = Join-Path $altiumSystemDirectory "DotNET\DevExpress.Wpf"
$devExpressDataAssemblies = @(Get-ChildItem -LiteralPath $devExpressDirectory -Filter "DevExpress.Data.v*.dll")
if ($devExpressDataAssemblies.Count -ne 1) {
    throw "Expected one DevExpress.Data assembly in '$devExpressDirectory'; found $($devExpressDataAssemblies.Count)."
}
$devExpressVersionMatch = [regex]::Match($devExpressDataAssemblies[0].BaseName, '^DevExpress\.Data\.v(\d+\.\d+)$')
if (-not $devExpressVersionMatch.Success) {
    throw "Could not determine the installed DevExpress version from '$($devExpressDataAssemblies[0].Name)'."
}
$altiumBuildProperties = @(
    "-p:AltiumSystemDirectory=$altiumSystemDirectory",
    "-p:DevExpressDirectory=$devExpressDirectory",
    "-p:DevExpressVersion=$($devExpressVersionMatch.Groups[1].Value)"
)
Write-Host "Building against installed Altium SDK and DevExpress $($devExpressVersionMatch.Groups[1].Value)"

Write-Step "Building EasyEDA Loader ($Configuration)"
dotnet build $projectPath -c $Configuration --nologo -v:q -clp:ErrorsOnly @altiumBuildProperties
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for EasyEDA Loader with exit code $LASTEXITCODE."
}

Write-Step "Building Tronstol E1 PNP output generator ($Configuration)"
dotnet build $tronstolProjectPath -c $Configuration --nologo -v:q -clp:ErrorsOnly @altiumBuildProperties
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for Tronstol E1 PNP output generator with exit code $LASTEXITCODE."
}

Write-Step "Building EasyEDA Shape SVG output generator ($Configuration)"
dotnet build $shapeSvgProjectPath -c $Configuration --nologo -v:q -clp:ErrorsOnly @altiumBuildProperties
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for EasyEDA Shape SVG output generator with exit code $LASTEXITCODE."
}

Write-Step "Building Tronstol E1 Scan Gerber output generator ($Configuration)"
dotnet build $scanGerberProjectPath -c $Configuration --nologo -v:q -clp:ErrorsOnly @altiumBuildProperties
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for Tronstol E1 Scan Gerber output generator with exit code $LASTEXITCODE."
}

Write-Step "Building OCCT HLR helper ($Configuration)"
dotnet build $helperProjectPath -c $Configuration --nologo -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OCCT HLR helper with exit code $LASTEXITCODE."
}

Write-Step "Building F3D render helper ($Configuration)"
dotnet build $f3dHelperProjectPath -c $Configuration --nologo -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for F3D render helper with exit code $LASTEXITCODE."
}

Assert-AltiumClosed

[xml]$projectXml = Get-Content -LiteralPath $projectPath
$targetFramework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    $targetFramework = "net8.0-windows"
}

$buildDir = Join-Path $sourceDir "bin\$Configuration\$targetFramework"
$builtDll = Join-Path $buildDir "EasyEDA-Loader.dll"
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "Built DLL was not found: $builtDll"
}

[xml]$tronstolProjectXml = Get-Content -LiteralPath $tronstolProjectPath
$tronstolTargetFramework = @($tronstolProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($tronstolTargetFramework)) {
    $tronstolTargetFramework = "net8.0-windows"
}

$tronstolBuildDir = Join-Path $tronstolSourceDir "bin\$Configuration\$tronstolTargetFramework"
$builtTronstolDll = Join-Path $tronstolBuildDir "TronstolE1Pnp.Outputer.dll"
if (-not (Test-Path -LiteralPath $builtTronstolDll)) {
    throw "Built Tronstol E1 PNP output DLL was not found: $builtTronstolDll"
}

[xml]$scanGerberProjectXml = Get-Content -LiteralPath $scanGerberProjectPath
$scanGerberTargetFramework = @($scanGerberProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
$scanGerberBuildDir = Join-Path $scanGerberSourceDir "bin\$Configuration\$scanGerberTargetFramework"
$builtScanGerberDll = Join-Path $scanGerberBuildDir "TronstolE1ScanGerber.Outputer.dll"
if (-not (Test-Path -LiteralPath $builtScanGerberDll)) {
    throw "Built Tronstol E1 Scan Gerber output DLL was not found: $builtScanGerberDll"
}

[xml]$shapeSvgProjectXml = Get-Content -LiteralPath $shapeSvgProjectPath
$shapeSvgTargetFramework = @($shapeSvgProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($shapeSvgTargetFramework)) {
    $shapeSvgTargetFramework = "net8.0-windows"
}

$shapeSvgBuildDir = Join-Path $shapeSvgSourceDir "bin\$Configuration\$shapeSvgTargetFramework"
$builtShapeSvgDll = Join-Path $shapeSvgBuildDir "EasyEDAShapeSvg.Outputer.dll"
if (-not (Test-Path -LiteralPath $builtShapeSvgDll)) {
    throw "Built EasyEDA Shape SVG output DLL was not found: $builtShapeSvgDll"
}

[xml]$helperProjectXml = Get-Content -LiteralPath $helperProjectPath
$helperTargetFramework = @($helperProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($helperTargetFramework)) {
    $helperTargetFramework = "net8.0-windows7.0"
}

$helperRuntimeIdentifier = @($helperProjectXml.Project.PropertyGroup.RuntimeIdentifier | Where-Object { $_ } | Select-Object -First 1)[0]
$helperBuildDir = Join-Path $helperSourceDir "bin\$Configuration\$helperTargetFramework"
if (-not [string]::IsNullOrWhiteSpace($helperRuntimeIdentifier)) {
    $helperBuildDir = Join-Path $helperBuildDir $helperRuntimeIdentifier
}

$builtHelperExe = Join-Path $helperBuildDir "StepOcctHlr.exe"
if (-not (Test-Path -LiteralPath $builtHelperExe)) {
    throw "Built OCCT HLR helper executable was not found: $builtHelperExe"
}

[xml]$f3dHelperProjectXml = Get-Content -LiteralPath $f3dHelperProjectPath
$f3dHelperTargetFramework = @($f3dHelperProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($f3dHelperTargetFramework)) {
    $f3dHelperTargetFramework = "net8.0-windows7.0"
}

$f3dHelperRuntimeIdentifier = @($f3dHelperProjectXml.Project.PropertyGroup.RuntimeIdentifier | Where-Object { $_ } | Select-Object -First 1)[0]
$f3dHelperBuildDir = Join-Path $f3dHelperSourceDir "bin\$Configuration\$f3dHelperTargetFramework"
if (-not [string]::IsNullOrWhiteSpace($f3dHelperRuntimeIdentifier)) {
    $f3dHelperBuildDir = Join-Path $f3dHelperBuildDir $f3dHelperRuntimeIdentifier
}

$builtF3DHelperExe = Join-Path $f3dHelperBuildDir "StepF3DRender.exe"
if (-not (Test-Path -LiteralPath $builtF3DHelperExe)) {
    throw "Built F3D render helper executable was not found: $builtF3DHelperExe"
}

$f3dNativeLibraryPath = Resolve-F3DNativeLibrary
$f3dNativeRuntimeSourceDir = $null
if ([string]::IsNullOrWhiteSpace($f3dNativeLibraryPath)) {
    Write-Warning "F3D native library f3d_c_api.dll was not found. Install F3D or set STEPCLEANER_F3D_LIB if internal STEP preview is needed."
} else {
    $f3dNativeRuntimeSourceDir = Split-Path -Parent $f3dNativeLibraryPath
}

Write-Step "Installing to Altium extension folder"
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -Path (Join-Path $buildDir "*") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "EasyEDA-Loader.ins") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "EasyEDA-Loader.rcs") -Destination $installDir -Force
Copy-Item -Path (Join-Path $tronstolBuildDir "TronstolE1Pnp.Outputer.*") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $tronstolSourceDir "TronstolE1Pnp.OUT") -Destination $installDir -Force
Copy-Item -Path (Join-Path $scanGerberBuildDir "TronstolE1ScanGerber.Outputer.*") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $scanGerberSourceDir "TronstolE1ScanGerber.OUT") -Destination $installDir -Force
Copy-Item -Path (Join-Path $shapeSvgBuildDir "EasyEDAShapeSvg.Outputer.*") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $shapeSvgSourceDir "EasyEDAShapeSvg.OUT") -Destination $installDir -Force

$helperInstallDir = Join-Path $installDir "StepOcctHlr"
New-Item -ItemType Directory -Path $helperInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $helperBuildDir "*") -Destination $helperInstallDir -Recurse -Force

$f3dHelperInstallDir = Join-Path $installDir "StepF3DRender"
New-Item -ItemType Directory -Path $f3dHelperInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $f3dHelperBuildDir "*") -Destination $f3dHelperInstallDir -Recurse -Force

$f3dNativeInstallDir = Join-Path $installDir "F3D\bin"
$installedF3DNativeLibrary = Join-Path $f3dNativeInstallDir "f3d_c_api.dll"
if (-not [string]::IsNullOrWhiteSpace($f3dNativeRuntimeSourceDir)) {
    New-Item -ItemType Directory -Path $f3dNativeInstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $f3dNativeRuntimeSourceDir "*") -Destination $f3dNativeInstallDir -Recurse -Force

    if (-not (Test-Path -LiteralPath $installedF3DNativeLibrary)) {
        throw "Installed F3D native library was not found: $installedF3DNativeLibrary"
    }

    Install-F3DCompatibleMsvcRuntime -F3DRuntimeSourceDir $f3dNativeRuntimeSourceDir -AltiumExecutablePath $AltiumExe
}

$installedDll = Join-Path $installDir "EasyEDA-Loader.dll"
$installedTronstolDll = Join-Path $installDir "TronstolE1Pnp.Outputer.dll"
$installedTronstolConfig = Join-Path $installDir "TronstolE1Pnp.OUT"
$installedScanGerberDll = Join-Path $installDir "TronstolE1ScanGerber.Outputer.dll"
$installedScanGerberConfig = Join-Path $installDir "TronstolE1ScanGerber.OUT"
$installedShapeSvgDll = Join-Path $installDir "EasyEDAShapeSvg.Outputer.dll"
$installedShapeSvgConfig = Join-Path $installDir "EasyEDAShapeSvg.OUT"
if (-not (Test-Path -LiteralPath $installedTronstolDll)) {
    throw "Installed Tronstol E1 PNP output DLL was not found: $installedTronstolDll"
}
if (-not (Test-Path -LiteralPath $installedTronstolConfig)) {
    throw "Installed Tronstol E1 PNP output registration was not found: $installedTronstolConfig"
}
if (-not (Test-Path -LiteralPath $installedScanGerberDll) -or -not (Test-Path -LiteralPath $installedScanGerberConfig)) {
    throw "Installed Tronstol E1 Scan Gerber output DLL or registration was not found."
}
if (-not (Test-Path -LiteralPath $installedShapeSvgDll)) {
    throw "Installed EasyEDA Shape SVG output DLL was not found: $installedShapeSvgDll"
}
if (-not (Test-Path -LiteralPath $installedShapeSvgConfig)) {
    throw "Installed EasyEDA Shape SVG output registration was not found: $installedShapeSvgConfig"
}

$installedHelperExe = Join-Path $helperInstallDir "StepOcctHlr.exe"
if (-not (Test-Path -LiteralPath $installedHelperExe)) {
    throw "Installed OCCT HLR helper executable was not found: $installedHelperExe"
}

$installedF3DHelperExe = Join-Path $f3dHelperInstallDir "StepF3DRender.exe"
if (-not (Test-Path -LiteralPath $installedF3DHelperExe)) {
    throw "Installed F3D render helper executable was not found: $installedF3DHelperExe"
}

$assembly = [System.Reflection.AssemblyName]::GetAssemblyName($installedDll)
$version = $assembly.Version.ToString()
$versionGuid = [guid]::NewGuid().ToString("B").ToUpperInvariant()

Write-Step "Updating Altium extension registry"
$registryText = Get-Content -LiteralPath $registryPath -Raw
$pattern = '(?s)(<Item HRID="EasyEDA-Loader".*?<Version>)(.*?)(</Version>.*?<VersionGuid>)(.*?)(</VersionGuid>)'
$replacement = '${1}' + $version + '${3}' + $versionGuid + '${5}'
$updatedRegistryText = [regex]::Replace($registryText, $pattern, $replacement, 1)
if ($updatedRegistryText -eq $registryText) {
    throw "EasyEDA-Loader registry item was not updated; pattern did not match."
}

$registryBackupDir = Join-Path $AltiumProfile "ExtensionBackups\RegistryBackups"
New-Item -ItemType Directory -Force -Path $registryBackupDir | Out-Null
$registryBackupPath = Join-Path $registryBackupDir ("ExtensionsRegistry.xml.bak-before-easyeda-install-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
Copy-Item -LiteralPath $registryPath -Destination $registryBackupPath -Force

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($registryPath, $updatedRegistryText, $utf8NoBom)

[xml]$registryXml = Get-Content -LiteralPath $registryPath
$registryItems = @($registryXml.Extensions.Item).Count
$nonItemNodes = @($registryXml.Extensions.ChildNodes | Where-Object { $_.LocalName -ne "Item" }).Count
$easyEdaItem = @($registryXml.Extensions.Item | Where-Object { $_.HRID -eq "EasyEDA-Loader" }) | Select-Object -First 1

$hash = (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash
$tronstolHash = (Get-FileHash -LiteralPath $installedTronstolDll -Algorithm SHA256).Hash
$helperHash = (Get-FileHash -LiteralPath $installedHelperExe -Algorithm SHA256).Hash
$f3dHelperHash = (Get-FileHash -LiteralPath $installedF3DHelperExe -Algorithm SHA256).Hash
$f3dNativeHash = $null
if (Test-Path -LiteralPath $installedF3DNativeLibrary) {
    $f3dNativeHash = (Get-FileHash -LiteralPath $installedF3DNativeLibrary -Algorithm SHA256).Hash
}
Write-Host "Installed DLL: $installedDll"
Write-Host "Assembly version: $version"
Write-Host "SHA256: $hash"
Write-Host "Installed Tronstol E1 PNP output DLL: $installedTronstolDll"
Write-Host "Tronstol E1 PNP SHA256: $tronstolHash"
Write-Host "Installed Tronstol E1 Scan Gerber output DLL: $installedScanGerberDll"
Write-Host "Installed OCCT HLR helper: $installedHelperExe"
Write-Host "OCCT HLR helper SHA256: $helperHash"
Write-Host "Installed F3D render helper: $installedF3DHelperExe"
Write-Host "F3D render helper SHA256: $f3dHelperHash"
if (-not [string]::IsNullOrWhiteSpace($f3dNativeHash)) {
    Write-Host "Installed F3D native library: $installedF3DNativeLibrary"
    Write-Host "F3D native library SHA256: $f3dNativeHash"
} else {
    Write-Host "Installed F3D native library: not bundled; f3d_c_api.dll was not found"
}
Write-Host "Registry Items=$registryItems NonItem=$nonItemNodes EasyEDA=$($easyEdaItem.Version) VersionGuid=$($easyEdaItem.VersionGuid)"
Write-Host "Registry backup: $registryBackupPath"

if (-not $NoLaunch) {
    Write-Step "Launching Altium"
    Start-Process -FilePath $AltiumExe
}
}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red

    if (-not $NoPauseOnError) {
        Write-Host ""
        Read-Host "Press Enter to close this window" | Out-Null
    }

    exit 1
}
