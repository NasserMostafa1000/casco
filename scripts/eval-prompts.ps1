# Quality run with REAL AI (costs money): for every prompt in eval-prompts.json
#   generate -> preview -> RTL check -> 5 edits (+ layout checks) -> undo -> publish -> AI cost,
# and writes a results table (markdown + CSV) with preview links for the visual check, which stays manual.
# Run against a staging server with real AI keys and Turnstile test keys (it registers a test user):
#   powershell -File scripts/eval-prompts.ps1 -Base https://staging.example -AdminEmail you@x.com -AdminPassword ...
param(
    [string]$Base = "http://localhost:5080",
    [string]$AdminEmail = "admin@casco.local",
    [string]$AdminPassword = "AdminPass1!",
    [string]$PromptsFile = (Join-Path $PSScriptRoot "eval-prompts.json"),
    [string]$Only = "",
    [int]$MaxContinues = 3,
    [string]$Captcha = "XXXX.DUMMY.TOKEN.XXXX"
)
$ErrorActionPreference = "Stop"

function Call([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null) {
    $h = @{}
    if ($Token) { $h.Authorization = "Bearer $Token" }
    $req = @{ Method = $Method; Uri = "$Base$Path"; Headers = $h; ContentType = "application/json; charset=utf-8" }
    if ($null -ne $Body) { $req.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10)) }
    for ($i = 0; ; $i++) {
        try { return Invoke-RestMethod @req }
        catch {
            if ($i -ge 20 -or $_.Exception.Response -isnot [Net.HttpWebResponse] -or [int]$_.Exception.Response.StatusCode -ne 429) { throw }
            Start-Sleep 5
        }
    }
}

function WaitIdle([string]$ProjectId, [int]$Seconds = 1800) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $p = Call GET "/api/projects/$ProjectId" -Token $script:t
        if (-not $p.activeTask) { return $p }
        Start-Sleep 3
    }
    throw "Project $ProjectId still busy after $Seconds s"
}

$warning = [string][char]0x26A0
function LastReplyFailed($p) {
    $last = $p.messages | Where-Object { $_.role -eq "assistant" } | Select-Object -Last 1
    return (-not $last) -or $last.content.StartsWith($warning)
}

function CurrentFiles($p) { (Call GET "/api/projects/$($p.id)/versions/$($p.currentVersionId)" -Token $script:t).files }

# Big builds stop after the part limit; press "continue" like a user would.
function Continue-Build($p) {
    $n = 0
    while ($p.continuable -and $n -lt $MaxContinues) {
        Call POST "/api/projects/$($p.id)/messages" @{ content = ""; continue = $true } $script:t | Out-Null
        $p = WaitIdle $p.id
        $n++
    }
    return @{ Project = $p; Continues = $n }
}

# Checks from eval-prompts.json: shared-layout behaviour of multi-page sites.
function Test-Check([string]$Check, $Before, $After, $Version) {
    if (-not $Check) { return $null }
    $files = CurrentFiles $After
    $pages = @($files.PSObject.Properties | Where-Object { $_.Name -like "*.html" })
    $layout = $files.PSObject.Properties | Where-Object { $_.Name -eq "assets/layout.js" }
    switch -Regex ($Check) {
        "^brand:(.+)$" {
            $name = $Matches[1]
            if ($layout -and $layout.Value.Contains($name)) { return "ok (layout.js)" }
            $missing = @($pages | Where-Object { -not $_.Value.Contains($name) })
            if ($missing.Count -eq 0) { return "ok (all $($pages.Count) pages)" }
            return "FAIL: missing on $(@($missing | ForEach-Object Name) -join ', ')"
        }
        "^newPageUsesLayout$" {
            $new = @($pages | Where-Object { @($Before.files) -notcontains $_.Name })
            if ($new.Count -eq 0) { return "FAIL: no new page" }
            if (-not $layout) { return "no layout.js (site has $($pages.Count) pages)" }
            $bad = @($new | Where-Object { $_.Value -notmatch "assets/layout\.js" })
            if ($bad.Count -eq 0) { return "ok" } else { return "FAIL: $(@($bad | ForEach-Object Name) -join ', ') without layout.js" }
        }
        "^fewFiles:(\d+)$" {
            $limit = [int]$Matches[1]
            if ($Version.filesChanged -le $limit) { return "ok ($($Version.filesChanged) files)" }
            return "FAIL: $($Version.filesChanged) files changed (layout.js should hold the header)"
        }
    }
    return "unknown check"
}

$prompts = Get-Content -Raw -Encoding UTF8 $PromptsFile | ConvertFrom-Json
if ($Only) { $prompts = @($prompts | Where-Object { $Only.Split(",") -contains $_.key }) }

try { $admin = Call POST "/api/auth/login" @{ email = $AdminEmail; password = $AdminPassword } }
catch { $admin = Call POST "/api/auth/register" @{ name = "Admin"; email = $AdminEmail; password = $AdminPassword; turnstileToken = $Captcha } }
$at = $admin.token
$stamp = Get-Random
$user = Call POST "/api/auth/register" @{ name = "Eval"; email = "eval-$stamp@test.local"; password = "Passw0rd!"; turnstileToken = $Captcha }
$t = $user.token
$userId = (Call GET "/api/me" -Token $t).user.id
Call POST "/api/admin/users/$userId/activate" @{ interval = "monthly" } $at | Out-Null
Call POST "/api/admin/users/$userId/credits" @{ amount = 50000; note = "eval run" } $at | Out-Null

