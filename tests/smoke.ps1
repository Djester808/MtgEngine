<#
.SYNOPSIS
    End-to-end smoke test for MtgEngine.Api.

.DESCRIPTION
    Exercises every AI endpoint plus non-AI regression checks against a running
    instance. This hits the LIVE Anthropic API and therefore costs real money and
    takes ~40s -- it is a deliberate pre-release check, not a CI gate.

    Start the API first:
        dotnet run --project MtgEngine.Api

.PARAMETER BaseUrl
    Root URL of the running API. Defaults to the Kestrel HTTPS endpoint in appsettings.json.

.PARAMETER SkipExpensive
    Skip the ai-build case (~99 Scryfall lookups + a 6000-token Sonnet call).

.EXAMPLE
    .\tests\smoke.ps1
.EXAMPLE
    .\tests\smoke.ps1 -SkipExpensive
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = "https://localhost:7001",
    [switch] $SkipExpensive
)

Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAllSmoke : ICertificatePolicy {
  public bool CheckValidationResult(ServicePoint sp, X509Certificate c, WebRequest r, int p) { return true; }
}
"@ -ErrorAction SilentlyContinue
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllSmoke
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$Base = $BaseUrl.TrimEnd('/')
$script:pass = 0; $script:fail = 0; $script:skip = 0

function Skip($name, $why) {
    Write-Host ("  SKIP  {0,-34} {1,6}    {2}" -f $name, "", $why) -ForegroundColor DarkGray
    $script:skip++
}

function Check($name, $block) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $block
        $sw.Stop()
        Write-Host ("  PASS  {0,-34} {1,6}ms  {2}" -f $name, $sw.ElapsedMilliseconds, $result) -ForegroundColor Green
        $script:pass++
    } catch {
        $sw.Stop()
        $detail = $_.Exception.Message
        if ($_.ErrorDetails) { $detail = $_.ErrorDetails.Message }
        Write-Host ("  FAIL  {0,-34} {1,6}ms  {2}" -f $name, $sw.ElapsedMilliseconds, $detail) -ForegroundColor Red
        $script:fail++
    }
}

Write-Host "`n=== AUTH / NON-AI REGRESSION ===" -ForegroundColor Cyan

$script:tok = $null
Check "auth/register (JWT new secret)" {
    $b = @{ username="smoke_$(Get-Random)"; email="smoke$(Get-Random)@x.com"; password="smoke123" } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/auth/register" -Method Post -Body $b -ContentType "application/json"
    $script:tok = $r.token
    "user=$($r.username)"
}
$H = @{ Authorization = "Bearer $script:tok" }

Check "decks GET (authorized)" {
    $r = Invoke-RestMethod "$Base/api/decks" -Headers $H
    "count=$(@($r).Count)"
}

Check "decks GET (401 without token)" {
    try { Invoke-RestMethod "$Base/api/decks" | Out-Null; throw "expected 401 but succeeded" }
    catch { if ($_.Exception.Response.StatusCode.value__ -eq 401) { "correctly rejected" } else { throw } }
}

$script:cmd = $null
Check "commanders GET (bulk data)" {
    # Endpoint returns a bare JSON array of {oracleId,name,imageUri,...}.
    # PS 5.1: Invoke-RestMethod writes a JSON array to the pipeline as ONE object,
    # so piping it straight into Select-Object selects the whole array. Assign to a
    # variable first -- variable expansion unrolls -- then take the first element.
    $all = Invoke-RestMethod "$Base/api/commanders" -Headers $H
    $script:cmd = $all | Select-Object -First 1
    if (-not $script:cmd) { throw "no commanders returned" }
    if (-not $script:cmd.oracleId) { throw "first commander has no oracleId" }
    if ($script:cmd.oracleId -is [array]) { throw "unwrap failed: got an array of ids" }
    "first='$($script:cmd.name)' id=$($script:cmd.oracleId.Substring(0,8))"
}

Check "commanders GET {id} (detail)" {
    $d = Invoke-RestMethod "$Base/api/commanders/$($script:cmd.oracleId)" -Headers $H
    # List payload omits oracle text; detail carries it. Needed by the AI prompts.
    $script:cmdText = if ($d.oracleText) { $d.oracleText } elseif ($d.card.oracleText) { $d.card.oracleText } else { "" }
    "textLen=$($script:cmdText.Length)"
}

$script:deckId = $null
Check "decks POST (create)" {
    $b = @{ name="smoke-deck-$(Get-Random)"; format="commander"; commanderOracleId=$script:cmd.oracleId } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $script:deckId = $r.deckId
    if (-not $script:deckId) { $script:deckId = $r.id }
    "deckId=$($script:deckId)"
}

