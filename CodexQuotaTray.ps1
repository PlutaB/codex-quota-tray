param([switch]$Once)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$script:AppName = 'Codex Quota Tray'
$script:ScriptPath = $PSCommandPath
$script:StartupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:StartupValue = 'CodexQuotaTray'

function Get-QuotaLabel([int]$Minutes) {
    switch ($Minutes) {
        300 { '5-hour usage' }
        1440 { '1-day usage' }
        10080 { '7-day usage' }
        default {
            if ($Minutes % 1440 -eq 0) { "$($Minutes / 1440)-day usage" }
            elseif ($Minutes % 60 -eq 0) { "$($Minutes / 60)-hour usage" }
            else { "$Minutes-minute usage" }
        }
    }
}

function Get-RelativeTime($UnixTime) {
    if ($null -eq $UnixTime) { return '--' }
    try { $seconds = [math]::Round(([DateTimeOffset]::FromUnixTimeSeconds([int64]$UnixTime).LocalDateTime - (Get-Date)).TotalSeconds) }
    catch { return '--' }
    if ($seconds -le 0) { return 'now' }
    $days = [math]::Floor($seconds / 86400); $hours = [math]::Floor(($seconds % 86400) / 3600); $minutes = [math]::Floor(($seconds % 3600) / 60)
    if ($days -gt 0) { return "${days}d${hours}h" }
    if ($hours -gt 0) { return "${hours}h${minutes}m" }
    return "$( [math]::Max(1, $minutes) )m"
}

