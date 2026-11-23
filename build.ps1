param(
    [string]$Sdk,
    [string]$Nsis,
    [string]$ModelArchive,
    [string]$BrandIcon
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildDir = Join-Path $root '.build'
$distribution = Join-Path $root 'dist'
$staging = Join-Path (Join-Path $buildDir 'artifacts') ([Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'app'
$sample = Join-Path $staging 'SoftGain'
$packages = Join-Path $buildDir 'packages'
$brand = Get-Content -LiteralPath (Join-Path $root 'branding.json') -Raw -Encoding utf8 | ConvertFrom-Json

if ($brand.name -notmatch '^[\p{L}\p{N}][\p{L}\p{N} _.\-]{0,31}(?<![ .])$' -or
    $brand.installerName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_\-]{0,49}$' -or
    $brand.version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
    ($brand.version.Split('.') | Where-Object { [int]$_ -gt 65535 })) {
    throw 'Check name, installerName and version in branding.json.'
}

function Find-Tool([string]$supplied, [string]$bundled, [string]$name) {
    if ($supplied) { return (Resolve-Path -LiteralPath $supplied).Path }
    if (Test-Path -LiteralPath $bundled -PathType Leaf) { return $bundled }
    return (Get-Command $name -ErrorAction Stop).Source
}

$Sdk = Find-Tool $Sdk (Join-Path $buildDir 'dotnet\dotnet.exe') 'dotnet.exe'
$Nsis = Find-Tool $Nsis (Join-Path $buildDir 'nsis-3.11\makensis.exe') 'makensis.exe'
if (!$BrandIcon) { $BrandIcon = Join-Path $root 'installer\assistant.ico' }
if (!$ModelArchive) { $ModelArchive = Join-Path $buildDir 'vosk-model-small-cn-0.22.zip' }
$BrandIcon = (Resolve-Path -LiteralPath $BrandIcon).Path
$modelDigest = '3AF8B0E7E0F835AE9D414CE5DF580237A3CFB08D586C9FBBB0F7FF29AD5B14BA'
if ((Get-FileHash -LiteralPath $ModelArchive -Algorithm SHA256).Hash -ne $modelDigest) {
    throw 'The Vosk Chinese model checksum differs from the expected release.'
}

New-Item -ItemType Directory -Force -Path $payload,$sample,$distribution | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $buildDir 'cli'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $buildDir 'nuget-http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

foreach ($project in @('src\Desktop\Desktop.csproj', 'samples\SoftGain\SoftGain.csproj')) {
    & $Sdk restore (Join-Path $root $project) --locked-mode --packages $packages
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $project." }
}

$appArguments = @(
    'publish', (Join-Path $root 'src\Desktop\Desktop.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', '--no-restore', '-o', $payload, "-p:Product=$($brand.name)",
    "-p:Version=$($brand.version)", "-p:BrandIcon=$BrandIcon", '-p:DebugType=None'
)
& $Sdk @appArguments
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
& $Sdk publish (Join-Path $root 'samples\SoftGain\SoftGain.csproj') -c Release -r win-x64 --self-contained --no-restore -o $sample -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Plugin publish failed.' }

$modelFolder = Join-Path $payload 'models'
New-Item -ItemType Directory -Path $modelFolder | Out-Null
tar.exe -xf $ModelArchive -C $modelFolder
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $modelFolder 'vosk-model-small-cn-0.22\am\final.mdl'))) {
    throw 'Could not extract the Chinese model.'
}

Copy-Item -LiteralPath (Join-Path $root 'ThirdPartyNotices.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'docs\voice-plugins.md') -Destination $sample
$noticeDir = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $noticeDir | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $root 'src\Desktop\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtime = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
    Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' } | Select-Object -First 1
if (!$runtime) { throw 'Could not locate the .NET runtime in the restore metadata.' }
$runtimeVersion = $runtime.version.Trim('[', ']', ' ').Split(',')[0].Trim()
foreach ($notice in @{
    'dotnet-license.txt' = "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT"
    'dotnet-notices.txt' = "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT"
    'wpf-license.txt' = "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion\LICENSE"
    'system-speech-notices.txt' = 'system.speech\10.0.15\THIRD-PARTY-NOTICES.TXT'
    'protected-data-notices.txt' = 'system.security.cryptography.protecteddata\10.0.15\THIRD-PARTY-NOTICES.TXT'
}.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $packages $notice.Value) -Destination (Join-Path $noticeDir $notice.Key)
}
Copy-Item -Path (Join-Path $root 'installer\licenses\*.txt') -Destination $noticeDir
$nsisNotice = Join-Path (Split-Path -Parent $Nsis) 'COPYING'
if (Test-Path -LiteralPath $nsisNotice) {
    Copy-Item -LiteralPath $nsisNotice -Destination (Join-Path $noticeDir 'nsis.txt')
}
Copy-Item -LiteralPath (Join-Path $noticeDir 'dotnet-license.txt'),(Join-Path $noticeDir 'dotnet-notices.txt') -Destination $sample

$manifest = Join-Path $staging 'uninstall-files.nsh'
$relativeBase = $payload + [IO.Path]::DirectorySeparatorChar
$uninstall = @(
    Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
        'Delete "$INSTDIR\app\' + $_.FullName.Substring($relativeBase.Length) + '"'
    }
    Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        'RMDir "$INSTDIR\app\' + $_.FullName.Substring($relativeBase.Length) + '"'
    }
    'RMDir "$INSTDIR\app"'
)
[IO.File]::WriteAllLines($manifest, $uninstall, [Text.UTF8Encoding]::new($true))

$installer = Join-Path $distribution "$($brand.installerName)-Setup-$($brand.version).exe"
$nsisArguments = @(
    '/V2', '/INPUTCHARSET', 'UTF8', "/DPRODUCT_NAME=$($brand.name)", "/DPRODUCT_VERSION=$($brand.version)", "/DOUTPUT_EXE=$installer",
    "/DBRAND_ICON=$BrandIcon", "/DPAYLOAD=$payload", "/DUNINSTALL_FILES=$manifest", (Join-Path $root 'installer\setup.nsi')
)
& $Nsis @nsisArguments
if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed.' }
Compress-Archive -LiteralPath $sample -DestinationPath (Join-Path $distribution 'SoftGain-Plugin.zip') -Force
$releaseManifest = Join-Path $distribution 'update-windows-x64.json'
$release = [ordered]@{
    version = $brand.version
    size = (Get-Item -LiteralPath $installer).Length
    sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText($releaseManifest, ($release | ConvertTo-Json -Compress) + "`n")
Write-Output "Update manifest: $releaseManifest"
Write-Output "Installer: $installer"
Write-Output "Published app: $payload"
