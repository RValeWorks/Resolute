param(
    [string]$GamePath,
    [string]$DotnetPath,
    [switch]$NativeCruiseExperiment,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (-not $GamePath) { $GamePath = $env:NUCLEAR_OPTION_GAME }
if (-not $GamePath) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $candidates = @('D:\SteamLibrary\steamapps\common\Nuclear Option')
    if ($steam) {
        $candidates += Join-Path $steam 'steamapps\common\Nuclear Option'
        $libraries = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraries) {
            foreach ($line in Get-Content -LiteralPath $libraries) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $candidates += Join-Path ($Matches[1].Replace('\\', '\')) 'steamapps\common\Nuclear Option'
                }
            }
        }
    }
    $GamePath = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'NuclearOption.exe') } | Select-Object -First 1
}
if (-not $GamePath) { throw 'Specify -GamePath or set NUCLEAR_OPTION_GAME to your Nuclear Option folder.' }
$managed = Join-Path $GamePath 'NuclearOption_Data\Managed'
$core = Join-Path $GamePath 'BepInEx\core'
foreach ($required in @((Join-Path $managed 'Assembly-CSharp.dll'), (Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Required local reference is missing: ' + $required) }
}
if (-not $DotnetPath) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $DotnetPath = $command.Source }
    elseif ($env:ProgramFiles) { $DotnetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
}
if (-not $DotnetPath -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'Install a .NET SDK, or specify its dotnet executable using -DotnetPath.'
}
$sdkLines = & $DotnetPath --list-sdks
if ($LASTEXITCODE -ne 0) { throw '.NET SDK discovery failed.' }
$sdks = @(foreach ($line in $sdkLines) {
    if ($line -match '^([^ ]+)\s+\[(.+)\]$') {
        $versionText = $Matches[1]
        $compiler = Join-Path (Join-Path $Matches[2] $versionText) 'Roslyn\bincore\csc.dll'
        if (Test-Path -LiteralPath $compiler -PathType Leaf) {
            [pscustomobject]@{ Version = [version](($versionText -split '-')[0]); Text = $versionText; Compiler = $compiler }
        }
    }
})
$sdk = $sdks | Sort-Object Version -Descending | Select-Object -First 1
if (-not $sdk) { throw 'No .NET SDK with the C# compiler was found. A runtime alone cannot build the plugin.' }

$out = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'build\plugin' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$dll = Join-Path $out 'Resolute.dll'
$receipt = Join-Path $out 'build_receipt.json'
if (Test-Path -LiteralPath $receipt) { Remove-Item -LiteralPath $receipt }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src\Resolute') -Filter '*.cs' -File | Sort-Object Name)
if ($sources.Count -eq 0) { throw 'Plugin source files were not found.' }
$options = [System.Collections.Generic.List[string]]::new()
foreach ($option in @('/nologo', '/target:library', '/langversion:latest', '/optimize+', '/deterministic+', '/nostdlib+')) { $options.Add($option) }
if ($NativeCruiseExperiment) { $options.Add('/define:RESOLUTE_NATIVE_CRUISE_EXPERIMENT') }
$options.Add('/out:"' + $dll + '"')
Get-ChildItem -LiteralPath $managed -Filter '*.dll' -File | Sort-Object Name | ForEach-Object { $options.Add('/reference:"' + $_.FullName + '"') }
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) { $options.Add('/reference:"' + (Join-Path $core $name) + '"') }
foreach ($source in $sources) { $options.Add('"' + $source.FullName + '"') }
$mode = if ($NativeCruiseExperiment) { 'NativeCruiseExperiment' } else { 'Full' }
$rsp = Join-Path $out 'compile.rsp'
$options | Set-Content -LiteralPath $rsp -Encoding UTF8
$sourceBefore = @{}
foreach ($source in $sources) { $sourceBefore[$source.FullName] = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
& $DotnetPath $sdk.Compiler "@$rsp"
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed; no valid build receipt was written.' }
$sourceEntries = @(foreach ($source in $sources) {
    $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $sourceBefore[$source.FullName]) { throw ('Source changed during compilation; rebuild before using this DLL: ' + $source.Name) }
    [ordered]@{ path = $source.FullName.Substring($workspace.Length + 1).Replace('\', '/'); sha256 = $sourceHash }
})
[ordered]@{
    schemaVersion = 1; mode = $mode; sdk = $sdk.Text
    pluginSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    sources = $sourceEntries
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt -Encoding UTF8
Get-Item -LiteralPath $dll | Select-Object FullName, Length
