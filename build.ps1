param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\CodexProxySwitcher.csproj"
$outputPath = Join-Path $projectRoot "dist"

$arguments = @("publish", $projectPath, "-c", "Release", "-r", "win-x64", "-p:PublishSingleFile=true", "-o", $outputPath)
if ($SelfContained) { $arguments += @("--self-contained", "true", "-p:EnableCompressionInSingleFile=true") }
else { $arguments += @("--self-contained", "false") }

dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Build completed: $(Join-Path $outputPath 'CodexProxySwitcher.exe')"
