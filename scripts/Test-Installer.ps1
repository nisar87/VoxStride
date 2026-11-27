<#
.SYNOPSIS
  Installs VoxStride silently for the current user, verifies the install, runs the headless smoke test from the
  installed executable, uninstalls, and verifies removal. Writes a JSON evidence file.
.EXAMPLE
  scripts/Test-Installer.ps1 -Installer .\artifacts\installer\VoxStride-Setup-0.1.0-x64.exe -Evidence .\installer-evidence.json
#>
param(
  [Parameter(Mandatory)] [string] $Installer,
  [string] $Evidence = "installer-evidence.json"
)
$ErrorActionPreference = 'Stop'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\VoxStride'
$exe = Join-Path $appDir 'VoxStride.exe'
$result = [ordered]@{ caseId = 'INST-01'; installer = (Split-Path $Installer -Leaf); os = [Environment]::OSVersion.VersionString; steps = @() }
function Step($name, $ok, $detail) { $script:result.steps += [ordered]@{ name = $name; ok = $ok; detail = $detail }; if (-not $ok) { throw "$name failed: $detail" } }

try {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/LOG=install.log' -Wait -PassThru
  Step 'install-exit-code' ($p.ExitCode -eq 0) "exit $($p.ExitCode), $([int]$sw.Elapsed.TotalSeconds)s"
  Step 'exe-present' (Test-Path $exe) $exe
  $uninst = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -eq 'VoxStride' }
  Step 'uninstall-entry-hkcu' ($null -ne $uninst) 'per-user uninstall registration'
  Step 'no-admin-required' (-not (Test-Path (Join-Path $env:ProgramFiles 'VoxStride'))) 'not installed under Program Files'

  $hostJson = Join-Path $appDir 'browser-extension\com.voxstride.bridge.json'
  Step 'browser-host-manifest' (Test-Path $hostJson) $hostJson
  $hostDef = Get-Content $hostJson -Raw | ConvertFrom-Json
  Step 'browser-host-path' ($hostDef.path -eq $exe) $hostDef.path
  Step 'browser-host-origin' ($hostDef.allowed_origins.Count -eq 1 -and $hostDef.allowed_origins[0] -like 'chrome-extension://*/') ($hostDef.allowed_origins -join ',')
  foreach ($b in @('Google\Chrome', 'Microsoft\Edge')) {
    $k = "HKCU:\Software\$b\NativeMessagingHosts\com.voxstride.bridge"
    Step "browser-host-registered-$($b.Split('\')[0])" ((Get-ItemProperty -Path $k -ErrorAction SilentlyContinue).'(default)' -eq $hostJson) $k
  }
  Step 'extension-files' ((Test-Path (Join-Path $appDir 'browser-extension\manifest.json')) -and (Test-Path (Join-Path $appDir 'browser-extension\background.js'))) 'manifest.json, background.js'

  $smoke = Join-Path $env:TEMP 'voxstride-smoke-installed.json'
  $p = Start-Process -FilePath $exe -ArgumentList '--smoke-test', '--out', $smoke -Wait -PassThru
  $smokeJson = if (Test-Path $smoke) { Get-Content $smoke -Raw } else { '{}' }
  $result.smoke = $smokeJson | ConvertFrom-Json
  Step 'smoke-test' ($p.ExitCode -eq 0) "exit $($p.ExitCode)"

  $uninstaller = Join-Path $appDir 'unins000.exe'
  $p = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
  Start-Sleep -Seconds 3
  Step 'uninstall-exit-code' ($p.ExitCode -eq 0) "exit $($p.ExitCode)"
  Step 'app-removed' (-not (Test-Path $exe)) $exe
  Step 'browser-host-unregistered' (-not (Test-Path 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.voxstride.bridge')) 'native host key removed'
  Step 'data-removed' (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'VoxStride'))) 'user data folder removed on silent uninstall'
  $result.status = 'passed'
}
catch {
  $result.status = 'failed'
  $result.error = $_.ToString()
  if (Test-Path install.log) { $result.installLogTail = (Get-Content install.log -Tail 40) -join "`n" }
}
finally {
  $result | ConvertTo-Json -Depth 8 | Set-Content -Path $Evidence -Encoding utf8
  Get-Content $Evidence
}
if ($result.status -ne 'passed') { exit 1 }
