# 生成 MSIX 安装包（商店提交用的格式）
#
# 依赖：.NET 8 SDK；makeappx / signtool 从 Microsoft.Windows.SDK.BuildTools 这个 NuGet 包取，
#       第一次运行前先执行：dotnet restore ..\tools\SdkBoot\SdkBoot.csproj
#
# 用法：
#   .\build-msix.ps1                       # 只打包，不签名
#   .\build-msix.ps1 -Sign -Thumbprint XX  # 用本机证书库里的证书签名（自测用）

param(
    [string]$Version = "1.1.1",
    [switch]$Sign,
    [string]$Thumbprint = ""
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$packaging = $PSScriptRoot
$stage = Join-Path $packaging "stage"
$outDir = Join-Path $projectRoot "dist"
$msix = Join-Path $outDir "SoundBridge-$Version-x64.msix"

function Find-SdkTool([string]$name) {
    $pkg = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools"
    if (-not (Test-Path $pkg)) {
        throw "没找到 Windows SDK BuildTools，先运行: dotnet restore tools\SdkBoot\SdkBoot.csproj"
    }
    $tool = Get-ChildItem $pkg -Recurse -Filter "$name.exe" |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) { throw "找不到 $name.exe" }
    return $tool.FullName
}

Write-Host "[1/4] 发布程序到 stage 目录"
if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
dotnet publish $projectRoot -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
    -o $stage -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

Write-Host "[2/4] 放入清单和商店素材"
Copy-Item (Join-Path $packaging "Package.appxmanifest") (Join-Path $stage "AppxManifest.xml") -Force
$assets = Join-Path $stage "Assets"
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Copy-Item (Join-Path $packaging "Assets\*.png") $assets -Force

Write-Host "[3/4] 打包"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$makeappx = Find-SdkTool "makeappx"
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败" }

if ($Sign) {
    if (-not $Thumbprint) { throw "签名需要 -Thumbprint" }
    Write-Host "[4/4] 签名"
    $signtool = Find-SdkTool "signtool"
    & $signtool sign /sha1 $Thumbprint /fd SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "签名失败" }
} else {
    Write-Host "[4/4] 跳过签名（提交商店时由微软免费签名；本地自测见 README）"
}

$size = [math]::Round((Get-Item $msix).Length / 1MB, 1)
Write-Host "完成: $msix ($size MB)"
