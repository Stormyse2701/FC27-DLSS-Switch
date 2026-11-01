# FC 27 DLSS Mod - Uninstall: removes the NVIDIA DLLs installed by this mod and restores backed-up files.
$ErrorActionPreference = 'Stop'
$files = Split-Path -Parent $MyInvocation.MyCommand.Path
$mod = Split-Path -Parent $files
$expected = [ordered]@{
    'nvngx_dlss.dll'    = 'D00B5B0AB29C3CA101B70C3A5ECB3A01B7EF33766A3383A28A18623083E6123E'
    'nvngx_dlssg.dll'   = '8D59D49B36410DD6B62FEF2503A5871D347415BD85D943FA0848563A95DD50C7'
    'sl.common.dll'     = '38EEA3B4209AFC455636EA372B5933D65FCC13BECEC6C84D101DCFC4D8648F75'
    'sl.dlss.dll'       = '18871EEA87C90345DA97D8109FAA81EA7D5840E8D99B51B55B0E46CFA177F84E'
    'sl.dlss_g.dll'     = '3ED9538DF724993BEA8A050195A6A78B9C8C8A566D5B05ADE477AAB3C390E90F'
    'sl.interposer.dll' = 'B1D7395AEBD7B9722841D64DCF7433680020B6A8E421F851C16D5BA0C2182EDF'
    'sl.pcl.dll'        = '5698C38E722318681F215C9FE68E45B9BEDD51378ACC699D8EEE26E961E57770'
    'sl.reflex.dll'     = '443577E869352436FF7EC8F4D9C90418D3E95368436CE219261E0F4F07A38122'
}
function Find-GameDir {
    foreach ($name in @('gamefolder.txt', 'spielordner.txt')) {
        $saved = Join-Path $mod $name
        if (Test-Path -LiteralPath $saved) {
            $g = (Get-Content -LiteralPath $saved | Select-Object -First 1).Trim()
            if ($g -and (Test-Path -LiteralPath (Join-Path $g 'FC27.exe'))) { return $g }
        }
    }
    $d = $mod
    for ($i = 0; $i -lt 4 -and $d; $i++) {
        if (Test-Path -LiteralPath (Join-Path $d 'FC27.exe')) { return $d }
        $d = Split-Path -Parent $d
    }
    $p = Get-Process -Name FC27 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.Path) { return (Split-Path -Parent $p.Path) }
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Please select the FC 27 game folder (the folder that contains FC27.exe).'
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        if (Test-Path -LiteralPath (Join-Path $dlg.SelectedPath 'FC27.exe')) { return $dlg.SelectedPath }
    }
    return $null
}

try {
    Write-Host 'FC 27 DLSS Mod - Uninstall'
    Write-Host ''
    $game = Find-GameDir
    if (-not $game) { throw 'FC 27 game folder not found.' }
    Write-Host ('Game folder: ' + $game)
    if (Get-Process -Name FC27 -ErrorAction SilentlyContinue) { throw 'FC 27 is running. Please close the game first.' }
    $sw = @(Get-Process -Name FC27_DLSS_Switch, FC27_DLSS_Schalter -ErrorAction SilentlyContinue)
    if ($sw.Count -gt 0) { $sw | Stop-Process -Force; Write-Host 'DLSS Switch closed.' }

    $backup = Join-Path $mod 'backup'
    foreach ($k in $expected.Keys) {
        $dst = Join-Path $game $k
        if (Test-Path -LiteralPath $dst) {
            $h = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
            if ($h -eq $expected[$k]) { Remove-Item -LiteralPath $dst -Force; Write-Host ('Removed: ' + $k) }
            else { Write-Host ('Skipped (different version, not from this mod): ' + $k) }
        }
        $bk = Join-Path $backup $k
        if (Test-Path -LiteralPath $bk) { Copy-Item -LiteralPath $bk -Destination $dst -Force; Write-Host ('Restored: ' + $k) }
    }
    Write-Host ''
    Write-Host 'DONE. You can now delete the FC27_DLSS_Mod folder.'
} catch {
    Write-Host ''
    Write-Host ('ERROR: ' + $_.Exception.Message)
}