Write-Host "`n=== AI ENDPOINTS ===" -ForegroundColor Cyan

Check "POST decks/synergy" {
    $b = @{ commanderOracleId="smoke-$(Get-Random)"; commanderName="Atraxa, Praetors' Voice";
            commanderText="Flying, vigilance, deathtouch, lifelink. At the beginning of your end step, proliferate.";
            cardOracleId="smoke-$(Get-Random)"; cardName="Doubling Season";
            cardText="If an effect would put one or more counters on a permanent, it puts twice that many instead.";
            deckCardNames=@() } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks/synergy" -Method Post -Body $b -ContentType "application/json" -Headers $H
    if ($r.score -lt 0 -or $r.score -gt 100) { throw "score out of range: $($r.score)" }
    "score=$($r.score)"
}

Check "POST decks/synergy/batch (scores a browse page, then serves it cached)" {
    # Uses real oracle ids: the batch path looks each card up to build its prompt.
    $cmd = Invoke-RestMethod "$Base/api/cards/by-name?name=Krenko,%20Mob%20Boss" -Headers $H
    $page = Invoke-RestMethod "$Base/api/cards/candidates?commanderOracleId=$($cmd.oracleId)&q=goblin&limit=5" -Headers $H
    $ids = @($page.cards | ForEach-Object { $_.card.oracleId })
    if ($ids.Count -eq 0) { throw "candidate pool returned nothing to score" }

    $b = @{ commanderOracleId = $cmd.oracleId; cardOracleIds = $ids } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks/synergy/batch" -Method Post -Body $b -ContentType "application/json" -Headers $H
    if (@($r).Count -eq 0) { throw "batch returned no scores for $($ids.Count) cards" }
    foreach ($s in $r) {
        if ($s.score -lt 1 -or $s.score -gt 100) { throw "score out of range for $($s.name): $($s.score)" }
    }

    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    Invoke-RestMethod "$Base/api/decks/synergy/batch" -Method Post -Body $b -ContentType "application/json" -Headers $H | Out-Null
    $sw2.Stop()
    if ($sw2.ElapsedMilliseconds -gt 1000) { throw "2nd call took $($sw2.ElapsedMilliseconds)ms - cache likely not hit" }
    "scored=$(@($r).Count) of $($ids.Count), 2nd call $($sw2.ElapsedMilliseconds)ms (cached)"
}

Check "POST decks/synergy/batch (rejects an oversized page)" {
    $b = @{ commanderOracleId = "smoke-oversize"; cardOracleIds = (1..41 | ForEach-Object { "id-$_" }) } | ConvertTo-Json
    # Windows PowerShell 5.1 throws WebException; pwsh 7 throws HttpResponseException.
    # Catch broadly and read the status off whichever it is.
    $accepted = $false
    try {
        Invoke-RestMethod "$Base/api/decks/synergy/batch" -Method Post -Body $b -ContentType "application/json" -Headers $H | Out-Null
        $accepted = $true
    } catch {
        $code = [int]$_.Exception.Response.StatusCode
        if ($code -ne 400) { throw "expected 400, got $code" }
    }
    if ($accepted) { throw "41 cards were accepted; the batch cap is not enforced" }
    "41 cards correctly rejected with 400"
}

Check "POST decks/synergy (cache hit)" {
    $b = @{ commanderOracleId="smoke-cache-fixed"; commanderName="Atraxa, Praetors' Voice"; commanderText="proliferate";
            cardOracleId="smoke-cache-fixed-card"; cardName="Doubling Season"; cardText="doubles counters";
            deckCardNames=@() } | ConvertTo-Json
    Invoke-RestMethod "$Base/api/decks/synergy" -Method Post -Body $b -ContentType "application/json" -Headers $H | Out-Null
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod "$Base/api/decks/synergy" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $sw2.Stop()
    if ($sw2.ElapsedMilliseconds -gt 500) { throw "2nd call took $($sw2.ElapsedMilliseconds)ms - cache likely not hit" }
    "2nd call $($sw2.ElapsedMilliseconds)ms (cached), score=$($r.score)"
}

