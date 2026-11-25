<#
.SYNOPSIS
    ImageViewer 를 Self-contained 로 게시하고, NSIS 가 설치되어 있으면 설치 프로그램을 만든다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
    powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipTests -SkipInstaller

    Magick.NET 멀티코어(OpenMP) 빌드를 쓰므로 win-x64 만 지원한다.
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"
if ($Runtime -ne "win-x64") { throw "win-x64 만 지원합니다 (Magick.NET OpenMP x64 빌드 사용)." }
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\ImageViewer\ImageViewer.csproj"
$output = Join-Path $root "artifacts\publish\$Runtime"

if (-not $SkipTests) {
    Write-Host "== 테스트 실행" -ForegroundColor Cyan
    dotnet test (Join-Path $root "ImageViewer.sln") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "테스트 실패" }
}

Write-Host "== 게시: $Runtime -> $output" -ForegroundColor Cyan
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $output `
    -p:PublishReadyToRun=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "게시 실패" }

if ($SkipInstaller) { return }

if ($Runtime -ne "win-x64") {
    Write-Warning "설치 프로그램은 win-x64 게시본만 지원합니다. 설치 프로그램 생성을 건너뜁니다."
    return
}

$makensis = @(
    "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
    "$env:ProgramFiles\NSIS\makensis.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $makensis) {
    $makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
}

if (-not $makensis) {
    Write-Warning "NSIS (makensis.exe) 를 찾지 못해 설치 프로그램 생성을 건너뜁니다. https://nsis.sourceforge.io"
    return
}

[xml]$csproj = Get-Content $project -Encoding UTF8
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = "1.0.0" }

New-Item -ItemType Directory -Force (Join-Path $root "artifacts\installer") | Out-Null

$portableDir = Join-Path $root "artifacts\publish\portable-$Runtime"
$portableExe = Join-Path $root "artifacts\installer\ImageViewer-$version-portable.exe"
Write-Host "== 포터블 단일 실행 파일 생성 (v$version) -> $portableExe" -ForegroundColor Cyan
if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $portableDir `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "포터블 게시 실패" }
Copy-Item (Join-Path $portableDir "ImageViewer.exe") $portableExe -Force
Get-Item $portableExe

Write-Host "== 설치 프로그램 생성 (NSIS, v$version)" -ForegroundColor Cyan
& $makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$version" "/DPUBLISH_DIR=$output" (Join-Path $root "src\ImageViewer.Installer\ImageViewer.nsi")
if ($LASTEXITCODE -ne 0) { throw "설치 프로그램 생성 실패" }
$setupExe = Join-Path $root "artifacts\installer\ImageViewer-$version-setup.exe"
Get-Item $setupExe

$releaseAssets = @($setupExe, $portableExe)

if (-not $SkipMsi) {
    $wix = (Get-Command wix -ErrorAction SilentlyContinue).Source
    if (-not $wix) {
        $dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
        if (Test-Path $dotnetTools) {
            $wix = $dotnetTools
        }
    }
    if (-not $wix) {
        Write-Host "== WiX Toolset (.NET Global Tool) 설치" -ForegroundColor Cyan
        dotnet tool update --global wix --version 5.0.2
        $dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
        if (Test-Path $dotnetTools) {
            $wix = $dotnetTools
        }
    }

    if ($wix) {
        $msiFile = Join-Path $root "artifacts\installer\ImageViewer-$version.msi"
        $wxsFile = Join-Path $root "src\ImageViewer.Installer\ImageViewer.wxs"
        Write-Host "== MSI 설치 패키지 생성 (WiX, v$version) -> $msiFile" -ForegroundColor Cyan
        & $wix build $wxsFile -arch x64 -culture ko-KR -pdbtype none -d "Version=$version" -d "PublishDir=$output" -o $msiFile
        if ($LASTEXITCODE -ne 0) { throw "MSI 설치 패키지 생성 실패" }
        Get-Item $msiFile
        $releaseAssets += $msiFile
    } else {
        Write-Warning "WiX (wix.exe) 를 찾지 못해 MSI 생성을 건너뜁니다. (dotnet tool install --global wix)"
    }
}

