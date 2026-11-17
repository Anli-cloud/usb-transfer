[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The 64-bit .NET Framework compiler was not found at $compiler."
}

$references = @(
    'WPF\WindowsBase.dll'
    'WPF\PresentationCore.dll'
    'WPF\PresentationFramework.dll'
    'WPF\WindowsFormsIntegration.dll'
    'System.Xaml.dll'
    'System.Windows.Forms.dll'
    'System.Drawing.dll'
    'System.Runtime.Serialization.dll'
    'System.Management.dll'
    'Microsoft.CSharp.dll'
    'Microsoft.VisualBasic.dll'
) | ForEach-Object {
    $reference = Join-Path $framework $_
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "A required .NET Framework assembly was not found at $reference."
    }
    '/reference:' + $reference
}

$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.cs' |
    Where-Object { $_.Name -notlike '*.Tests.cs' } |
    Sort-Object -Property Name |
    Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) {
    throw 'No C# app sources were found.'
}

$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[void] [System.IO.Directory]::CreateDirectory($destination)
$executable = Join-Path $destination 'USBTransfer.exe'
$manifest = Join-Path $PSScriptRoot 'app.manifest'

$compilerArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/langversion:5'
    '/codepage:65001'
    '/utf8output'
    '/optimize+'
    '/out:' + $executable
    '/win32manifest:' + $manifest
) + $references + $sources

& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Write-Output $executable
