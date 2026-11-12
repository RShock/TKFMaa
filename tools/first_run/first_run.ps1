[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][int]$CurrentProcessId
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$marker = Join-Path $InstallRoot '.tkfm-first-run-migration.json'
$script:busy = $false
$script:completed = $false
$script:candidates = @()
$script:candidateSignature = $null
$launchingProcess = Get-Process -Id $CurrentProcessId -ErrorAction Stop
$launchingStarted = $launchingProcess.StartTime.ToUniversalTime().ToFileTimeUtc()

function Save-State([string]$State) {
    $data = @{ version = 1; status = $State; at = [DateTimeOffset]::Now.ToString('o') } | ConvertTo-Json
    [IO.File]::WriteAllText($marker, $data, [Text.UTF8Encoding]::new($false))
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'TKFMaaR — 导入旧版配置'
$form.ClientSize = New-Object System.Drawing.Size(760, 395)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.AutoScaleMode = 'Dpi'

$intro = New-Object System.Windows.Forms.Label
$intro.SetBounds(20, 18, 720, 91)
$intro.Text = "如需继承旧版设置，请同时打开旧助手，保存设置并停止任务。本窗口会自动检测。`r`n将导入全部配置、实例与定时设置，以及自定义关卡的两张截图和附属图片。`r`n只有一个旧助手时无需选择来源，点击导入即可。旧目录保留，新版现有资料会先备份。"
$form.Controls.Add($intro)

$list = New-Object System.Windows.Forms.ListBox
$list.SetBounds(20, 113, 720, 100)
$list.HorizontalScrollbar = $true
$list.Visible = $false
$form.Controls.Add($list)

$sourceStatus = New-Object System.Windows.Forms.Label
$sourceStatus.SetBounds(20, 113, 720, 100)
$sourceStatus.TextAlign = 'MiddleLeft'
$sourceStatus.AutoEllipsis = $true
$sourceStatus.Text = '正在查找旧助手……'
$form.Controls.Add($sourceStatus)

$detail = New-Object System.Windows.Forms.Label
$detail.SetBounds(20, 222, 720, 49)
$detail.Text = '正在查找旧助手……'
$form.Controls.Add($detail)

$note = New-Object System.Windows.Forms.Label
$note.SetBounds(20, 277, 720, 44)
$note.Text = '本提示只显示一次。取消、按 Esc 或关闭窗口后，将继续启动新版。迁移后请检查任务和定时设置；未保存到磁盘的修改无法保证继承。'
$form.Controls.Add($note)

$refresh = New-Object System.Windows.Forms.Button
$refresh.Text = '重新检测'
$refresh.SetBounds(20, 337, 110, 35)
$form.Controls.Add($refresh)
$import = New-Object System.Windows.Forms.Button
$import.Text = '导入并结束旧助手'
$import.SetBounds(397, 337, 185, 35)
$import.Enabled = $false
$form.Controls.Add($import)
$cancel = New-Object System.Windows.Forms.Button
$cancel.Text = '取消迁移'
$cancel.SetBounds(597, 337, 143, 35)
$cancel.DialogResult = 'Cancel'
$form.Controls.Add($cancel)
$form.CancelButton = $cancel
$detectionTimer = New-Object System.Windows.Forms.Timer
$detectionTimer.Interval = 1500
$detectionTimer.add_Tick({ Refresh-OldProcesses })
$parentTimer = New-Object System.Windows.Forms.Timer
$parentTimer.Interval = 500
$parentTimer.add_Tick({
    if ($script:busy) { return }
    $parentProcess = Get-Process -Id $CurrentProcessId -ErrorAction SilentlyContinue
    if (-not $parentProcess -or $parentProcess.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $launchingStarted) {
        $form.Close()
    }
})
$parentTimer.Start()

function Refresh-OldProcesses {
    if ($script:busy) { return }
    $refresh.Enabled = $false
    $found = @()
    try {
        foreach ($candidate in @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Id -ne $CurrentProcessId -and $_.ProcessName -match '^(MFAAvalonia|TKFMaaR?|TKFMaaR?-[\w.-]+)$'
        })) {
            try {
                $executable = $candidate.Path
                if (-not $executable) { continue }
                $folder = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($executable)).TrimEnd('\')
                if ($folder.Equals($InstallRoot, [StringComparison]::OrdinalIgnoreCase) -or
                    $folder.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                    $InstallRoot.StartsWith($folder + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $interfacePath = Join-Path $folder 'interface.json'
                if (-not (Test-Path -LiteralPath $interfacePath -PathType Leaf)) { continue }
                $metadata = Get-Content -LiteralPath $interfacePath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($metadata.name -notmatch '^TKFMaaR?(（开发版）)?$') { continue }
                if (-not (Test-Path -LiteralPath (Join-Path $folder 'config/instances') -PathType Container)) { continue }
                $found += [PSCustomObject]@{
                    Id = $candidate.Id; Executable = $executable; Folder = $folder
                    Started = $candidate.StartTime.ToUniversalTime().ToFileTimeUtc().ToString()
                    Display = "$($metadata.name)  $($metadata.version)  [PID $($candidate.Id)]  $folder"
                }
            }
            catch { continue }
        }
        $found = @($found | Sort-Object Id)
        $signature = ($found | ForEach-Object { "$($_.Id)|$($_.Started)|$($_.Display)" }) -join "`n"
        # Keep a manual selection (and any import error) while candidates are unchanged.
        if ($signature -ceq $script:candidateSignature) { return }
        $previous = if ($list.SelectedIndex -ge 0) { $script:candidates[$list.SelectedIndex] } else { $null }
        $list.Items.Clear()
        $script:candidates = $found
        $script:candidateSignature = $signature
        foreach ($item in $found) { [void]$list.Items.Add($item.Display) }
        $list.Visible = $found.Count -gt 1
        $sourceStatus.Visible = $found.Count -le 1
        if ($found.Count -eq 0) {
            $sourceStatus.Text = '尚未检测到旧助手。打开旧版后，这里会自动显示来源。'
            $detail.Text = '请保存旧版设置并停止任务；如旧版以管理员身份运行，需以相同权限打开新版。'
        }
        elseif ($found.Count -eq 1) {
            $sourceStatus.Text = "已自动识别旧助手，无需选择来源：`r`n$($found[0].Display)"
            $list.SelectedIndex = 0
        }
        else {
            $detail.Text = '检测到多个旧助手，请选择要导入的一个。'
            for ($index = 0; $index -lt $found.Count; $index++) {
                if ($previous -and $found[$index].Id -eq $previous.Id -and $found[$index].Started -eq $previous.Started) {
                    $list.SelectedIndex = $index
                    break
                }
            }
        }
        $import.Enabled = $list.SelectedIndex -ge 0
    }
    finally { $refresh.Enabled = $true }
}

$list.add_SelectedIndexChanged({
    $import.Enabled = -not $script:busy -and $list.SelectedIndex -ge 0
    if ($list.SelectedIndex -ge 0 -and -not $script:busy) {
        $detail.Text = '点击「导入并结束旧助手」即可继承配置。只会结束此旧助手，模拟器继续运行。'
    }
})
$refresh.add_Click({ Refresh-OldProcesses })
$import.add_Click({
    if ($script:busy -or $list.SelectedIndex -lt 0) { return }
    $selected = $script:candidates[$list.SelectedIndex]
    $script:busy = $true
    $detectionTimer.Stop()
    $refresh.Enabled = $false
    $import.Enabled = $false
    $cancel.Enabled = $false
    $list.Enabled = $false
    $resultPath = Join-Path $InstallRoot ('temp/config-import-result-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        $python = Join-Path $InstallRoot 'python/python.exe'
        if ($env:TKFM_FIRST_RUN_PYTHON) { $python = $env:TKFM_FIRST_RUN_PYTHON }
        if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw '缺少发布包中的 Python 环境，无法迁移。' }
        [void][IO.Directory]::CreateDirectory((Split-Path $resultPath -Parent))
        $detail.Text = '正在暂存并检查配置和图片；检查完成后结束旧助手，再备份并导入。请等待……'
        [System.Windows.Forms.Application]::DoEvents()
        # Windows paths cannot contain quotes; arguments are quoted separately, never evaluated as shell code.
        $arguments = @(
            (Join-Path $PSScriptRoot 'import_config.py'), '--source-exe', $selected.Executable,
            '--process-id', [string]$selected.Id, '--started', $selected.Started,
            '--target', $InstallRoot, '--current-process-id', [string]$CurrentProcessId,
            '--result', $resultPath
        ) | ForEach-Object { '"' + $_.ToString().TrimEnd('\') + '"' }
        $worker = Start-Process -FilePath $python -ArgumentList $arguments -WindowStyle Hidden -PassThru
        try {
            while (-not $worker.WaitForExit(100)) { [System.Windows.Forms.Application]::DoEvents() }
            if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw '导入程序没有返回结果；旧配置目录仍保留。' }
            $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if (-not $result.ok) { throw $result.error }
        }
        finally { $worker.Dispose() }
        # The initial "shown" marker already suppresses subsequent prompts.
        try { Save-State 'imported' } catch { }
        $script:completed = $true
        [void][System.Windows.Forms.MessageBox]::Show($form,
            "已导入 $($result.config_files) 个配置文件、$($result.images) 张图片。`r`n新版原资料备份：$($result.backup)`r`n旧版目录保留。新版现在继续启动，请检查任务及定时设置。",
            '迁移完成', 'OK', 'Information')
        $form.Close()
    }
    catch {
        $detail.Text = '迁移未完成：' + $_.Exception.Message
        [void][System.Windows.Forms.MessageBox]::Show($form, $detail.Text, '迁移未完成', 'OK', 'Warning')
    }
    finally {
        $script:busy = $false
        $refresh.Enabled = $true
        $list.Enabled = $true
        $cancel.Enabled = $true
        $import.Enabled = $list.SelectedIndex -ge 0
        if (-not $script:completed) { $detectionTimer.Start() }
    }
})
$form.add_FormClosing({
    param($sender, $eventArgs)
    if ($script:busy -and -not $script:completed) { $eventArgs.Cancel = $true; return }
    $detectionTimer.Stop()
    if (-not $script:completed) { try { Save-State 'cancelled' } catch { } }
})
$form.add_Shown({ Save-State 'shown'; Refresh-OldProcesses; $detectionTimer.Start() })
[void]$form.ShowDialog()
$detectionTimer.Stop()
$detectionTimer.Dispose()
$parentTimer.Stop()
$parentTimer.Dispose()
$form.Dispose()