# 자동 업데이트용 확인값: GitHub 릴리스에 설치 파일 · 포터블 exe · MSI 와 함께 SHA256SUMS.txt 와 그 서명을 올린다.
# 앱은 SHA256SUMS.txt.sig 를 앱에 들어 있는 공개키로 확인한 뒤에만 확인값을 믿는다 (서명이 없으면 자동 업데이트하지 않는다).
$sumsFile = Join-Path $root "artifacts\installer\SHA256SUMS.txt"
$sigFile = "$sumsFile.sig"
$lines = foreach ($file in $releaseAssets) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $file -Leaf)"
}
[IO.File]::WriteAllText($sumsFile, ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
Remove-Item $sigFile -ErrorAction SilentlyContinue

# 직접 배포(README 방법 3)할 때: 환경 변수 IMAGEVIEWER_UPDATE_SIGNING_KEY 에 서명 키(PEM) 경로를 넣으면 서명한다.
# (GitHub Actions 에서는 릴리스 단계가 따로 서명하므로 여기서는 하지 않는다)
$signingKey = $env:IMAGEVIEWER_UPDATE_SIGNING_KEY
if ($signingKey) {
    $openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
    if (-not $openssl) {
        $gitOpenssl = Join-Path $env:ProgramFiles "Git\usr\bin\openssl.exe"
        if (Test-Path $gitOpenssl) { $openssl = $gitOpenssl }
    }
    if (-not $openssl) { throw "openssl 을 찾지 못해 서명할 수 없습니다. (Git for Windows 에 들어 있습니다)" }

    $sigDer = "$sumsFile.der"
    & $openssl dgst -sha256 -sign $signingKey -out $sigDer $sumsFile
    if ($LASTEXITCODE -ne 0) { throw "SHA256SUMS.txt 서명 실패" }

    # 앱에 들어 있는 공개키로 다시 확인한다 (키가 짝이 맞지 않으면 멈춘다).
    [xml]$csprojXml = Get-Content (Join-Path $root "src\ImageViewer\ImageViewer.csproj") -Encoding UTF8
    $publicKey = ($csprojXml.Project.ItemGroup.AssemblyMetadata | Where-Object { $_.Include -eq "UpdatePublicKey" }).Value
    $pubDer = "$sumsFile.pub.der"
    [IO.File]::WriteAllBytes($pubDer, [Convert]::FromBase64String($publicKey))
    & $openssl dgst -sha256 -verify $pubDer -keyform DER -signature $sigDer $sumsFile
    $verified = $LASTEXITCODE -eq 0
    [IO.File]::WriteAllText($sigFile, [Convert]::ToBase64String([IO.File]::ReadAllBytes($sigDer)), (New-Object Text.UTF8Encoding $false))
    Remove-Item $sigDer, $pubDer -ErrorAction SilentlyContinue
    if (-not $verified) {
        Remove-Item $sigFile -ErrorAction SilentlyContinue
        throw "서명 키가 앱의 공개키(ImageViewer.csproj 의 UpdatePublicKey)와 짝이 맞지 않습니다."
    }
    $releaseFiles = $releaseAssets + $sumsFile + $sigFile
} else {
    if (-not $env:GITHUB_ACTIONS) {
        Write-Warning "IMAGEVIEWER_UPDATE_SIGNING_KEY 가 없어 SHA256SUMS.txt 에 서명하지 않았습니다. 서명 없는 릴리스는 자동 업데이트되지 않습니다."
    }
    $releaseFiles = $releaseAssets + $sumsFile
}

Write-Host "== 릴리스에 올릴 파일 (태그 v$version)" -ForegroundColor Cyan
Get-Item $releaseFiles | Select-Object Name, Length | Format-Table -AutoSize
