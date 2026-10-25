param(
    [ValidateSet('Deck', 'Graveyard', 'EngineRegression', 'All')]
    [string] $Scope = 'Deck'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$filter = switch ($Scope) {
    'Deck' { 'Scope=Deck' }
    'Graveyard' { 'Scope=Graveyard' }
    'EngineRegression' { 'Scope!=Deck&Scope!=Graveyard' }
    'All' { $null }
}

$projects = if ($Scope -eq 'Graveyard') { @('MtgEngine.Rules.Tests') } else { @('MtgEngine.Rules.Tests', 'MtgEngine.Api.Tests') }
foreach ($project in $projects) {
    Write-Host "$Scope verification: $project"
    $arguments = @('test', (Join-Path $repoRoot "tests/$project"), '--no-restore', '--verbosity', 'minimal')
    if ($filter) { $arguments += @('--filter', $filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
