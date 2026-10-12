# 构建脚本：沙箱下 NuGet 临时目录重定向到工作区
$base = Join-Path $PSScriptRoot ".build"
New-Item -ItemType Directory -Force -Path "$base\scratch","$base\packages","$base\clihome","$base\tmp" | Out-Null
$env:NUGET_SCRATCH="$base\scratch"
$env:NUGET_PACKAGES="$base\packages"
$env:DOTNET_CLI_HOME="$base\clihome"
$env:TEMP="$base\tmp"
$env:TMP="$base\tmp"
$env:DOTNET_CLI_TELEMETRY_OPTOUT="1"
$env:DOTNET_NOLOGO="1"
# 公司网络下 NuGet 会卡在证书吊销检查/并行解压，改为离线吊销检查 + 串行还原
$env:NUGET_CERT_REVOCATION_MODE="offline"
$proj = Join-Path $PSScriptRoot "src\Safe720\Safe720.csproj"
dotnet restore $proj --disable-parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $proj -c Debug --no-restore $args