Check "POST decks/suggestions (gameChangers populated)" {
    # Regression guard: this category returned 0 on every run because the prompt asked
    # for "high-impact cards" while the filter required Scryfall's official flag.
    $b = @{ commanderOracleId=$script:cmd.oracleId; commanderName=$script:cmd.name;
            commanderText=$script:cmdText; deckCardNames=@();
            deckTags=@(); suggestionTags=@() } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks/suggestions" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $gc = @($r.gameChangers)
    if ($gc.Count -eq 0) { throw "gameChangers empty - model picked cards outside the official list" }
    foreach ($c in $gc) {
        if (-not $c.card.gameChanger) { throw "'$($c.name)' returned but is not flagged GameChanger" }
    }
    "gameChangers=$($gc.Count) e.g. '$($gc[0].name)'"
}

Check "POST decks/suggestions (rejections are reported)" {
    # Silent rejection is what hid the gameChangers bug: a category emptied by
    # validation looked identical to one the model declined to fill.
    $b = @{ commanderOracleId=$script:cmd.oracleId; commanderName=$script:cmd.name;
            commanderText=$script:cmdText; deckCardNames=@("Sol Ring","Swamp");
            deckTags=@(); suggestionTags=@() } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks/suggestions" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $d = $r.diagnostics
    if ($null -eq $d) { throw "no diagnostics block returned" }
    if ($d.proposed -le 0) { throw "proposed=$($d.proposed) - model returned nothing" }
    if ($d.accepted -gt $d.proposed) { throw "accepted ($($d.accepted)) > proposed ($($d.proposed))" }

    $reasons = @()
    if ($d.rejected) { $reasons = @($d.rejected.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) }
    $sum = 0
    if ($d.rejected) { $d.rejected.PSObject.Properties | ForEach-Object { $sum += $_.Value } }

    "proposed=$($d.proposed) accepted=$($d.accepted) rejected[$($reasons -join ' ')]"
}

Check "POST decks/suggestions" {
    $b = @{ commanderOracleId=$script:cmd.oracleId; commanderName=$script:cmd.name;
            commanderText=$script:cmdText; deckCardNames=@(); deckTags=@(); suggestionTags=@("budget") } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks/suggestions" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $n = @($r.topSynergy).Count + @($r.gameChangers).Count + @($r.notableMentions).Count + @($r.latestSet).Count
    if ($n -eq 0) { throw "no suggestions returned" }
    "total=$n (synergy=$(@($r.topSynergy).Count) gc=$(@($r.gameChangers).Count))"
}

Check "POST decks/mana-tune" {
    $b = @{ format="commander"; deckCardNames=@("Sol Ring","Cultivate"); currentLands=34;
            recommendedLands=37; avgCmc=3.2; activeColors=@("G","W") } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/decks/mana-tune" -Method Post -Body $b -ContentType "application/json" -Headers $H
    if (@($r.advice).Count -eq 0) { throw "no advice returned" }
    "advice=$(@($r.advice).Count) lands=$(@($r.landSuggestions).Count)"
}

Check "POST decks/mana-tune (cache hit)" {
    # Identical request to the previous check -- must be served from the DB cache.
    $b = @{ format="commander"; deckCardNames=@("Sol Ring","Cultivate"); currentLands=34;
            recommendedLands=37; avgCmc=3.2; activeColors=@("G","W") } | ConvertTo-Json
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod "$Base/api/decks/mana-tune" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $sw2.Stop()
    if ($sw2.ElapsedMilliseconds -gt 500) { throw "2nd call took $($sw2.ElapsedMilliseconds)ms - cache not hit" }
    "2nd call $($sw2.ElapsedMilliseconds)ms (cached), advice=$(@($r.advice).Count)"
}

Check "POST decks/mana-tune (reordered deck still hits)" {
    # Same deck, different card order: the cache key sorts inputs, so this must hit.
    $b = @{ format="commander"; deckCardNames=@("Cultivate","Sol Ring"); currentLands=34;
            recommendedLands=37; avgCmc=3.2; activeColors=@("G","W") } | ConvertTo-Json
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod "$Base/api/decks/mana-tune" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $sw2.Stop()
    if ($sw2.ElapsedMilliseconds -gt 500) { throw "2nd call took $($sw2.ElapsedMilliseconds)ms - reorder broke the cache key" }
    "reordered $($sw2.ElapsedMilliseconds)ms (cached), advice=$(@($r.advice).Count)"
}

