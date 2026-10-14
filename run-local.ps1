# Loads .env (gitignored) into this process's environment, then runs the API the same
# way "dotnet run" would. .NET has no built-in .env support, so this replaces typing
# every KOLEKSI env var override by hand on each launch.
$envFile = Join-Path $PSScriptRoot ".env"
if (-not (Test-Path $envFile)) {
    Write-Error "No .env file at $envFile"
    exit 1
}

Get-Content $envFile | ForEach-Object {
    $line = $_.Trim()
    if ($line -eq "" -or $line.StartsWith("#")) { return }
    $eq = $line.IndexOf("=")
    if ($eq -lt 0) { return }
    $name = $line.Substring(0, $eq).Trim()
    $value = $line.Substring($eq + 1).Trim()
    Set-Item -Path "env:$name" -Value $value
}

dotnet run --no-launch-profile --project (Join-Path $PSScriptRoot "src/Koleksi.Api")
