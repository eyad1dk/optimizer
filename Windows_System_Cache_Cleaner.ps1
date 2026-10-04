<# EZoptimizer read-only cleanup inspection. No deletion is performed. #>
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
try {
 $root=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 $rootItem=Get-Item -LiteralPath $root
 if($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked temporary folders are excluded.'}
 $files=@(Get-ChildItem -LiteralPath $root -File -Force | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-7)} | Select-Object -First 25000)
 $bytes=($files|Measure-Object Length -Sum).Sum
 [pscustomobject]@{success=$true;id='cleanup.inspect';readOnly=$true;category='User temp';files=$files.Count;logicalBytes=[long]$bytes;recursive=$false;message='Read-only top-level estimate. Use EZoptimizer to scan and review individual files. No files or services were changed.'}|ConvertTo-Json -Compress
} catch {
 [pscustomobject]@{success=$false;id='cleanup.inspect';message=$_.Exception.Message}|ConvertTo-Json -Compress
 exit 1
}
