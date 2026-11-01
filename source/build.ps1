# Builds FC27_DLSS_Switch.exe from DlssSwitch.cs with the C# compiler that ships with Windows (.NET Framework 4.x).
param([string]$Out = '')
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $src) 'FC27_DLSS_Switch.exe' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw ('C# compiler of Windows not found: ' + $csc) }
$cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/warn:0',
    ('/out:' + $Out),
    ('/win32manifest:' + (Join-Path $src 'app.manifest')),
    ('/win32icon:' + (Join-Path $src 'app.ico')),
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll',
    (Join-Path $src 'DlssSwitch.cs'))
$ErrorActionPreference = 'Continue'
$output = & $csc $cscArgs 2>&1
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
foreach ($l in $output) { Write-Host ([string]$l) }
if ($code -ne 0) { throw ('Compiling failed (code ' + $code + ')') }
Write-Host ('EXE created: ' + $Out)
