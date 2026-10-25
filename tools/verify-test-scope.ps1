param(
    [ValidateSet('Deck', 'Graveyard', 'Hand', 'Exile', 'Player', 'CommandZone', 'Reveal', 'Stack', 'Battlefield', 'TokenCopy', 'GameTable', 'TurnPriority', 'FoundationIntegration', 'Foundations', 'EngineRegression', 'All')]
    [string] $Scope = 'Deck'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$foundationScopes = @('Deck', 'Graveyard', 'Hand', 'Exile', 'Player', 'CommandZone', 'Reveal', 'Stack', 'Battlefield', 'TokenCopy', 'GameTable', 'TurnPriority', 'FoundationIntegration')
$filter = switch ($Scope) {
    'Foundations' { ($foundationScopes | ForEach-Object { "Scope=$_" }) -join '|' }
    'EngineRegression' { ($foundationScopes | ForEach-Object { "Scope!=$_" }) -join '&' }
    'All' { $null }
    default { "Scope=$Scope" }
}

$projects = if ($Scope -in @('Deck', 'Foundations', 'EngineRegression', 'All')) { @('MtgEngine.Rules.Tests', 'MtgEngine.Api.Tests') } else { @('MtgEngine.Rules.Tests') }
foreach ($project in $projects) {
    Write-Host "$Scope verification: $project"
    $arguments = @('test', (Join-Path $repoRoot "tests/$project"), '--no-restore', '--verbosity', 'minimal')
    if ($filter) { $arguments += @('--filter', $filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