$results = @()
foreach ($case in $prompts) {
    Write-Host "== $($case.key): $($case.name)" -ForegroundColor Cyan
    $row = [ordered]@{ key = $case.key; build = "?"; pages = 0; continues = 0; preview = "?"; rtl = "?"; edits = "?"; checks = ""; undo = "?"; publish = "?"; cost = 0; buildS = 0; previewUrl = ""; notes = "" }
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $created = Call POST "/api/projects" @{ name = $case.name; description = $case.description } $t
        $p = WaitIdle $created.id
        $c = Continue-Build $p
        $p = $c.Project
        $row.continues = $c.Continues
        $row.buildS = [int]$sw.Elapsed.TotalSeconds
        $row.build = if (LastReplyFailed $p) { "FAIL" } else { "ok" }
        $row.pages = @($p.files | Where-Object { $_ -like "*.html" }).Count
        $row.previewUrl = "$($p.previewBase)index.html"

        $html = Invoke-WebRequest -UseBasicParsing $row.previewUrl
        $row.preview = if ($html.StatusCode -eq 200 -and $html.Content -match "</html>") { "ok" } else { "FAIL" }
        $row.rtl = if ($html.Content -match 'dir\s*=\s*"rtl"' -and $html.Content -match 'lang\s*=\s*"ar') { "ok" } else { "FAIL" }

        $okEdits = 0
        $checks = @()
        foreach ($edit in $case.edits) {
            $before = $p
            Call POST "/api/projects/$($p.id)/messages" @{ content = $edit.prompt } $t | Out-Null
            $p = (Continue-Build (WaitIdle $p.id)).Project
            $version = $p.versions | Select-Object -First 1
            $changed = $p.currentVersionId -ne $before.currentVersionId
            if ($changed -and -not (LastReplyFailed $p)) { $okEdits++ }
            elseif (-not $changed) { $row.notes += "no change: $($edit.prompt); " }
            if ($edit.check) { $checks += "$($edit.check) -> $(Test-Check $edit.check $before $p $version)" }
        }
        $row.edits = "$okEdits/$(@($case.edits).Count)"
        $row.checks = $checks -join " | "

        $previous = $p.versions[1]
        $restored = Call POST "/api/projects/$($p.id)/versions/$($previous.id)/restore" -Token $t
        $p = Call GET "/api/projects/$($p.id)" -Token $t
        $same = ((CurrentFiles $p) | ConvertTo-Json -Depth 5 -Compress) -eq ((Call GET "/api/projects/$($p.id)/versions/$($previous.id)" -Token $t).files | ConvertTo-Json -Depth 5 -Compress)
        $row.undo = if ($restored.versionId -eq $p.currentVersionId -and $same) { "ok" } else { "FAIL" }

        Call POST "/api/admin/projects/$($p.id)/hosting" @{ tier = $p.requiredTier; months = 1 } $at | Out-Null
        $pub = Call POST "/api/projects/$($p.id)/publish" -Token $t
        $site = try { (Invoke-WebRequest -UseBasicParsing $pub.url).StatusCode } catch { 0 }
        if ($site -ne 200) {
            $hostName = ([Uri]$pub.url).Host
            $site = [int](& curl.exe -s -o NUL -w "%{http_code}" -H "Host: $hostName" "$Base/")
        }
        $row.publish = if ($site -eq 200) { "ok" } else { "FAIL ($site)" }

        $sites = (Call GET "/api/admin/users/$userId" -Token $at).sites
        $row.cost = [Math]::Round((($sites | Where-Object { $_.id -eq $p.id }).aiCost), 4)
        if ($row.cost -eq 0) { $row.notes += "cost 0: answered from the AI response cache (same prompts run in the last 7 days); " }
    }
    catch { $row.notes += "ERROR: $($_.Exception.Message)" }
    $results += [pscustomobject]$row
    Write-Host ("  build={0} pages={1} preview={2} rtl={3} edits={4} undo={5} publish={6} cost=`${7}" -f $row.build, $row.pages, $row.preview, $row.rtl, $row.edits, $row.undo, $row.publish, $row.cost)
}

$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $outDir | Out-Null
$name = "eval-{0:yyyyMMdd-HHmm}" -f (Get-Date)
$results | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $outDir "$name.csv")

$md = @("| Project | Build | Pages | Preview | RTL | Visual | Edits | Checks | Undo | Publish | Cost | Build time | Notes |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|")
foreach ($r in $results) {
    $md += "| [$($r.key)]($($r.previewUrl)) | $($r.build) | $($r.pages) | $($r.preview) | $($r.rtl) | ? | $($r.edits) | $($r.checks) | $($r.undo) | $($r.publish) | `$$($r.cost) | $($r.buildS)s | $($r.notes) |"
}
$total = ($results | Measure-Object cost -Sum).Sum
$md += ""
$md += "Total AI cost: `$$([Math]::Round($total, 4)) for $($results.Count) sites. Fill the Visual column by opening each preview link (desktop + mobile)."
[IO.File]::WriteAllLines((Join-Path $outDir "$name.md"), $md, (New-Object Text.UTF8Encoding($true)))
$md | Write-Host
Write-Host "Saved $(Join-Path $outDir "$name.md")"
