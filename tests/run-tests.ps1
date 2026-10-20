param([string[]]$Names = @('CompletionTests', 'AntigravityTests', 'TopmostTests'))
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path $PSScriptRoot -Parent
$outputDir = Join-Path $projectDir 'scratch\review-check'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Management.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll','System.Runtime.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$winmdPath = (Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\UnionMetadata" -Recurse -Filter "Windows.winmd" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if (-not $winmdPath -and (Test-Path "$projectDir\Windows.winmd")) {
    $winmdPath = "$projectDir\Windows.winmd"
}
$rtPath = (Get-ChildItem -Path "$env:WINDIR\Microsoft.NET\assembly" -Recurse -Filter "System.Runtime.WindowsRuntime.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if ($winmdPath -and $rtPath) {
    $refs += "/reference:$winmdPath"
    $refs += "/reference:$rtPath"
}
$sources = Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object FullName
foreach ($name in $Names) {
    $output = Join-Path $outputDir ($name + '.exe')
    & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 "/main:$name" "/out:$output" @refs @sources (Join-Path $PSScriptRoot ($name + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "$name compilation failed" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $output
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    if (-not $process.WaitForExit(10000)) {
        $process.Kill()
        throw "$name timed out after 10000ms and was terminated"
    }
    if ($process.ExitCode -ne 0) { throw "$name failed with exit code $($process.ExitCode)" }
}
