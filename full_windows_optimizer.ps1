<#
EZoptimizer compatibility entry point. Default behavior is read-only.
The old unconditional optimizer is available only in Git history.
#>
[CmdletBinding()]
param([ValidateSet('Inspect','OpenApp')][string]$Action='Inspect',[string]$ExecutablePath)
$ErrorActionPreference='Stop'
try {
 if($Action -eq 'OpenApp') {
  if(-not $ExecutablePath){$ExecutablePath=Join-Path $PSScriptRoot 'EZoptimizer.exe'}
  $resolved=(Resolve-Path -LiteralPath $ExecutablePath).Path
  if([IO.Path]::GetFileName($resolved) -ne 'EZoptimizer.exe'){throw 'Select the published EZoptimizer.exe.'}
  Start-Process -FilePath $resolved -WindowStyle Hidden
  [pscustomobject]@{success=$true;id='app.open';message='EZoptimizer opened. Stage and preview changes inside the app.'}|ConvertTo-Json -Compress
 } else {
  $os=Get-CimInstance Win32_OperatingSystem
  $cpu=Get-CimInstance Win32_Processor|Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
  [pscustomobject]@{success=$true;id='system.inspect';readOnly=$true;windows=$os.Caption;build=$os.BuildNumber;cpu=@($cpu);message='No optimizations were applied. Use the desktop review queue.'}|ConvertTo-Json -Depth 4 -Compress
 }
} catch {
 [pscustomobject]@{success=$false;id='system.inspect';message=$_.Exception.Message}|ConvertTo-Json -Compress
 exit 1
}