Check "POST decks/suggestions (cache hit)" {
    $b = @{ commanderOracleId=$script:cmd.oracleId; commanderName=$script:cmd.name;
            commanderText=$script:cmdText; deckCardNames=@("Sol Ring");
            deckTags=@(); suggestionTags=@() } | ConvertTo-Json
    Invoke-RestMethod "$Base/api/decks/suggestions" -Method Post -Body $b -ContentType "application/json" -Headers $H | Out-Null
    $sw2 = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod "$Base/api/decks/suggestions" -Method Post -Body $b -ContentType "application/json" -Headers $H
    $sw2.Stop()
    if ($sw2.ElapsedMilliseconds -gt 500) { throw "2nd call took $($sw2.ElapsedMilliseconds)ms - cache not hit" }
    $n = @($r.topSynergy).Count + @($r.gameChangers).Count + @($r.latestSet).Count + @($r.notableMentions).Count
    "2nd call $($sw2.ElapsedMilliseconds)ms (cached), total=$n"
}

Check "GET commanders/{id}/suggestions" {
    $r = Invoke-RestMethod "$Base/api/commanders/$($script:cmd.oracleId)/suggestions" -Headers $H
    $n = @($r.topSynergy).Count + @($r.gameChangers).Count
    if ($n -eq 0) { throw "no suggestions" }
    "total=$n"
}

Check "POST vision/identify-card" {
    # Use a real card image the API itself just handed us, so the URL is guaranteed valid.
    $img = Invoke-WebRequest $script:cmd.imageUri -UseBasicParsing
    $b64 = [Convert]::ToBase64String($img.Content)
    $b = @{ imageBase64=$b64; mediaType="image/jpeg" } | ConvertTo-Json
    $r = Invoke-RestMethod "$Base/api/vision/identify-card" -Method Post -Body $b -ContentType "application/json" -Headers $H

    if ($r.error) { throw "vision error: $($r.error)" }
    if (-not $r.cardName) { throw "no cardName returned" }
    if (-not $r.verified) { throw "card did not resolve against Scryfall" }
    if (-not $r.oracleId) { throw "no oracleId returned" }

    # Regression guard: setName used to be passed straight through from the model and
    # was wrong on every observed run. It must now either match a genuine printing of
    # the identified card, or be withheld entirely.
    if ($r.setName) {
        $printings = Invoke-RestMethod "$Base/api/cards/$($r.oracleId)/printings" -Headers $H
        $valid = @($printings | ForEach-Object { $_.setName })
        if ($valid -notcontains $r.setName) {
            throw "hallucinated set '$($r.setName)' - not a printing of '$($r.cardName)'"
        }
        "card='$($r.cardName)' set='$($r.setName)' (1 of $($valid.Count) real printings)"
    } else {
        "card='$($r.cardName)' set=null (correctly withheld)"
    }
}

