# 构建 PCL_Unlock
#   .\build.ps1                      只构建，输出到 .\dist
#   .\build.ps1 -Deploy              构建并部署到 PCL2 目录下的 RegionUnlock\
#   .\build.ps1 -Deploy -DeployPath D:\game\PCL2\RegionUnlock

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Deploy,
    [string]$DeployPath = "D:\game\PCL2"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$launcherProject = Join-Path $root "src\PCLRegionUnlock\PCL_Unlock.csproj"
$hookProject = Join-Path $root "src\PclRegionHook\PclRegionHook.csproj"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

Write-Host "==> 清理 dist" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $dist "Hook") | Out-Null

Write-Host "==> 编译 startup hook" -ForegroundColor Cyan
dotnet build $hookProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "hook 编译失败，退出码 $LASTEXITCODE" }

$hookOut = Join-Path $root "src\PclRegionHook\bin\$Configuration\net10.0-windows"
foreach ($file in @("PclRegionHook.dll", "0Harmony.dll")) {
    if (-not (Test-Path (Join-Path $hookOut $file))) { throw "缺少产物：$(Join-Path $hookOut $file)" }
}

Write-Host "==> 发布启动器（内嵌 hook，单文件）" -ForegroundColor Cyan
dotnet publish $launcherProject -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o $dist -p:PclHookDir=$hookOut
if ($LASTEXITCODE -ne 0) { throw "启动器发布失败，退出码 $LASTEXITCODE" }
Get-ChildItem $dist -Filter *.pdb | Remove-Item -Force

# 单 exe 已内嵌 hook；这里再放一份外部副本，方便替换调试。
foreach ($file in @("PclRegionHook.dll", "0Harmony.dll")) {
    Copy-Item (Join-Path $hookOut $file) (Join-Path $dist "Hook") -Force
}

Copy-Item (Join-Path $root "README.md") $dist -Force

Write-Host "==> 构建完成：$dist" -ForegroundColor Green
Get-ChildItem -Recurse $dist -File | ForEach-Object {
    "    {0,-40} {1,8:N0} B" -f $_.FullName.Substring($dist.Length + 1), $_.Length
}

if (-not $Deploy) {
    Write-Host "提示：加 -Deploy 可同时部署到 $DeployPath" -ForegroundColor DarkGray
    return
}

Write-Host "==> 部署到 $DeployPath" -ForegroundColor Cyan
if (-not (Test-Path $DeployPath)) { New-Item -ItemType Directory -Force -Path $DeployPath | Out-Null }

$running = Get-Process -Name "PCL2_CE_Release_x64", "PCL2_CE_Release_x64_2", "PCL2_CE_Release_ARM64" -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning "PCL 正在运行，已加载的 hook DLL 可能被占用，先关掉 PCL 再部署更稳妥。"
}

# 单 exe 已内嵌 hook，部署只需要拷贝这一个文件到 PCL 目录。
Copy-Item (Join-Path $dist "PCL_Unlock.exe") $DeployPath -Force
Write-Host "==> 部署完成：$(Join-Path $DeployPath 'PCL_Unlock.exe')" -ForegroundColor Green