function Get-LatestQuota {
    $roots = @("$env:USERPROFILE\.codex\sessions", "$env:USERPROFILE\.codex\archived_sessions")
    $files = @($roots | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem -Path $_ -Filter '*.jsonl' -File -Recurse -ErrorAction SilentlyContinue }) |
        Sort-Object LastWriteTime -Descending | Select-Object -First 120
    if (-not $files) { return @{ Error = 'No Codex session logs found.' } }

    $best = $null
    foreach ($file in $files) {
        try {
            $stream = [System.IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
            try {
                $offset = [math]::Max(0, $stream.Length - 1MB); $stream.Seek($offset, [System.IO.SeekOrigin]::Begin) | Out-Null
                $reader = [System.IO.StreamReader]::new($stream); $text = $reader.ReadToEnd(); $reader.Dispose()
            } finally { $stream.Dispose() }
        } catch { continue }
        $lines = $text -split "`n"
        [array]::Reverse($lines)
        foreach ($line in $lines) {
            if ($line -notmatch '"token_count"' -or $line -notmatch '"rate_limits"') { continue }
            try { $record = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
            if ($record.payload.type -ne 'token_count' -or $null -eq $record.payload.rate_limits) { continue }
            $limits = $record.payload.rate_limits; $windows = @()
            foreach ($prop in $limits.psobject.Properties) {
                $value = $prop.Value
                if ($null -eq $value -or $null -eq $value.used_percent -or $null -eq $value.window_minutes) { continue }
                $minutes = [int]$value.window_minutes
                if ($minutes -le 0) { continue }
                $remaining = [math]::Max(0, [math]::Min(100, 100 - [double]$value.used_percent))
                $windows += [pscustomobject]@{ Key=$prop.Name; Minutes=$minutes; Label=(Get-QuotaLabel $minutes); Used=[double]$value.used_percent; Remaining=$remaining; ResetsAt=$value.resets_at }
            }
            $windows = @($windows | Sort-Object Minutes, Key)
            if ($windows.Count -gt 0 -or $limits.rate_limit_reached_type) {
                try { $observed = [DateTimeOffset]::Parse($record.timestamp).LocalDateTime } catch { $observed = [DateTime]::MinValue }
                $snapshot = [pscustomobject]@{ Windows=$windows; Plan=$limits.plan_type; Reached=$limits.rate_limit_reached_type; Source=$file.FullName; Observed=$observed }
                if ($null -eq $best -or $snapshot.Observed -gt $best.Observed) { $best = $snapshot }
                break
            }
        }
    }
    if ($best) { return $best }
    return @{ Error = 'No rate limit event found in recent Codex logs.' }
}

function New-TrayIcon($Snapshot) {
    $bitmap = [System.Drawing.Bitmap]::new(32, 32)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'; $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.FillEllipse([System.Drawing.Brushes]::Black, 2, 6, 9, 20)
    $windows = @($Snapshot.Windows | Select-Object -First 2)
    if ($windows.Count -eq 0) { $graphics.DrawString('--', [System.Drawing.Font]::new('Segoe UI', 8, 'Bold'), [System.Drawing.Brushes]::DimGray, 12, 9) }
    for ($i = 0; $i -lt $windows.Count; $i++) {
        $y = if ($windows.Count -eq 1) { 11 } else { 7 + 12 * $i }; $remaining = $windows[$i].Remaining
        $color = if ($remaining -ge 40) { [System.Drawing.Color]::FromArgb(45, 170, 90) } elseif ($remaining -ge 20) { [System.Drawing.Color]::FromArgb(235, 175, 35) } else { [System.Drawing.Color]::FromArgb(210, 70, 70) }
        $graphics.FillRectangle([System.Drawing.Brushes]::DimGray, 13, $y, 17, 8)
        $graphics.FillRectangle([System.Drawing.Brush]::new($color), 13, $y, [int](17 * $remaining / 100), 8)
    }
    $graphics.Dispose(); $handle = $bitmap.GetHicon(); $icon = [System.Drawing.Icon]::FromHandle($handle)
    return @{ Icon=$icon; Bitmap=$bitmap }
}

function Set-Startup([bool]$Enabled) {
    if ($Enabled) {
        $command = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script:ScriptPath`""
        New-ItemProperty -Path $script:StartupKey -Name $script:StartupValue -Value $command -PropertyType String -Force | Out-Null
    } else { Remove-ItemProperty -Path $script:StartupKey -Name $script:StartupValue -ErrorAction SilentlyContinue }
}

function Test-Startup { return $null -ne (Get-ItemProperty -Path $script:StartupKey -Name $script:StartupValue -ErrorAction SilentlyContinue).$script:StartupValue }

$initial = Get-LatestQuota
if ($Once) {
    if ($initial.Error) { "Codex --`n$($initial.Error)" } else { $initial.Windows | ForEach-Object { "$($_.Label): $([math]::Round($_.Used))% used / $([math]::Round($_.Remaining))% left; reset in $(Get-RelativeTime $_.ResetsAt)" } }
    exit
}

$context = [System.Windows.Forms.ApplicationContext]::new()
$tray = [System.Windows.Forms.NotifyIcon]::new(); $tray.Visible = $true
$menu = [System.Windows.Forms.ContextMenuStrip]::new(); $tray.ContextMenuStrip = $menu
$script:lastIcon = $null; $script:lastBitmap = $null

function Refresh-Tray {
    $snapshot = Get-LatestQuota
    $menu.Items.Clear() | Out-Null
    $item = $menu.Items.Add($script:AppName); $item.Enabled = $false
    if ($snapshot.Error) {
        $item = $menu.Items.Add($snapshot.Error); $item.Enabled = $false; $tray.Text = "Codex quota: unavailable"
    } else {
        if ($snapshot.Plan) { $item = $menu.Items.Add("Plan: $($snapshot.Plan)"); $item.Enabled = $false }
        if ($snapshot.Reached) { $item = $menu.Items.Add("Limit reached: $($snapshot.Reached)"); $item.Enabled = $false }
        [void]$menu.Items.Add('-')
        $tooltip = @()
        foreach ($window in $snapshot.Windows) {
            $line = "$($window.Label): $([math]::Round($window.Used))% used / $([math]::Round($window.Remaining))% left"
            if ($null -ne $window.ResetsAt) { $line += "; resets in $(Get-RelativeTime $window.ResetsAt)" }
            $item = $menu.Items.Add($line); $item.Enabled = $false; $tooltip += "$($window.Label) $([math]::Round($window.Remaining))% left"
        }
        [void]$menu.Items.Add('-'); $item = $menu.Items.Add("Updated: $($snapshot.Observed.ToString('g'))"); $item.Enabled = $false
        $item = $menu.Items.Add("Source: $([IO.Path]::GetFileName($snapshot.Source))"); $item.Enabled = $false
        $tray.Text = (($tooltip -join ' / ').Substring(0, [math]::Min(63, ($tooltip -join ' / ').Length)))
    }
    [void]$menu.Items.Add('-')
    $refresh = $menu.Items.Add('Refresh now'); $refresh.add_Click({ Refresh-Tray })
    $logs = $menu.Items.Add('Open session logs'); $logs.add_Click({ Start-Process "$env:USERPROFILE\.codex\sessions" })
    $startup = $menu.Items.Add('Start at login'); $startup.Checked = Test-Startup; $startup.add_Click({ Set-Startup (-not (Test-Startup)); Refresh-Tray })
    [void]$menu.Items.Add('-')
    $quit = $menu.Items.Add('Quit'); $quit.add_Click({ $tray.Visible = $false; $context.ExitThread() })
    $newIcon = New-TrayIcon $snapshot; $tray.Icon = $newIcon.Icon
    if ($script:lastIcon) { $script:lastIcon.Dispose() }; if ($script:lastBitmap) { $script:lastBitmap.Dispose() }
    $script:lastIcon = $newIcon.Icon; $script:lastBitmap = $newIcon.Bitmap
}

Refresh-Tray
$timer = [System.Windows.Forms.Timer]::new(); $timer.Interval = 15000; $timer.add_Tick({ Refresh-Tray }); $timer.Start()
[System.Windows.Forms.Application]::Run($context)
$timer.Dispose(); $tray.Dispose(); if ($script:lastIcon) { $script:lastIcon.Dispose() }; if ($script:lastBitmap) { $script:lastBitmap.Dispose() }
