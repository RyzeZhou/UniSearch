<#
.SYNOPSIS
    把 UniSearch 发布到 dist\，并在桌面创建/刷新快捷方式。

.DESCRIPTION
    为什么快捷方式指向 dist\ 而不是 src\UniSearch.Host\bin\Debug\：
      1. bin\Debug 是构建产物目录，每次构建都会变；快捷方式指向它等于指向一个随时会变的目标。
      2. 程序运行时锁住 bin\Debug\*.dll，想重新构建就得先 taskkill（本项目踩过 MSB3021）。
    dist\ 是"给人用的那一份"，改完代码重跑本脚本即可更新（快捷方式不用重建，路径没变）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install-desktop-shortcut.ps1
    powershell -ExecutionPolicy Bypass -File tools\install-desktop-shortcut.ps1 -NoShortcut   # 只发布
#>
param(
    # 只发布，不动快捷方式。
    [switch]$NoShortcut,
    # 自包含发布（MVP A1）：-r win-x64 --self-contained，不需要目标机器装 .NET 运行时。
    # 默认关：框架依赖包体小、发布快，适合日常迭代；给别人用的包再开这个。
    [switch]$SelfContained,
    # 桌面路径。默认问系统（能正确处理 OneDrive 重定向的桌面）。
    [string]$Desktop = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'UniSearch.exe'
$project = Join-Path $root 'src\UniSearch.Host\UniSearch.Host.csproj'

# 运行中的实例会锁住 dist\*.dll，发布时复制不过去（MSB3021/MSB3027）
$running = Get-Process -Name UniSearch -ErrorAction SilentlyContinue
if ($running) {
    foreach ($p in $running) {
        Write-Host "停止运行中的 UniSearch (PID $($p.Id)) —— 它锁着 dist\ 里的 dll"
        $p.Kill()
        $p.WaitForExit(5000) | Out-Null
    }
}

Write-Host "发布到 $dist ..."
if ($SelfContained) {
    & dotnet publish $project -c Release -o $dist -r win-x64 --self-contained --nologo
} else {
    & dotnet publish $project -c Release -o $dist --nologo
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }
if (-not (Test-Path $exe)) { throw "发布完成但找不到 $exe" }

$size = [math]::Round((Get-Item $exe).Length / 1KB)
Write-Host "已生成 $exe（$size KB，$(Get-Date -Format 'yyyy-MM-dd HH:mm')）"

if ($NoShortcut) { Write-Host '按要求跳过快捷方式（-NoShortcut）'; return }

if (-not (Test-Path $Desktop)) { throw "桌面目录不存在：$Desktop" }

$lnkPath = Join-Path $Desktop 'UniSearch.lnk'
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dist
$lnk.IconLocation = "$exe,0"
$lnk.Description = 'UniSearch - 全局搜索（Everything 索引）'
$lnk.Save()

# 读回来核对：CreateShortcut 保存失败是静默的，不验证等于没做
$check = $ws.CreateShortcut($lnkPath)
if ($check.TargetPath -ne $exe) { throw "快捷方式保存后读回的目标不对：$($check.TargetPath)" }
if (-not (Test-Path $check.TargetPath)) { throw "快捷方式指向的文件不存在：$($check.TargetPath)" }

Write-Host "桌面快捷方式已就绪：$lnkPath"
Write-Host "  -> $($check.TargetPath)"
Write-Host '启动后按 Alt+Win+Space 唤出（或 Ctrl+F 限定到资源管理器当前目录）。'
