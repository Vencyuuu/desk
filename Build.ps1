param([switch]$Run)

$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$releaseDirectory = Join-Path $projectDirectory 'artifacts\DeskNest-Desktop'
$buildDirectory = Join-Path $projectDirectory '.build\release'

# 将 SDK 的首次运行缓存留在工作区内，构建不需要修改用户级开发环境。
$env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.build\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

& dotnet publish (Join-Path $projectDirectory 'src\DeskNest\DeskNest.csproj') `
    --configuration Release `
    --self-contained false `
    --nologo `
    -p:UseSharedCompilation=false `
    "-p:OutputPath=$buildDirectory/" `
    --output $releaseDirectory

if ($LASTEXITCODE -ne 0) {
    throw 'DeskNest 构建失败，请查看上方错误。'
}

Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md') `
    -Destination (Join-Path $releaseDirectory '使用说明.md') -Force

& (Join-Path $projectDirectory 'Build-Shell.ps1') -OutputDirectory $releaseDirectory

Write-Host "构建完成：$releaseDirectory"
if ($Run) {
    # 此处启动的是用户交互应用，需要显示窗口；不是后台构建辅助进程。
    Start-Process -FilePath (Join-Path $releaseDirectory 'DeskNest.exe') -WindowStyle Normal
}
