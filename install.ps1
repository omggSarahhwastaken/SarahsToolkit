# Sarah's Toolkit one-line installer.
# Friends install by pasting this into PowerShell (no GitHub browsing, no unzip):
#   irm https://raw.githubusercontent.com/omggSarahhwastaken/SarahsToolkit/main/install.ps1 | iex
# The script downloads the latest release installer and runs it. The
# installer self-elevates (UAC prompt) when it needs to write to Program Files.
$ErrorActionPreference = 'Stop'
$url = 'https://github.com/omggSarahhwastaken/SarahsToolkit/releases/latest/download/SarahsToolkitInstaller_x64.exe'
$tmp = Join-Path $env:TEMP 'SarahsToolkitInstaller_x64.exe'
Write-Host "Downloading Sarah's Toolkit…"
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
Write-Host 'Running installer…'
Start-Process -FilePath $tmp -Wait
Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Host "Done — Sarah's Toolkit is installed."
