# Dot-source from the repository root for reproducible local tooling.
$qfreyRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $qfreyRoot '.cache/tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $qfreyRoot '.cache/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $qfreyRoot '.cache/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $qfreyRoot '.cache/nuget/http'
$env:UV_CACHE_DIR = Join-Path $qfreyRoot '.cache/uv'
$env:UV_PROJECT_ENVIRONMENT = Join-Path $qfreyRoot '.cache/venv'
$env:PYTHONPYCACHEPREFIX = Join-Path $qfreyRoot '.cache/pycache'
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $qfreyRoot '.cache/playwright'
$env:PWTEST_CACHE_DIR = Join-Path $qfreyRoot '.cache/playwright-transform'
# Playwright 1.52's experimental TS ESM loader hangs on this Node 24 Windows toolchain.
$env:PW_DISABLE_TS_ESM = '1'
$env:PATH = "$env:DOTNET_ROOT;$(Join-Path $qfreyRoot '.cache/tools/uv/Scripts');$env:PATH"
