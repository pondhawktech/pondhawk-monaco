<#
.SYNOPSIS
    Cake Frosting bootstrapper for Windows. The companion to build.sh.

.EXAMPLE
    ./build.ps1                                     # Default: Build + Test
    ./build.ps1 --target Bundle                     # Re-bundle Monaco into wwwroot/dist
    ./build.ps1 --target Pack                       # Produce the NuGet package into artifacts/
    ./build.ps1 --target Demo                       # Run the WASM demo harness
    ./build.ps1 --target Version --bump=minor       # Bump the version file

.NOTES
    Cake Frosting is an ordinary .NET console app, so every target runs identically on Windows, Linux
    and macOS. This file exists only because bash is not the shell here — it passes its arguments
    straight through.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $CakeArguments
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot

try {
    dotnet run --project build/Build.csproj -- @CakeArguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