if ($SkipExpensive) {
    Skip "POST decks/{id}/ai-build" "-SkipExpensive set"
    Skip "POST decks/{id}/ai-score" "-SkipExpensive set"
    Skip "POST decks/{id}/ai-refine" "-SkipExpensive set"
} else {
    Check "POST decks/{id}/ai-build (fills all 99 slots)" {
        $b = @{ commanderOracleId=$script:cmd.oracleId; bracket=2; priceRange="budget";
                includeSideboard=$false; includeMaybeboard=$false } | ConvertTo-Json
        $r = Invoke-RestMethod "$Base/api/decks/$($script:deckId)/ai-build" -Method Post -Body $b -ContentType "application/json" -Headers $H
        if ($r.cardsAdded -lt 1) { throw "no cards added (skipped=$($r.cardsSkipped))" }

        # skipped > 0 is healthy -- it means validation rejected hallucinated or
        # illegal names. What is NOT acceptable is those rejections leaving the deck
        # short: substitutes must backfill them.
        if ($r.mainShortfall -ne 0) {
            throw "deck is $($r.mainShortfall) cards short of $($r.mainTarget) - substitutes exhausted"
        }

        # Cross-check against what actually landed in the deck, not just the report.
        $detail = Invoke-RestMethod "$Base/api/decks/$($script:deckId)" -Headers $H
        $mainCount = (@($detail.cards | Where-Object { $_.board -eq 'main' }) | Measure-Object -Property quantity -Sum).Sum
        if ($mainCount -ne $r.mainTarget) {
            throw "deck holds $mainCount main cards but target was $($r.mainTarget)"
        }

        $reasons = @()
        if ($r.skippedByReason) { $reasons = @($r.skippedByReason.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) }
        "deck=$mainCount/$($r.mainTarget) skipped=$($r.cardsSkipped) [$($reasons -join ' ')]"
    }

    Check "POST decks/{id}/ai-score (every card scored + reasoned)" {
        $r = Invoke-RestMethod "$Base/api/decks/$($script:deckId)/ai-score" -Method Post -Headers $H
        $cards = @($r.cards)
        if ($cards.Count -eq 0) { throw "no cards scored" }
        if ($r.averageScore -le 0 -or $r.averageScore -gt 100) { throw "average out of range: $($r.averageScore)" }
        # A score without a reason is useless to the user — that's the whole point.
        $noReason = @($cards | Where-Object { -not $_.reason })
        if ($noReason.Count -gt 0) { throw "$($noReason.Count) cards scored with no reason" }
        $bad = @($cards | Where-Object { $_.score -lt 0 -or $_.score -gt 100 })
        if ($bad.Count -gt 0) { throw "$($bad.Count) scores out of 0-100 range" }
        $script:scoredAvg = $r.averageScore
        $script:scoredSample = $cards[0]
        "scored=$($cards.Count) avg=$($r.averageScore) weak=$(@($r.weakestCards).Count)"
    }

    Check "ai-score warms the single-card synergy cache" {
        # Batch scoring persists into the same table the per-card endpoint reads.
        # This silently failed once (EF could not translate string[].Contains on .NET 10)
        # and only a DB inspection caught it — so assert it through the API.
        if (-not $script:scoredSample) { throw "no scored card captured" }
        $b = @{ commanderOracleId=$script:cmd.oracleId; commanderName=$script:cmd.name;
                commanderText=$script:cmdText;
                cardOracleId=$script:scoredSample.oracleId; cardName=$script:scoredSample.name;
                cardText="x"; deckCardNames=@() } | ConvertTo-Json
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-RestMethod "$Base/api/decks/synergy" -Method Post -Body $b -ContentType "application/json" -Headers $H
        $sw.Stop()
        if ($sw.ElapsedMilliseconds -gt 500) {
            throw "took $($sw.ElapsedMilliseconds)ms - batch scores were not persisted"
        }
        if ($r.score -ne $script:scoredSample.score) {
            throw "cached score $($r.score) != batch score $($script:scoredSample.score)"
        }
        "'$($script:scoredSample.name)' served from cache in $($sw.ElapsedMilliseconds)ms, score=$($r.score)"
    }

    Check "POST decks/{id}/ai-refine (deck size preserved)" {
        $before = Invoke-RestMethod "$Base/api/decks/$($script:deckId)" -Headers $H
        $sizeBefore = (@($before.cards | Where-Object { $_.board -eq 'main' }) | Measure-Object -Property quantity -Sum).Sum

        $b = @{ bracket=2; priceRange="budget"; maxSwaps=5 } | ConvertTo-Json
        $r = Invoke-RestMethod "$Base/api/decks/$($script:deckId)/ai-refine" -Method Post -Body $b -ContentType "application/json" -Headers $H

        # The invariant that matters: refining must not silently shrink the deck.
        if ($r.deckSizeAfter -ne $r.deckSizeBefore) {
            throw "deck size changed $($r.deckSizeBefore) -> $($r.deckSizeAfter)"
        }
        $after = Invoke-RestMethod "$Base/api/decks/$($script:deckId)" -Headers $H
        $sizeAfter = (@($after.cards | Where-Object { $_.board -eq 'main' }) | Measure-Object -Property quantity -Sum).Sum
        if ($sizeAfter -ne $sizeBefore) { throw "persisted deck changed size $sizeBefore -> $sizeAfter" }

        $swaps = @($r.swaps)
        foreach ($s in $swaps) {
            if (-not $s.out -or -not $s.in) { throw "swap missing in/out" }
            if ($s.out -eq $s.in) { throw "swap replaced '$($s.out)' with itself" }
        }
        $rej = @()
        if ($r.rejectedByReason) { $rej = @($r.rejectedByReason.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) }
        $eg = if ($swaps.Count -gt 0) { " e.g. '$($swaps[0].out)'->'$($swaps[0].in)'" } else { "" }
        "swaps=$($swaps.Count) size=$sizeAfter (unchanged)$eg [$($rej -join ' ')]"
    }
}

Write-Host "`n=== CLEANUP ===" -ForegroundColor Cyan
Check "decks DELETE (cleanup)" {
    Invoke-RestMethod "$Base/api/decks/$($script:deckId)" -Method Delete -Headers $H | Out-Null
    "deleted"
}

Write-Host ""
Write-Host ("RESULT: {0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) `
    -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })
Write-Host "Note: leaves a smoke_* user and synergy cache rows in the local dev DB." -ForegroundColor DarkGray
if ($script:fail -gt 0) { exit 1 }
