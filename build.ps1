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
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 同步更新小体积单文件版（需对方装 .NET 9 桌面运行时），发给别人用的就是 publish\small\Safe720.exe
$small = Join-Path $PSScriptRoot "publish\small"
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none --disable-parallel -o $small
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "单文件版已更新：$small\Safe720.exe"
