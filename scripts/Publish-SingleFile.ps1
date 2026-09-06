#requires -Version 7.0
[CmdletBinding()]
param([string]$DotnetPath='dotnet', [string]$PackageCache, [switch]$Offline)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
& "$PSScriptRoot/Build.ps1" -Task Package -DotnetPath $DotnetPath -PackageCache $PackageCache -Offline:$Offline
$package=Get-Content "$root/.build/last-package.json" -Raw | ConvertFrom-Json
$project=Join-Path $package.snapshot 'src/DesktopAgent.Windows/DesktopAgent.Windows.csproj'
$content=Join-Path (Split-Path $project) 'distribution'
[void][IO.Directory]::CreateDirectory($content)
foreach($name in @('LICENSE','PRIVACY.md','RELEASE-NOTES.md','build.json','third-party')) {
    Copy-Item -LiteralPath (Join-Path $package.package $name) -Destination $content -Recurse
}
# Notices travel inside the standalone binary and are extracted beside its bundled runtime.
$xml=Get-Content $project -Raw
$xml=$xml.Replace('</Project>', '<ItemGroup><Content Include="distribution/**/*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="Always" /></ItemGroup></Project>')
[IO.File]::WriteAllText($project,$xml)
$output=Join-Path $package.snapshot 'single-file-output'
$oldCache=$env:NUGET_PACKAGES
$oldHome=$env:DOTNET_CLI_HOME
try {
    if($PackageCache){$env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackageCache)}
    $env:DOTNET_CLI_HOME=Join-Path $root '.build/dotnet-home'
    & $DotnetPath publish $project -c Release -r win-x64 --self-contained true --no-restore --output $output --disable-build-servers `
        -p:RuntimeFrameworkVersion=10.0.11 -p:RestorePackagesWithLockFile=false -p:RestoreLockedMode=false `
        -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if($LASTEXITCODE -ne 0){throw "Single-file publish failed: $LASTEXITCODE"}
    $files=@(Get-ChildItem $output -Recurse -File)
    if($files.Count -ne 1 -or $files[0].Name -ne 'DesktopAgent.exe'){throw 'Publish output is not a standalone EXE.'}
    $assets=Join-Path $root ('releases/github-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    if(Test-Path $assets){throw 'Asset directory already exists.'}
    [void][IO.Directory]::CreateDirectory($assets)
    $exe=Join-Path $assets 'Prism-0.2.0-preview.1-win-x64.exe'
    $zip=Join-Path $assets 'Prism-0.2.0-preview.1-win-x64.zip'
    Copy-Item $files[0].FullName $exe
    Copy-Item $package.zip $zip
    Copy-Item "$root/docs/RELEASE.md" "$assets/RELEASE-NOTES.md"
    $hashes=@($exe,$zip) | ForEach-Object { (Get-FileHash $_).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_) }
    [IO.File]::WriteAllLines("$assets/SHA256SUMS.txt",$hashes,[Text.Encoding]::ASCII)
    @{exe=$exe;zip=$zip;assets=$assets;sourceCommit=$package.sourceCommit;package=$package.package;snapshot=$package.snapshot} |
        ConvertTo-Json | Set-Content "$root/.build/last-release.json" -Encoding utf8
    Write-Output "Release assets: $assets"
} finally { $env:NUGET_PACKAGES=$oldCache; $env:DOTNET_CLI_HOME=$oldHome }
