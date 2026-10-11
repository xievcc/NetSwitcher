#requires -version 2.0
<#
    编译 NetSwitcher（C# WinForms，单文件 EXE）。
    用法：  powershell -ExecutionPolicy Bypass -File .\build.ps1
    输出：  .\dist\NetSwitcher.exe   （已内嵌 UAC 管理员清单，双击即提权）
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw '找不到 C# 编译器 csc.exe，请确认系统已安装 .NET Framework 4.x。'
}

$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' |
    Sort-Object Name | ForEach-Object { $_.FullName }
if (-not $sources) { throw 'src 目录下没有找到 .cs 源文件。' }
$srcQuoted = $sources | ForEach-Object { '"' + $_ + '"' }

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$exe = Join-Path $OutputDirectory 'NetSwitcher.exe'
$stageDirectory = Join-Path $root 'build'
if (-not (Test-Path -LiteralPath $stageDirectory)) {
    New-Item -ItemType Directory -Path $stageDirectory | Out-Null
}
# 先编到临时文件，再替换最终产物：exe 正被占用时也能编译成功，不会白编一场
$staged = Join-Path $stageDirectory 'NetSwitcher.compiled.exe'

$arguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/utf8output'
    '/win32manifest:"' + (Join-Path $root 'app.manifest') + '"'
    '/out:"' + $staged + '"'
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Web.Extensions.dll'
) + ($sources | ForEach-Object { '"' + $_ + '"' })

Write-Host "编译中 -> $staged"
& $csc $arguments
if ($LASTEXITCODE -ne 0) { throw "编译失败，csc 退出码 $LASTEXITCODE" }

# 替换最终产物；若被占用（程序正在运行/被杀软或系统占用句柄）则退到备用文件名
$final = $exe
try {
    Copy-Item -LiteralPath $staged -Destination $exe -Force -ErrorAction Stop
}
catch {
    $final = Join-Path $OutputDirectory 'NetSwitcher_new.exe'
    Copy-Item -LiteralPath $staged -Destination $final -Force -ErrorAction Stop
    Write-Warning ("{0} 正被占用，无法覆盖（通常是 NetSwitcher 还在运行）。" -f $exe)
    Write-Warning ("本次已输出到：{0} —— 请退出正在运行的程序，再把它改名替换。" -f $final)
}

$info = Get-Item -LiteralPath $final
Write-Host ("编译成功：{0}  ({1:N0} 字节)" -f $info.FullName, $info.Length)

# 顺手编译两个自检程序（probe 只读；ui-smoke 会读写配置文件，不碰网络）
$testDir = Join-Path $root 'tests'
if (Test-Path -LiteralPath $testDir) {
    $common = @(
        '/nologo', '/target:exe', '/utf8output',
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        '/reference:System.Web.Extensions.dll'
    )
    $probe = Join-Path $OutputDirectory 'probe.exe'
    & $csc ($common + @(
        ('/out:"' + $probe + '"'),
        ('"' + (Join-Path $root 'src\NetworkManager.cs') + '"'),
        ('"' + (Join-Path $root 'src\ProfileStore.cs') + '"'),
        ('"' + (Join-Path $testDir 'Probe.cs') + '"')
    ))
    if ($LASTEXITCODE -eq 0) { Write-Host "自检程序：$probe" } else { Write-Warning 'probe.exe 编译失败（不影响主程序）' }

    $smoke = Join-Path $OutputDirectory 'ui-smoke.exe'
    & $csc ($common + @(
        '/main:UiSmoke',
        ('/out:"' + $smoke + '"'),
        ('"' + (Join-Path $root 'src\NetworkManager.cs') + '"'),
        ('"' + (Join-Path $root 'src\ProfileStore.cs') + '"'),
        ('"' + (Join-Path $root 'src\SettingsStore.cs') + '"'),
        ('"' + (Join-Path $root 'src\CommandLine.cs') + '"'),
        ('"' + (Join-Path $root 'src\NetworkApplier.cs') + '"'),
        ('"' + (Join-Path $root 'src\SwitcherForm.cs') + '"'),
        ('"' + (Join-Path $root 'src\Program.cs') + '"'),
        ('"' + (Join-Path $root 'src\MainForm.cs') + '"'),
        ('"' + (Join-Path $testDir 'UiSmoke.cs') + '"')
    ))
    if ($LASTEXITCODE -eq 0) { Write-Host "自检程序：$smoke" } else { Write-Warning 'ui-smoke.exe 编译失败（不影响主程序）' }
    # 真实双进程的单实例通信验证（沙箱外运行；能验证命名管道链路）
    $ipcLive = Join-Path $OutputDirectory 'ipc-live.exe'
    $ipcRun = Join-Path $OutputDirectory 'ipc-run.exe'
    & $csc ($common + @('/main:IpcLive', ('/out:"' + $ipcLive + '"')) +
        $srcQuoted + ('"' + (Join-Path $testDir 'IpcLive.cs') + '"'))
    if ($LASTEXITCODE -eq 0) { Write-Host "自检程序：$ipcLive" } else { Write-Warning 'ipc-live.exe 编译失败（不影响主程序）' }

    & $csc ($common + @('/main:IpcLiveRunner', ('/out:"' + $ipcRun + '"'),
        ('"' + (Join-Path $testDir 'IpcLiveRunner.cs') + '"')))
    if ($LASTEXITCODE -eq 0) { Write-Host "自检程序：$ipcRun（双进程通信验证）" } else { Write-Warning 'ipc-run.exe 编译失败（不影响主程序）' }
}
