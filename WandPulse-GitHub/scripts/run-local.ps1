param([switch]$NoUpload)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$configFile = Join-Path $repoRoot 'local\config.json'
if (-not (Test-Path -LiteralPath $configFile)) { throw 'Create local/config.json from config.example.json first.' }
$config = Get-Content -LiteralPath $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
$mutex = New-Object System.Threading.Mutex($false, 'Local\WandPulseGitHubCollector')
if (-not $mutex.WaitOne(0)) { Write-Output 'Another collection is running.'; $mutex.Dispose(); exit 0 }
$result = 1
try {
    # User-scoped environment variables become available to already-open sessions.
    $credential = [Environment]::GetEnvironmentVariable('XMOD_LOGIN_CREDENTIAL','User')
    if ($credential) { $env:XMOD_LOGIN_CREDENTIAL = $credential }
    $env:GIT_TERMINAL_PROMPT = '0'
    $command = if ($NoUpload) { 'collect' } else { 'run' }
    & $config.pythonExe (Join-Path $PSScriptRoot 'pipeline.py') $command
    $result = $LASTEXITCODE
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
exit $result
