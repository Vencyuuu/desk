param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\DeskNest'))

$ErrorActionPreference = 'Stop'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $locator -latest -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw '构建桌面菜单扩展需要 Visual Studio C++ x64 工具。' }

$compilerDirectory = Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\shobjidl.h') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { throw '缺少 Windows SDK。' }

$temporaryDirectory = Join-Path $PSScriptRoot '.build\shell'
New-Item -ItemType Directory -Force -Path $temporaryDirectory, $OutputDirectory | Out-Null
$compiler = Join-Path $compilerDirectory.FullName 'bin\Hostx64\x64\cl.exe'
$nativeSource = Join-Path $PSScriptRoot 'src\DeskNest.Shell\ExplorerCommand.cpp'
$exports = Join-Path $PSScriptRoot 'src\DeskNest.Shell\DeskNest.Shell.def'
$library = Join-Path $temporaryDirectory 'DeskNest.Shell.dll'
$options = @(
    '/nologo', '/std:c++17', '/utf-8', '/W4', '/WX', '/EHsc', '/LD', '/MT', '/O2',
    '/DUNICODE', '/D_UNICODE',
    "/I$($compilerDirectory.FullName)\include", "/I$($sdk.FullName)\ucrt",
    "/I$($sdk.FullName)\shared", "/I$($sdk.FullName)\um", "/I$($sdk.FullName)\winrt",
    $nativeSource, "/Fo$temporaryDirectory\", "/Fe$library", '/link', '/Brepro',
    "/DEF:$exports", "/IMPLIB:$temporaryDirectory\DeskNest.Shell.lib",
    "/LIBPATH:$($compilerDirectory.FullName)\lib\x64",
    "/LIBPATH:$sdkRoot\Lib\$($sdk.Name)\ucrt\x64",
    "/LIBPATH:$sdkRoot\Lib\$($sdk.Name)\um\x64",
    'ole32.lib', 'shell32.lib', 'shlwapi.lib', 'uuid.lib', 'user32.lib', 'advapi32.lib'
)
& $compiler @options
if ($LASTEXITCODE -ne 0) { throw '原生桌面菜单扩展构建失败。' }

# 使用内容版本文件名，避免更新时覆盖已被 Explorer 加载的 DLL。
$digest = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
$name = "DeskNest.Shell.$digest.dll"
$destination = Join-Path $OutputDirectory $name
if (-not (Test-Path -LiteralPath $destination)) {
    Copy-Item -LiteralPath $library -Destination $destination
}
Set-Content -LiteralPath (Join-Path $OutputDirectory 'shell-extension.txt') -Value $name -Encoding utf8
Write-Host "桌面菜单扩展：$name"
