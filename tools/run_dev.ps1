#Requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$ForceRestart
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeRoot = Join-Path $projectRoot 'install'
$frontendPath = Join-Path $runtimeRoot 'MFAAvalonia.exe'
$pythonPath = Join-Path $projectRoot '.venv312/Scripts/python.exe'
$sourceInterfacePath = Join-Path $projectRoot 'assets/interface.json'
$runtimeInterfacePath = Join-Path $runtimeRoot 'interface.json'
$globalConfigPath = Join-Path $runtimeRoot 'config/config.json'
$backupRoot = Join-Path $runtimeRoot ('backup/dev-run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
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

# Forced restart is explicit: back up persisted settings before interrupting the
# frontend. Match full paths and Agent script arguments, never process names alone.
if ($ForceRestart) {
    $forceBackupRoot = Join-Path $backupRoot 'before-force'
    New-Item -ItemType Directory -Path $forceBackupRoot -Force | Out-Null
    foreach ($stateFile in @($runtimeInterfacePath, (Join-Path $runtimeRoot 'appsettings.json'))) {
        if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
            Copy-Item -LiteralPath $stateFile -Destination $forceBackupRoot
        }
    }
    $configRoot = Join-Path $runtimeRoot 'config'
    if (Test-Path -LiteralPath $configRoot -PathType Container) {
        Copy-Item -LiteralPath $configRoot -Destination (Join-Path $forceBackupRoot 'config') -Recurse
    }
    Write-Output "强制重启前的配置已备份：$forceBackupRoot"

    $agentScriptPath = (Join-Path $projectRoot 'agent/main.py').Replace('\', '/')
    $agentArgumentPattern = '(?i)(?:^|[\s"])' + [regex]::Escape($agentScriptPath) + '(?=[\s"]|$)'
    $processSnapshot = @(Get-CimInstance Win32_Process -Filter "Name = 'MFAAvalonia.exe' OR Name = 'python.exe' OR Name = 'pythonw.exe'")
    $forceTargets = @($processSnapshot | Where-Object { $_.ExecutablePath -ieq $frontendPath }) +
        @($processSnapshot | Where-Object {
            $_.Name -in @('python.exe', 'pythonw.exe') -and
            $_.CommandLine -and $_.CommandLine.Replace('\', '/') -match $agentArgumentPattern
        })
    foreach ($snapshot in $forceTargets) {
        $targetProcessId = [int]$snapshot.ProcessId
        $current = Get-CimInstance Win32_Process -Filter "ProcessId = $targetProcessId"
        if ($null -eq $current) { continue }
        if ($current.CreationDate -ne $snapshot.CreationDate -or
            $current.ExecutablePath -ine $snapshot.ExecutablePath -or
            $current.CommandLine -cne $snapshot.CommandLine) {
            throw "进程身份已变化，未结束 PID $targetProcessId；请重新运行启动脚本。"
        }
        $targetProcess = Get-Process -Id $targetProcessId -ErrorAction SilentlyContinue
        if ($null -eq $targetProcess) { continue }
        try {
            Stop-Process -InputObject $targetProcess -Force
        }
        catch {
            if (-not $targetProcess.HasExited) { throw }
        }
        if (-not $targetProcess.WaitForExit(10000)) {
            throw "强制结束后进程仍未退出（PID $targetProcessId），未启动第二个助手。"
        }
        Write-Output "已强制结束本工作区进程：$($snapshot.Name)，PID：$targetProcessId"
    }
}

# Without -ForceRestart, retain the normal graceful-close behavior.
$runningFrontends = @(Get-Process -Name 'MFAAvalonia' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $frontendPath })
foreach ($frontend in $runningFrontends) {
    Write-Output '正在请求旧助手退出；暂停中的任务仍被前端视为运行中，如出现退出确认，请在助手中确认。'
    if (-not $frontend.CloseMainWindow() -or -not $frontend.WaitForExit(30000)) {
        throw '旧助手未完成退出，可能正在等待退出确认。可停止任务并关闭助手，或在明确允许中断任务时使用 tools/run_dev.ps1 -ForceRestart。未更新运行配置。'
    }
}

if (Test-Path -LiteralPath $runtimeInterfacePath -PathType Leaf) {
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Copy-Item -LiteralPath $runtimeInterfacePath -Destination (Join-Path $backupRoot 'interface.json')
}
if (Test-Path -LiteralPath $globalConfigPath -PathType Leaf) {
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Copy-Item -LiteralPath $globalConfigPath -Destination (Join-Path $backupRoot 'config.json')
}
& $pythonPath (Join-Path $projectRoot 'tools/migrate_runtime_options.py') `
    --instances (Join-Path $runtimeRoot 'config/instances') --backup (Join-Path $backupRoot 'instances')
if ($LASTEXITCODE -ne 0) {
    throw '实例配置迁移未完成，未启动开发版；原配置备份保留在 install/backup/。'
}
# Persist before launch: custom extensions load after the frontend's startup
# settings. Restore fork checks before launch and preserve later user choices.
$globalConfig = if (Test-Path -LiteralPath $globalConfigPath -PathType Leaf) {
    Get-Content -LiteralPath $globalConfigPath -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 100
} else { @{} }
if ($globalConfig['TkfmForkUpdateCheckV1'] -ne $true) {
    $globalConfig['EnableCheckVersion'] = $true
    $globalConfig['TkfmForkUpdateCheckV1'] = $true
}
$globalConfig['DownloadSourceIndex'] = 0
foreach ($setting in @('EnableAutoUpdateResource', 'EnableAutoUpdateMFA')) {
    $globalConfig[$setting] = $false
}
New-Item -ItemType Directory -Path (Split-Path $globalConfigPath -Parent) -Force | Out-Null
[IO.File]::WriteAllText($globalConfigPath, ($globalConfig | ConvertTo-Json -Depth 100) + "`n",
    [Text.UTF8Encoding]::new($false))
$interfaceJson = ($interface | ConvertTo-Json -Depth 100) + "`n"
[IO.File]::WriteAllText($runtimeInterfacePath, $interfaceJson, [Text.UTF8Encoding]::new($false))

# The source Agent must use the same MaaFramework native runtime as the GUI.
# Pass these settings only to the launched process and its children.
$frontendProcess = Start-Process -FilePath $frontendPath -WorkingDirectory $runtimeRoot -PassThru `
    -WindowStyle Normal -Environment @{ MAAFW_BINARY_PATH = $nativePath; PYTHONUTF8 = '1' }
Write-Output "已启动开发版，PID：$($frontendProcess.Id)"
Write-Output '版本检查使用 RShock/TKFMaaR，首次恢复为开启，后续保留用户选择；自动安装保持关闭。'
Write-Output '任务识别与实时预览共用一个截图控制器，不再另建预览连接。'
Write-Output "资源：$(Join-Path $projectRoot 'assets/resource')"
Write-Output "Agent：$(Join-Path $projectRoot 'agent/main.py')"
Write-Output '未传入自动执行参数；请在界面中手动运行需要验证的任务。'
