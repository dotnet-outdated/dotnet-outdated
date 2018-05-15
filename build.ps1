#!/usr/bin/env pwsh
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Debug', 'Release')]
    $Configuration = $null,
	[switch]
	$IsOfficialBuild
)

Set-StrictMode -Version 1
$ErrorActionPreference = 'Stop'

function exec([string]$_cmd) {
    write-host -ForegroundColor DarkGray ">>> $_cmd $args"
    $ErrorActionPreference = 'Continue'
    & $_cmd @args
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        write-error "Failed with exit code $LASTEXITCODE"
        exit 1
    }
}

#
# Main
#


if (!$Configuration) {
    $Configuration = if ($env:CI -or $IsOfficialBuild) { 'Release' } else { 'Debug' }
}

[string[]] $MSBuildArgs = @("-p:Configuration=$Configuration")

if ($IsOfficialBuild) {
	$MSBuildArgs += '-p:CI=true'
}

$artifacts = "$PSScriptRoot/artifacts/"

Remove-Item -Recurse $artifacts -ErrorAction Ignore

exec dotnet build @MSBuildArgs

exec dotnet pack `
    --no-build `
    -o $artifacts @MSBuildArgs

#exec dotnet test `
#    "$PSScriptRoot/test/GitHubIssuesCli.Tests/" `
#    --no-build `
#     @MSBuildArgs

write-host -f magenta 'Done'