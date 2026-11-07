#Requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeRoot = Join-Path $projectRoot 'install'
$frontendPath = Join-Path $runtimeRoot 'MFAAvalonia.exe'
$pythonPath = Join-Path $projectRoot '.venv312/Scripts/python.exe'
$sourceInterfacePath = Join-Path $projectRoot 'assets/interface.json'
$runtimeInterfacePath = Join-Path $runtimeRoot 'interface.json'
$nativePath = Join-Path $runtimeRoot 'runtimes/win-x64/native'
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {
    $nativePath = Join-Path $runtimeRoot 'runtimes/win-arm64/native'
}

foreach ($requiredPath in @($frontendPath, $pythonPath, $sourceInterfacePath, (Join-Path $nativePath 'MaaFramework.dll'))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "缺少开发运行依赖：$requiredPath。请先准备 install/ 前端及运行时和 .venv312/ Python 3.12 环境。"
    }
}

$interface = Get-Content -LiteralPath $sourceInterfacePath -Raw -Encoding utf8 |
    ConvertFrom-Json -AsHashtable -Depth 100
foreach ($resource in $interface.resource) {
    $resource.path = @($resource.path | ForEach-Object {
        [IO.Path]::GetFullPath((Join-Path (Split-Path $sourceInterfacePath -Parent) $_))
    })
}
$interface.agent.child_exec = $pythonPath
$interface.agent.child_args = @((Join-Path $projectRoot 'agent/main.py'))
$interface.name = "$($interface.name)（开发版）"
$interface.version = "$($interface.version)-dev"

# Close only this checkout's frontend gracefully, so its cached interface and
# Agent cannot keep running an older revision. Never force-kill a user process.
$runningFrontends = @(Get-Process -Name 'MFAAvalonia' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $frontendPath })
foreach ($frontend in $runningFrontends) {
    if (-not $frontend.CloseMainWindow() -or -not $frontend.WaitForExit(10000)) {
        throw '前端尚未退出。请停止任务并完整关闭助手后，再运行 tools/run_dev.ps1。未更新运行配置。'
    }
}

$backupRoot = Join-Path $runtimeRoot ('backup/dev-run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
if (Test-Path -LiteralPath $runtimeInterfacePath -PathType Leaf) {
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    Copy-Item -LiteralPath $runtimeInterfacePath -Destination (Join-Path $backupRoot 'interface.json')
}
& $pythonPath (Join-Path $projectRoot 'tools/migrate_runtime_options.py') `
    --instances (Join-Path $runtimeRoot 'config/instances') --backup (Join-Path $backupRoot 'instances')
if ($LASTEXITCODE -ne 0) {
    throw '实例配置迁移未完成，未启动开发版；原配置备份保留在 install/backup/。'
}
$interfaceJson = ($interface | ConvertTo-Json -Depth 100) + "`n"
[IO.File]::WriteAllText($runtimeInterfacePath, $interfaceJson, [Text.UTF8Encoding]::new($false))

# The source Agent must use the same MaaFramework native runtime as the GUI.
# Pass these settings only to the launched process and its children.
$frontendProcess = Start-Process -FilePath $frontendPath -WorkingDirectory $runtimeRoot -PassThru `
    -WindowStyle Normal -Environment @{ MAAFW_BINARY_PATH = $nativePath; PYTHONUTF8 = '1' }
Write-Output "已启动开发版，PID：$($frontendProcess.Id)"
Write-Output "资源：$(Join-Path $projectRoot 'assets/resource')"
Write-Output "Agent：$(Join-Path $projectRoot 'agent/main.py')"
Write-Output '未传入自动执行参数；请在界面中手动运行需要验证的任务。'
