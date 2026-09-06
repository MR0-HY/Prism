#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Build','Test','Package')][string]$Task = 'Build',
    [string]$DotnetPath = 'dotnet',
    [string]$PackageCache,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$buildRoot = Join-Path $root '.build'
[void][IO.Directory]::CreateDirectory($buildRoot)
$savedEnvironment = @{}
$overrides = @{ DOTNET_CLI_HOME=(Join-Path $buildRoot 'dotnet-home'); DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_NOLOGO='1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='1' }
if ($PackageCache) { $overrides.NUGET_PACKAGES = [IO.Path]::GetFullPath($PackageCache) }
foreach ($name in $overrides.Keys) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,$overrides[$name],'Process') }
function Invoke-Dotnet([string[]]$Arguments) { & $DotnetPath @Arguments; if ($LASTEXITCODE -ne 0) { throw "dotnet command failed (exit $LASTEXITCODE)" } }
Push-Location -LiteralPath $root
try {
    $restoreArgs = @('--disable-parallel','-p:NuGetAudit=false')
    if ($Offline) {
        $config = Join-Path $buildRoot 'NuGet.offline.config'
        [IO.File]::WriteAllText($config, '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>')
        $restoreArgs += @('--configfile',$config)
    }
    if ($Task -ne 'Package') {
        Invoke-Dotnet (@('restore','DesktopAgent.sln') + $restoreArgs)
        Invoke-Dotnet @('build','DesktopAgent.sln','-c','Release','--no-restore')
        if ($Task -eq 'Test') { Invoke-Dotnet @('test','tests/DesktopAgent.Core.Tests/DesktopAgent.Core.Tests.csproj','-c','Release','--no-restore','--no-build','--logger','trx;LogFileName=core.trx','--results-directory',(Join-Path $buildRoot 'tests')) }
        return
    }
    $version = '0.2.0-preview.1'
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
    $name = "Prism-$version-win-x64-$stamp"
    $releases = Join-Path $root 'releases'
    $package = Join-Path $releases $name
    if (Test-Path -LiteralPath $package) { throw 'Package already exists.' }
    [void][IO.Directory]::CreateDirectory($releases)
    # Publish from a clean production snapshot so runtime restore never rewrites the source lock files.
    $snapshot = Join-Path $buildRoot ('package-source-' + $stamp)
    [void][IO.Directory]::CreateDirectory($snapshot)
    foreach ($configName in @('global.json','Directory.Build.props')) { Copy-Item -LiteralPath (Join-Path $root $configName) -Destination (Join-Path $snapshot $configName) }
    foreach ($projectName in @('DesktopAgent.Core','DesktopAgent.Windows')) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root ('src/' + $projectName)) -Recurse -File) {
            $relative = $file.FullName.Substring($root.Length + 1)
            if ($relative -match '(^|\\)(bin|obj)(\\|$)' -or $file.Extension -notin @('.cs','.csproj','.xaml','.ico','.manifest') -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
            $target = Join-Path $snapshot $relative
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target)); Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    $project = Join-Path $snapshot 'src/DesktopAgent.Windows/DesktopAgent.Windows.csproj'
    $properties = @('-p:RuntimeFrameworkVersion=10.0.11','-p:SelfContained=true','-p:RestorePackagesWithLockFile=false','-p:RestoreLockedMode=false','-p:NuGetAudit=false')
    Invoke-Dotnet (@('restore',$project,'-r','win-x64') + $restoreArgs + $properties)
    Invoke-Dotnet (@('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','--no-restore','--output',$package,'--disable-build-servers') + $properties)
    foreach ($required in @('DesktopAgent.exe','DesktopAgent.dll','DesktopAgent.Core.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','DesktopAgent.runtimeconfig.json')) { if (-not (Test-Path -LiteralPath (Join-Path $package $required))) { throw "Missing package file: $required" } }
    $options = (Get-Content -LiteralPath (Join-Path $package 'DesktopAgent.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
    if ($options.framework -or $options.frameworks) { throw 'Shared runtime dependency found.' }
    [IO.File]::WriteAllText((Join-Path $package 'Prism.cmd'), "@echo off`r`nstart `"`" `"%~dp0DesktopAgent.exe`"`r`n", [Text.Encoding]::ASCII)
    Copy-Item -LiteralPath 'docs/PRIVACY.md' -Destination (Join-Path $package 'PRIVACY.md')
    Copy-Item -LiteralPath 'docs/RELEASE.md' -Destination (Join-Path $package 'RELEASE-NOTES.md')
    Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $package 'LICENSE')
    $assetFile = Join-Path (Split-Path -Parent $project) 'obj/project.assets.json'
    $packageFolders = (Get-Content -LiteralPath $assetFile -Raw | ConvertFrom-Json).packageFolders.PSObject.Properties.Name
    $notices = Join-Path $package 'third-party'
    [void][IO.Directory]::CreateDirectory($notices)
    foreach ($notice in @(
        @('microsoft.netcore.app.runtime.win-x64','LICENSE.TXT','dotnet-runtime-LICENSE.txt'),
        @('microsoft.netcore.app.runtime.win-x64','THIRD-PARTY-NOTICES.TXT','dotnet-runtime-NOTICES.txt'),
        @('microsoft.windowsdesktop.app.runtime.win-x64','LICENSE','windows-desktop-LICENSE.txt')
    )) {
        $noticeSource = @($packageFolders | ForEach-Object { Join-Path $_ ($notice[0] + '/10.0.11/' + $notice[1]) } | Where-Object {Test-Path -LiteralPath $_}) | Select-Object -First 1
        if (-not $noticeSource) { throw ('Missing runtime redistribution notice: ' + $notice[0] + '/' + $notice[1]) }
        Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $notices $notice[2])
    }
    [IO.File]::WriteAllText((Join-Path $package 'START-HERE.txt'), "Prism / 棱镜 $version`r`n`r`n双击 Prism.cmd 启动。首次使用在设置中填入自己的模型密钥，并验证辅助定位。`r`n保留整个目录，不能只复制 exe。`r`nCtrl+Alt+F8 暂停，Ctrl+Alt+F9 停止。主窗口关闭后进入托盘，右键托盘可退出。`r`n本包不含密钥或个人数据。此为预览版，功能边界见 RELEASE-NOTES.md。`r`n", [Text.UTF8Encoding]::new($false))
    $commit = if (Get-Command git -ErrorAction SilentlyContinue) { (& git rev-parse HEAD 2>$null) -join '' } else { 'unavailable' }
    $dirty = if (Get-Command git -ErrorAction SilentlyContinue) { -not [string]::IsNullOrWhiteSpace((& git status --porcelain 2>$null) -join '') } else { $null }
    @{version=$version;sourceCommit=$commit;sourceWorkingTreeDirty=$dirty;selfContained=$true;runtime='10.0.11';builtAtUtc=[DateTime]::UtcNow.ToString('o');releaseStatus='PREVIEW';modelTasksTestedByPackaging=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'build.json') -Encoding utf8
    $files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object { @{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
    $files | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding utf8
    $zip = Join-Path $releases ($name + '.zip')
    Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
    [IO.File]::WriteAllText($zip + '.sha256', (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) + [Environment]::NewLine, [Text.Encoding]::ASCII)
    @{package=$package;zip=$zip;snapshot=$snapshot;sha256=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant();sourceCommit=$commit} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildRoot 'last-package.json') -Encoding utf8
    Write-Output "Package: $package"
    Write-Output "Release asset: $zip"
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
}
