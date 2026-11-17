# NetSwitch installer: downloads the latest release and installs it for the current user.
#   irm https://raw.githubusercontent.com/JetLaggedJackal/netswitch/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$dir = Join-Path $env:LOCALAPPDATA 'Programs\NetSwitch'
$exe = Join-Path $dir 'NetSwitch.exe'
$url = 'https://github.com/JetLaggedJackal/netswitch/releases/latest/download/NetSwitch.exe'

Write-Host 'Installing NetSwitch...'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# Stop a running copy so the file can be replaced (profiles and settings live elsewhere and are kept).
Get-Process NetSwitch -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object {
    $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500
    if (-not $_.HasExited) { $_ | Stop-Process -Force }
}

$tmp = "$exe.download"
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
Unblock-File $tmp
Move-Item -Force $tmp $exe

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'NetSwitch.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dir
$s.Description = 'Switch network profiles from the tray'
$s.Save()

Start-Process $exe
Write-Host "NetSwitch installed to $dir and started. Look for its icon in the system tray."
