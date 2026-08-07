# Builds the Solitaire Desk installer from a clean publish.
#
#     pwsh installer\build.ps1          (or: powershell -File installer\build.ps1)
#
# Two steps, in order: publish self-contained, then compile the .iss. Kept as a script because the
# ISCC path differs between a per-user winget install and a machine-wide one, and getting that
# wrong is the whole of "it worked on my machine".

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install --id JRSoftware.InnoSetup"
}

# A running copy locks DiamondDesktop.exe and the publish fails halfway through.
if (Get-Process DiamondDesktop -ErrorAction SilentlyContinue) {
    throw "Solitaire Desk is running. Close it before building."
}

Write-Host "Publishing..." -ForegroundColor Cyan
& dotnet publish "$root\DiamondDesktop\DiamondDesktop.csproj" `
    -c Release -o "$root\publish\DiamondDesktop" --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc "$root\installer\SolitaireDesk.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-ChildItem "$root\dist\SolitaireDesk-Setup-*.exe" |
         Sort-Object LastWriteTime | Select-Object -Last 1

Write-Host ""
Write-Host ("Built {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
