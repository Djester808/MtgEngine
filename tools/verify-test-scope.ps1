param(
    [ValidateSet('Deck', 'EngineRegression', 'All')]
    [string] $Scope = 'Deck'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$filter = switch ($Scope) {
    'Deck' { 'Scope=Deck' }
    'EngineRegression' { 'Scope!=Deck' }
    'All' { $null }
}

foreach ($project in @('MtgEngine.Rules.Tests', 'MtgEngine.Api.Tests')) {
    Write-Host "$Scope verification: $project"
    $arguments = @('test', (Join-Path $repoRoot "tests/$project"), '--no-restore', '--verbosity', 'minimal')
    if ($filter) { $arguments += @('--filter', $filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
