$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Management.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll','System.Runtime.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }

$winmdPath = (Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\UnionMetadata" -Recurse -Filter "Windows.winmd" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if (-not $winmdPath -and (Test-Path "$PSScriptRoot\Windows.winmd")) {
    $winmdPath = "$PSScriptRoot\Windows.winmd"
}
$rtPath = (Get-ChildItem -Path "$env:WINDIR\Microsoft.NET\assembly" -Recurse -Filter "System.Runtime.WindowsRuntime.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName

if ($winmdPath -and $rtPath) {
    $refs += "/reference:$winmdPath"
    $refs += "/reference:$rtPath"
}

$outExe = "$PSScriptRoot\TokenIsland.exe"

# 若程式正在運行，先停止舊進程以釋放鎖定
$wasRunning = $false
$procs = Get-Process TokenIsland -ErrorAction SilentlyContinue
if ($procs) {
    $wasRunning = $true
    Write-Output "Stopping running TokenIsland process..."
    Stop-Process -Name TokenIsland -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/win32icon:$PSScriptRoot\TokenIsland.ico" "/resource:$PSScriptRoot\TokenIsland.ico,TokenIsland.TrayIcon.ico" "/out:$outExe" @refs (Get-ChildItem -LiteralPath "$PSScriptRoot\src" -Filter '*.cs' | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output 'Built TokenIsland.exe successfully.'

if ($wasRunning) {
    Write-Output "Restarting TokenIsland..."
    Start-Process -FilePath $outExe -WorkingDirectory $PSScriptRoot
}
