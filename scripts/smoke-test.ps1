# End-to-end smoke test against a running API (Development, fake AI).
# Usage: pwsh scripts/smoke-test.ps1 [-Base http://localhost:5080]
param([string]$Base = "http://localhost:5080")
$ErrorActionPreference = "Stop"
$failures = 0

function Call([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Headers = @{}) {
    $h = @{} + $Headers
    if ($Token) { $h.Authorization = "Bearer $Token" }
    $req = @{ Method = $Method; Uri = "$Base$Path"; Headers = $h; ContentType = "application/json; charset=utf-8" }
    if ($null -ne $Body) { $req.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10)) }
    # The run makes more AI requests than the per-minute limit allows; wait out 429s instead of failing.
    for ($i = 0; ; $i++) {
        try { return Invoke-RestMethod @req }
        catch {
            if ($i -ge 20 -or $_.Exception.Response -isnot [Net.HttpWebResponse] -or [int]$_.Exception.Response.StatusCode -ne 429) { throw }
            Start-Sleep 5
        }
    }
}

function CallStatus([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null) {
    try { Call $Method $Path $Body $Token | Out-Null; return 200 }
    catch { return [int]$_.Exception.Response.StatusCode }
}

# Returns @{ status; code; details } for a request that is expected to fail.
function CallError([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Headers = @{}) {
    try { Call $Method $Path $Body $Token $Headers | Out-Null; return @{ status = 200 } }
    catch {
        $err = $null
        $raw = $_.ErrorDetails.Message
        if (-not $raw -and $_.Exception.Response -is [Net.HttpWebResponse]) {
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                if ($stream.CanSeek) { $stream.Position = 0 }
                $raw = (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd()
            } catch { }
        }
        try { $err = $raw | ConvertFrom-Json } catch { }
        return @{ status = [int]$_.Exception.Response.StatusCode; code = $err.code; details = $err.details; message = $err.message }
    }
}

function Check([string]$Name, [bool]$Ok, $Detail = "") {
    if ($Ok) { Write-Host "  PASS $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL $Name $Detail" -ForegroundColor Red; $script:failures++ }
}

function WaitTask([string]$ProjectId, [string]$Token) {
    for ($i = 0; $i -lt 60; $i++) {
        $p = Call GET "/api/projects/$ProjectId" -Token $Token
        if (-not $p.activeTask) { return $p }
        Start-Sleep -Milliseconds 500
    }
    throw "Task did not finish in time"
}

function HostGet([string]$HostName, [string]$Path = "/") {
    (& curl.exe -s -H "Host: $HostName" "$Base$Path") -join "`n"
}

function HostStatus([string]$HostName, [string]$Path) {
    & curl.exe -s -o NUL -w "%{http_code}" -H "Host: $HostName" "$Base$Path"
}

$stamp = Get-Random
Write-Host "== Health" -ForegroundColor Cyan
Check "health" ((Call GET "/health").status -eq "ok")

# Cloudflare's always-pass test token (Development uses Turnstile test keys).
$captcha = "XXXX.DUMMY.TOKEN.XXXX"

Write-Host "== Auth: bot check + Google/Apple" -ForegroundColor Cyan
$authCfg = Call GET "/api/auth/config"
Check "auth config exposes Turnstile site key" ([bool]$authCfg.turnstileSiteKey)
Check "register without Turnstile token rejected" ((CallStatus POST "/api/auth/register" @{ name = "Bot"; email = "bot$stamp@test.local"; password = "Passw0rd!" }) -eq 400)
if ($authCfg.googleClientId) {
    Check "forged Google token rejected (401)" ((CallStatus POST "/api/auth/external" @{ provider = "google"; idToken = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.x" }) -eq 401)
}
if ($authCfg.appleClientId) {
    Check "forged Apple token rejected (401)" ((CallStatus POST "/api/auth/external" @{ provider = "apple"; idToken = "not-a-jwt" }) -eq 401)
}
Check "unknown login provider rejected" ((CallStatus POST "/api/auth/external" @{ provider = "facebook"; idToken = "x" }) -eq 400)

Write-Host "== Free user: guaranteed website" -ForegroundColor Cyan
$owner = Call POST "/api/auth/register" @{ name = "Owner"; email = "owner$stamp@test.local"; password = "Passw0rd!"; turnstileToken = $captcha }
$t = $owner.token
$me = Call GET "/api/me" -Token $t
Check "free plan on signup" ($me.plan.key -eq "free")
Check "signup bonus credits" ($me.credits.available -eq 50) $me.credits.available

$ownerId = $me.user.id
$created = Call POST "/api/projects" @{ name = "Coffee House $stamp"; description = "Landing page for a coffee shop in Dubai" } $t
$p = WaitTask $created.id $t
Check "site has a version" ($p.versions.Count -ge 1) $p.versions.Count
Check "free generation succeeded (v2)" ($p.versions.Count -ge 2) $p.versions.Count
Check "landing page needs only static hosting" ($p.requiredTier -eq "static") $p.requiredTier
$me = Call GET "/api/me" -Token $t
Check "free generation cost no credits" ($me.credits.available -eq 50) $me.credits.available
Check "free site marked used" ($me.user.freeSiteUsed -eq $true)
Check "second project blocked on free plan (402)" ((CallStatus POST "/api/projects" @{ name = "Second site"; description = "Another landing page" } $t) -eq 402)

$preview = Invoke-WebRequest -UseBasicParsing "$($p.previewBase)index.html"
Check "preview serves html with SDK" ($preview.Content -match "__CASCO__" -and $preview.Content -match "sdk/casco.js")
Check "preview is CSP-sandboxed" ("$($preview.Headers['Content-Security-Policy'])" -match "^sandbox")

Write-Host "== Free plan limits: edits need Pro, publishing needs hosting" -ForegroundColor Cyan
$e = CallError POST "/api/projects/$($p.id)/messages" @{ content = "Add a testimonials section" } $t
Check "free user cannot edit (402 pro_required_edit)" ($e.status -eq 402 -and $e.code -eq "pro_required_edit") "$($e.status) $($e.code)"
$e = CallError POST "/api/projects/$($p.id)/publish" -Token $t
Check "publish without hosting (402 hosting_required)" ($e.status -eq 402 -and $e.code -eq "hosting_required") "$($e.status) $($e.code)"
Check "hosting error names the static tier" ($e.details.requiredTier -eq "static") $e.details.requiredTier
$hosting = Call GET "/api/projects/$($p.id)/hosting" -Token $t
Check "hosting status: not active" ($hosting.active -eq $false -and $hosting.prices.static.monthly -eq 5 -and $hosting.prices.backend.monthly -eq 10)
$code = & curl.exe -s -o NUL -w "%{http_code}" -H "Authorization: Bearer $t" "$Base/api/projects/$($p.id)/export"
Check "free user cannot download code (402)" ($code -eq "402") $code
Check "unknown host 404s" ((& curl.exe -s -o NUL -w "%{http_code}" -H "Host: nope-$stamp.localhost" "$Base/") -eq "404")

Write-Host "== Image uploads" -ForegroundColor Cyan
$png = Join-Path $env:TEMP "casco-smoke.png"
[IO.File]::WriteAllBytes($png, [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="))
$up = (& curl.exe -s -H "Authorization: Bearer $t" -F "file=@$png;type=image/png" "$Base/api/projects/$($p.id)/images") | ConvertFrom-Json
Check "owner image uploaded" ($up.url -match "/u/.+/o-.+\.png$") $up.url
$txt = Join-Path $env:TEMP "casco-smoke.txt"; Set-Content $txt "not an image"
$bad = & curl.exe -s -o NUL -w "%{http_code}" -H "Authorization: Bearer $t" -F "file=@$txt;type=image/png" "$Base/api/projects/$($p.id)/images"
Check "non-image rejected" ($bad -eq "400") $bad
$lib = Call GET "/api/projects/$($p.id)/images" -Token $t
Check "image library lists upload" (@($lib) -contains $up.url)

Write-Host "== Site runtime: forms" -ForegroundColor Cyan
Call POST "/api/s/$($p.siteKey)/forms/contact" @{ name = "Ali"; email = "ali@test.local"; message = "Hello" } | Out-Null
$subs = Call GET "/api/projects/$($p.id)/data/submissions" -Token $t
Check "form submission stored" ($subs.total -eq 1) $subs.total
Call POST "/api/s/$($p.siteKey)/forms/contact" @{ name = "Bot"; _hp = "spam" } | Out-Null
$subs = Call GET "/api/projects/$($p.id)/data/submissions" -Token $t
Check "honeypot drops spam" ($subs.total -eq 1) $subs.total

Write-Host "== Admin + Pro activation" -ForegroundColor Cyan
$adminEmail = "admin@casco.local"
try { $admin = Call POST "/api/auth/register" @{ name = "Admin"; email = $adminEmail; password = "AdminPass1!"; turnstileToken = $captcha } }
catch { $admin = Call POST "/api/auth/login" @{ email = $adminEmail; password = "AdminPass1!" } }
$at = $admin.token
Check "non-admin blocked from admin API" ((CallStatus GET "/api/admin/stats" -Token $t) -eq 403)
$stats = Call GET "/api/admin/stats" -Token $at
Check "admin stats" ($stats.users -ge 2)
$ai = Call GET "/api/admin/ai" -Token $at
Check "admin ai config" ($ai.tiers.cheap.Count -ge 1)
Call PUT "/api/admin/ai/tiers" @{ tiers = @{ cheap = @("gemini-3.1-flash-lite", "gpt-6-luna"); standard = $ai.tiers.standard; premium = $ai.tiers.premium } } $at | Out-Null
$ai2 = Call GET "/api/admin/ai" -Token $at
Check "model switch saved" ($ai2.tiers.cheap[0] -eq "gemini-3.1-flash-lite")
Check "unknown model rejected" ((CallStatus PUT "/api/admin/ai/tiers" @{ tiers = @{ cheap = @("nope-model"); standard = $ai.tiers.standard; premium = $ai.tiers.premium } } $at) -eq 400)
Call DELETE "/api/admin/ai/tiers" -Token $at | Out-Null
Call POST "/api/admin/users/$ownerId/activate" @{ interval = "monthly" } $at | Out-Null
$me = Call GET "/api/me" -Token $t
Check "pro activated" ($me.plan.isPro -eq $true)
Check "monthly credits granted" ($me.credits.plan -eq 5000) $me.credits.plan

Write-Host "== Chat edit (Pro, paid from credits)" -ForegroundColor Cyan
$creditsBefore = $me.credits.available
Call POST "/api/projects/$($p.id)/messages" @{ content = "Add a testimonials section" } $t | Out-Null
$p = WaitTask $p.id $t
Check "edit created a new version" ($p.versions.Count -ge 3) $p.versions.Count
$me = Call GET "/api/me" -Token $t
Check "edit charged credits" ($me.credits.available -lt $creditsBefore) $me.credits.available
Check "no credits left reserved" ($me.credits.reserved -eq 0) $me.credits.reserved
Call POST "/api/projects/$($p.id)/versions/$($p.versions[-1].id)/restore" -Token $t | Out-Null
$p = Call GET "/api/projects/$($p.id)" -Token $t
Check "restore creates a new version from v1" ($p.versions.Count -ge 4) $p.versions.Count
$versionsBefore = $p.versions.Count
Call POST "/api/projects/$($p.id)/messages" @{ content = "Update the pricing page #demo-read" } $t | Out-Null
$p = WaitTask $p.id $t
$reply = $p.messages | Where-Object { $_.role -eq "assistant" } | Select-Object -Last 1
Check "model can read files from the site map, then edit" ($p.versions.Count -eq $versionsBefore + 1 -and $reply.content -match [regex]::Unescape("\u0628\u0639\u062f \u0642\u0631\u0627\u0621\u0629")) $reply.content

Write-Host "== Live code stream" -ForegroundColor Cyan
$task = Call POST "/api/projects/$($p.id)/messages" @{ content = "Make the hero title bigger" } $t
$sse = (& curl.exe -s -N --max-time 30 -H "Authorization: Bearer $t" "$Base/api/tasks/$($task.taskId)/stream") -join "`n"
Check "stream ends with done event" ($sse -match '"type":"done"') $sse.Substring(0, [Math]::Min(200, $sse.Length))
Check "stream carried model output" ($sse -match '"type":"delta"' -or $sse -match 'summary')
$p = WaitTask $p.id $t
Check "unknown user cannot read stream" ((& curl.exe -s -o NUL -w "%{http_code}" "$Base/api/tasks/$($task.taskId)/stream") -eq "401")

Write-Host "== Images in chat" -ForegroundColor Cyan
Check "foreign image url rejected" ((CallStatus POST "/api/projects/$($p.id)/messages" @{ content = "use it"; images = @("https://evil.test/x.png") } $t) -eq 400)
Call POST "/api/projects/$($p.id)/messages" @{ content = "Put this photo in the hero"; images = @($up.url) } $t | Out-Null
$p = WaitTask $p.id $t
$userMsg = $p.messages | Where-Object { $_.role -eq "user" } | Select-Object -Last 1
Check "chat message keeps attached image" (@($userMsg.images) -contains $up.url)

Write-Host "== Big requests are built in parts" -ForegroundColor Cyan
$versionsBefore = $p.versions.Count
Check "continue with nothing unfinished is rejected" ((CallStatus POST "/api/projects/$($p.id)/messages" @{ content = ""; continue = $true } $t) -eq 400)
Call POST "/api/projects/$($p.id)/messages" @{ content = "Build five inner pages #demo-pages-5" } $t | Out-Null
$p = WaitTask $p.id $t
$pages = @(1..5 | Where-Object { @($p.files) -contains "page-$_.html" })
Check "5-page request finished across parts" ($pages.Count -eq 5) (@($p.files) -join ",")
Check "one version for the whole build" ($p.versions.Count -eq $versionsBefore + 1) "$versionsBefore -> $($p.versions.Count)"
$v = $p.versions[0]
Check "version records prompt, parts, files and model" ($v.prompt -eq "Build five inner pages #demo-pages-5" -and $v.parts -eq 3 -and $v.filesChanged -eq 6 -and $v.model) ($v | ConvertTo-Json -Compress)
Check "finished build is not continuable" ($null -eq $p.continuable)
$reply = ($p.messages | Where-Object { $_.role -eq "assistant" } | Select-Object -Last 1).content
Check "reply lists the parts" ($reply -match "\u0627\u0644\u062C\u0632\u0621 3:") $reply.Substring(0, [Math]::Min(120, $reply.Length))
$me = Call GET "/api/me" -Token $t
Check "multi-part build leaves nothing reserved" ($me.credits.reserved -eq 0) $me.credits.reserved
Call POST "/api/projects/$($p.id)/messages" @{ content = "Build a huge site #demo-pages-40" } $t | Out-Null
$p = WaitTask $p.id $t
$built = @(1..40 | Where-Object { @($p.files) -contains "page-$_.html" }).Count
Check "huge request stops after the part limit" ($built -eq 21) $built
$reply = ($p.messages | Where-Object { $_.role -eq "assistant" } | Select-Object -Last 1).content
Check "reply says what is left and how to continue" ($reply -match "page-40" -and $reply -match "\u0643\u0645\u0644") $reply.Substring([Math]::Max(0, $reply.Length - 200))
Check "reply counts pages built of the total" ($reply -match "16 \u0645\u0646 35") $reply.Substring([Math]::Max(0, $reply.Length - 300))
Check "editor offers continue with pages left" ($p.continuable.pagesLeft -eq 19) ($p.continuable | ConvertTo-Json -Compress)
Call POST "/api/projects/$($p.id)/messages" @{ content = ""; continue = $true } $t | Out-Null
$p = WaitTask $p.id $t
$built = @(1..40 | Where-Object { @($p.files) -contains "page-$_.html" }).Count
Check "continue button resumes from where it stopped" ($built -eq 37 -and $p.continuable.pagesLeft -eq 3) "$built pages, continuable=$($p.continuable | ConvertTo-Json -Compress)"
$reply = ($p.messages | Where-Object { $_.role -eq "assistant" } | Select-Object -Last 1).content
Check "progress counts the whole build" ($reply -match "32 \u0645\u0646 35") $reply.Substring([Math]::Max(0, $reply.Length - 300))
Call POST "/api/projects/$($p.id)/messages" @{ content = [regex]::Unescape("\u0643\u0645\u0644") } $t | Out-Null
$p = WaitTask $p.id $t
$built = @(1..40 | Where-Object { @($p.files) -contains "page-$_.html" }).Count
Check "typing the continue word finishes the original request" ($built -eq 40 -and $null -eq $p.continuable) "$built pages, continuable=$($p.continuable | ConvertTo-Json -Compress)"
Check "continued version names the original request" ($p.versions[0].prompt -match "#demo-pages-40$" -and $p.versions[0].prompt.StartsWith([string][char]0x0643)) $p.versions[0].prompt

Write-Host "== Provider rate limits are retried for free" -ForegroundColor Cyan
$before = (Call GET "/api/me" -Token $t).credits.available
Call POST "/api/projects/$($p.id)/messages" @{ content = "Add a small banner #demo-flaky" } $t | Out-Null
$p = WaitTask $p.id $t
$reply = ($p.messages | Where-Object { $_.role -eq "assistant" } | Select-Object -Last 1)
Check "request succeeded after two 429s" (-not $reply.content.StartsWith([string][char]0x26A0)) $reply.content
$me = Call GET "/api/me" -Token $t
Check "charged once, for the successful call only" (($before - $me.credits.available) -eq $reply.credits -and $me.credits.reserved -eq 0) "$before -> $($me.credits.available), message $($reply.credits)"

Write-Host "== Static hosting + publish" -ForegroundColor Cyan
Check "Pro alone does not publish (402)" ((CallError POST "/api/projects/$($p.id)/publish" -Token $t).code -eq "hosting_required")
Check "non-admin cannot grant hosting" ((CallStatus POST "/api/admin/projects/$($p.id)/hosting" @{ tier = "static"; months = 1 } $t) -eq 403)
Call POST "/api/admin/projects/$($p.id)/hosting" @{ tier = "static"; months = 1 } $at | Out-Null
$hosting = Call GET "/api/projects/$($p.id)/hosting" -Token $t
Check "static hosting active" ($hosting.active -eq $true -and $hosting.tier -eq "static" -and $hosting.daysLeft -ge 28) "$($hosting.tier) $($hosting.daysLeft)"
Call POST "/api/projects/$($p.id)/publish" -Token $t | Out-Null
$site = HostGet "$($p.slug).localhost"
Check "published site served on subdomain" ($site -match "<html")
Check "no Casco badge for Pro" (-not ($site -match "Casco</a>"))
Check "uploaded image used on published site" ($site -match [regex]::Escape($up.url))

Write-Host "== Managed backend: collections + server functions" -ForegroundColor Cyan
Call POST "/api/projects/$($p.id)/messages" @{ content = "Add customer reviews with approval #demo-backend" } $t | Out-Null
$p = WaitTask $p.id $t
Check "backend files written" (@($p.files) -contains "casco.backend.json" -and @($p.files) -contains "server/functions.js") (@($p.files) -join ",")
Check "site now needs backend hosting" ($p.requiredTier -eq "backend") $p.requiredTier
Check "features detected (db)" (@($p.features) -contains "db") (@($p.features) -join ",")
Check "preview hides server code" ((& curl.exe -s -o NUL -w "%{http_code}" "$($p.previewBase)server/functions.js") -eq "404")
$e = CallError POST "/api/projects/$($p.id)/publish" -Token $t
Check "static hosting cannot publish a backend site" ($e.code -eq "hosting_required" -and $e.details.requiredTier -eq "backend") "$($e.code) $($e.details.requiredTier)"
Call POST "/api/admin/projects/$($p.id)/hosting" @{ tier = "backend"; months = 1 } $at | Out-Null
Call POST "/api/projects/$($p.id)/publish" -Token $t | Out-Null
$hostName = "$($p.slug).localhost"
Check "published site hides casco.backend.json" ((HostStatus $hostName "/casco.backend.json") -eq "404")
Check "published site hides server/functions.js" ((HostStatus $hostName "/server/functions.js") -eq "404")

$sk = $p.siteKey
$review = Call POST "/api/s/$sk/db/reviews" @{ name = "Mona"; rating = 5; phone = "+971500000001" }
Check "visitor created a review" ([bool]$review.id)
Check "visitor cannot set readonly field" ((CallError POST "/api/s/$sk/db/reviews" @{ name = "X"; rating = 5; approved = $true }).status -eq 400)
$list = Call GET "/api/s/$sk/db/reviews"
Check "unapproved review hidden (readWhere)" ($list.total -eq 0) $list.total
Check "visitors cannot delete (admin only)" ((CallStatus DELETE "/api/s/$sk/db/reviews/$($review.id)") -eq 403)
$cols = Call GET "/api/projects/$($p.id)/data/collections" -Token $t
Check "dashboard lists collections" (@($cols | Where-Object { $_.name -eq "reviews" }).Count -eq 1)
$rows = Call GET "/api/projects/$($p.id)/data/collections/reviews" -Token $t
Check "owner sees review with private phone" ($rows.total -eq 1 -and $rows.items[0].phone -eq "+971500000001")
Call PATCH "/api/projects/$($p.id)/data/collections/reviews/$($review.id)" @{ approved = $true } $t | Out-Null
$list = Call GET "/api/s/$sk/db/reviews"
Check "approved review is public" ($list.total -eq 1) $list.total
Check "private phone hidden from visitors" (-not $list.items[0].phone)
$where = [uri]::EscapeDataString('{"rating":{"gte":4}}')
Check "where filter works" ((Call GET "/api/s/$sk/db/reviews?where=$where").total -eq 1)
Check "filtering by private field rejected" ((CallStatus GET "/api/s/$sk/db/reviews?where=$([uri]::EscapeDataString('{"phone":"x"}'))") -eq 400)
Check "undeclared collection 404s" ((CallStatus GET "/api/s/$sk/db/secrets") -eq 404)

$fn = Call POST "/api/s/$sk/fn/stats" @{ name = "Ali" }
Check "server function ran with db access" ($fn.result.count -eq 1 -and $fn.result.average -eq 5 -and $fn.result.hello -eq "Ali") ($fn.result | ConvertTo-Json -Compress)
$e = CallError POST "/api/s/$sk/fn/spin" @{}
Check "infinite loop stopped (function_limit)" ($e.status -eq 400 -and $e.code -eq "function_limit") "$($e.status) $($e.code)"
Check "undeclared function 404s" ((CallStatus POST "/api/s/$sk/fn/nope" @{}) -eq 404)

Write-Host "== Admin suspension (independent of paid hosting)" -ForegroundColor Cyan
Check "non-admin cannot suspend" ((CallStatus POST "/api/admin/projects/$($p.id)/suspend" @{ reason = "test reason" } $t) -eq 403)
Check "suspension needs a reason" ((CallStatus POST "/api/admin/projects/$($p.id)/suspend" @{ reason = " " } $at) -eq 400)
Call POST "/api/admin/projects/$($p.id)/suspend" @{ reason = "Selling prohibited items" } $at | Out-Null
Check "suspended paid site shows paused page (503)" ((HostStatus $hostName "/") -eq "503")
$e = CallError GET "/api/s/$sk/db/reviews"
Check "public API refused (site_suspended)" ($e.status -eq 403 -and $e.code -eq "site_suspended") "$($e.status) $($e.code)"
Check "visitors do not see the reason" (-not ($e.message -match "prohibited")) $e.message
$e = CallError GET "/api/s/$sk/db/reviews" -Headers @{ "X-Casco-Preview" = "1" }
Check "preview header does not bypass suspension" ($e.code -eq "site_suspended") $e.code
$ps = Call GET "/api/projects/$($p.id)" -Token $t
Check "owner sees the reason" ($ps.suspension.reason -eq "Selling prohibited items")
Check "dashboard list marks it suspended" (@(Call GET "/api/projects" -Token $t | Where-Object { $_.id -eq $p.id -and $_.suspended }).Count -eq 1)
$e = CallError POST "/api/projects/$($p.id)/publish" -Token $t
Check "owner cannot publish while suspended" ($e.status -eq 403 -and $e.code -eq "site_suspended" -and $e.message -match "prohibited") "$($e.status) $($e.code)"
$e = CallError POST "/api/billing/checkout" @{ kind = "hosting"; projectId = $p.id; interval = "monthly" } $t
Check "owner cannot buy hosting while suspended" ($e.code -eq "site_suspended") "$($e.status) $($e.code)"
$hosting = Call GET "/api/projects/$($p.id)/hosting" -Token $t
Check "paid hosting untouched by suspension" ($hosting.active -eq $true -and $hosting.tier -eq "backend" -and $hosting.suspended -eq $true) "$($hosting.active) $($hosting.suspended)"
$su = Call GET "/api/admin/users?filter=suspended&search=owner$stamp" -Token $at
Check "admin filter lists suspended owner" ($su.total -eq 1 -and $su.items[0].suspendedSites -eq 1) "$($su.total)"
$ud = Call GET "/api/admin/users/$ownerId" -Token $at
$suspendedSite = $ud.sites | Where-Object { $_.id -eq $p.id }
Check "admin sees who suspended and why" ($suspendedSite.suspension.reason -eq "Selling prohibited items" -and $suspendedSite.suspension.by -eq $adminEmail)
Call POST "/api/admin/projects/$($p.id)/unsuspend" -Token $at | Out-Null
Check "unsuspended site served again" ((HostStatus $hostName "/") -eq "200")
Check "public API works again" ((CallStatus GET "/api/s/$sk/db/reviews") -eq 200)
Check "owner banner cleared" ($null -eq (Call GET "/api/projects/$($p.id)" -Token $t).suspension)

Write-Host "== Store: products, cart orders (no online payment)" -ForegroundColor Cyan
$sp = Call POST "/api/projects" @{ name = "Oud Shop"; description = "Perfume store with cash on delivery"; templateKey = "store" } $t
$sp = WaitTask $sp.id $t
Check "store template used" ($sp.templateKey -eq "store")

Write-Host "== Site name (subdomain) chosen on publish" -ForegroundColor Cyan
Check "project exposes the site suffix" ($sp.siteSuffix -eq ".localhost:5080") $sp.siteSuffix
$free = Call GET "/api/projects/$($sp.id)/slug/check?slug=oud-house-$stamp" -Token $t
Check "free name is available" ($free.available -and $free.url -eq "http://oud-house-$stamp.localhost:5080") ($free | ConvertTo-Json -Compress)
$taken = Call GET "/api/projects/$($sp.id)/slug/check?slug=$($p.slug)" -Token $t
Check "name used by another site is taken, with a suggestion" (-not $taken.available -and $taken.code -eq "slug_taken" -and $taken.suggestion -like "$($p.slug)-*") ($taken | ConvertTo-Json -Compress)
$own = Call GET "/api/projects/$($p.id)/slug/check?slug=$($p.slug)" -Token $t
Check "a site's own name counts as available" ($own.available)
Check "reserved name refused" ((Call GET "/api/projects/$($sp.id)/slug/check?slug=www" -Token $t).code -eq "slug_reserved")
Check "too short name refused" ((Call GET "/api/projects/$($sp.id)/slug/check?slug=ab" -Token $t).code -eq "slug_invalid")
Check "other users cannot check names on my site" ((CallStatus GET "/api/projects/$($sp.id)/slug/check?slug=abc" $null $at) -eq 404)
Check "taken name cannot be saved (409)" ((CallStatus PUT "/api/projects/$($sp.id)/slug" @{ slug = $p.slug } $t) -eq 409)
Call PUT "/api/projects/$($sp.id)/slug" @{ slug = "oud-house-$stamp" } $t | Out-Null
$sp = Call GET "/api/projects/$($sp.id)" -Token $t
Check "chosen name saved before publishing" ($sp.slug -eq "oud-house-$stamp" -and $sp.subdomainUrl -eq "http://oud-house-$stamp.localhost:5080") $sp.subdomainUrl
Check "store needs backend hosting" ($sp.requiredTier -eq "backend") $sp.requiredTier
Check "store feature detected" (@($sp.features) -contains "store") (@($sp.features) -join ",")
$settings = Call GET "/api/projects/$($sp.id)/data/settings" -Token $t
Check "online payment explained with support number" ($settings.onlinePayment.available -eq $false -and $settings.onlinePayment.supportWhatsApp -eq "+971569166263")
Check "bad whatsapp number rejected" ((CallStatus PUT "/api/projects/$($sp.id)/data/settings" @{ settings = @{ whatsApp = "123" } } $t) -eq 400)
Call PUT "/api/projects/$($sp.id)/data/settings" @{ settings = @{ whatsApp = "+971 50 123 4567"; currency = "aed"; shippingFee = 15; freeShippingOver = 200; paymentMethods = @("cod", "whatsapp", "transfer", "card"); bankDetails = "IBAN AE00" } } $t | Out-Null
$settings = Call GET "/api/projects/$($sp.id)/data/settings" -Token $t
Check "settings sanitized" ($settings.settings.whatsApp -eq "971501234567" -and $settings.settings.currency -eq "AED" -and -not (@($settings.settings.paymentMethods) -contains "card")) ($settings.settings | ConvertTo-Json -Compress -Depth 5)

$prod = Call POST "/api/projects/$($sp.id)/data/products" @{ name = "Oud Royal"; price = 120; category = "oud"; stock = 2; images = @() } $t
Call POST "/api/projects/$($sp.id)/data/products" @{ name = "Hidden"; price = 1; isActive = $false } $t | Out-Null
$ssk = $sp.siteKey
$products = Call GET "/api/s/$ssk/store/products"
Check "only active products listed" (@($products.items).Count -eq 1) @($products.items).Count
Check "cod order needs an address" ((CallStatus POST "/api/s/$ssk/store/orders" @{ customerName = "Sara"; phone = "0501112233"; paymentMethod = "cod"; items = @(@{ productId = $prod.id; quantity = 1 }) }) -eq 400)
$order = Call POST "/api/s/$ssk/store/orders" @{ customerName = "Sara"; phone = "0501112233"; address = "Dubai Marina"; paymentMethod = "cod"; items = @(@{ productId = $prod.id; quantity = 1 }) }
Check "order placed with server prices" ($order.number -eq 1001 -and $order.subtotal -eq 120 -and $order.shippingFee -eq 15 -and $order.total -eq 135) ($order | ConvertTo-Json -Compress)
Check "order has WhatsApp link to owner" ($order.whatsappUrl -match "wa\.me/971501234567") $order.whatsappUrl
$e = CallError POST "/api/s/$ssk/store/orders" @{ customerName = "Omar"; phone = "0501112244"; paymentMethod = "transfer"; items = @(@{ productId = $prod.id; quantity = 5 }) }
Check "stock enforced (out_of_stock)" ($e.code -eq "out_of_stock") "$($e.status) $($e.code)"
$orders = Call GET "/api/projects/$($sp.id)/data/orders" -Token $t
Check "owner sees the order" ($orders.total -eq 1 -and $orders.newCount -eq 1)
function ProductStock { $all = Call GET "/api/projects/$($sp.id)/data/products" -Token $t; ($all | Where-Object { $_.id -eq $prod.id }).stock }
$stock = ProductStock
Check "stock decremented" ($stock -eq 1) $stock
Call POST "/api/projects/$($sp.id)/data/orders/$($order.id)/status" @{ status = "canceled" } $t | Out-Null
$stock = ProductStock
Check "canceling restocks" ($stock -eq 2) $stock

Write-Host "== Bookings: services, slots, double-booking" -ForegroundColor Cyan
$svc = Call POST "/api/projects/$($sp.id)/data/booking-services" @{ name = "Consultation"; durationMinutes = 60; price = 100 } $t
$services = Call GET "/api/s/$ssk/bookings/services"
Check "public services listed" (@($services.items).Count -eq 1)
$slot = $null
for ($i = 1; $i -le 7 -and -not $slot; $i++) {
    $day = [DateTime]::UtcNow.AddHours(4).AddDays($i).ToString("yyyy-MM-dd")
    $slots = Call GET "/api/s/$ssk/bookings/slots?serviceId=$($svc.id)&date=$day"
    $slot = @($slots.items | Where-Object { $_.available }) | Select-Object -First 1
}
Check "available slot found" ($null -ne $slot)
$startIso = if ($slot.start -is [DateTime]) { $slot.start.ToUniversalTime().ToString("o") } else { "$($slot.start)" }
$booking = Call POST "/api/s/$ssk/bookings" @{ serviceId = $svc.id; start = $startIso; customerName = "Huda"; phone = "0503334455" }
Check "booking created (pending)" ($booking.status -eq "pending") $booking.status
$e = CallError POST "/api/s/$ssk/bookings" @{ serviceId = $svc.id; start = $startIso; customerName = "Late"; phone = "0503334466" }
Check "double booking rejected (slot_taken)" ($e.status -eq 409 -and $e.code -eq "slot_taken") "$($e.status) $($e.code)"
$bookings = Call GET "/api/projects/$($sp.id)/data/bookings?upcoming=true" -Token $t
Check "owner sees the booking" (@($bookings.items).Count -eq 1 -and $bookings.pendingCount -eq 1)

Write-Host "== Pro: courses platform" -ForegroundColor Cyan
$c = Call POST "/api/projects" @{ name = "Academy"; description = "Online courses platform for programming"; templateKey = "courses" } $t
$cp = WaitTask $c.id $t
Check "courses template used" ($cp.templateKey -eq "courses")
Check "multi-page site" ($cp.files.Count -gt 2) $cp.files.Count
$course = Call POST "/api/projects/$($cp.id)/data/courses" @{ title = "C# Basics"; description = "Intro"; price = 0; currency = "USD"; isPublished = $true; sortOrder = 0 } $t
Call POST "/api/projects/$($cp.id)/data/courses/$($course.id)/lessons" @{ title = "Lesson 1"; videoUrl = "https://youtu.be/x"; isFreePreview = $false; durationMinutes = 10; sortOrder = 0 } $t | Out-Null
Check "javascript: url rejected" ((CallStatus POST "/api/projects/$($cp.id)/data/courses" @{ title = "Bad"; imageUrl = "javascript:alert(1)"; price = 0 } $t) -eq 400)
$list = Call GET "/api/s/$($cp.siteKey)/courses"
Check "public course list" (@($list.items).Count -eq 1) @($list.items).Count
$student = Call POST "/api/s/$($cp.siteKey)/auth/register" @{ name = "Student"; email = "s$stamp@test.local"; password = "Passw0rd!" }
$detail = Call GET "/api/s/$($cp.siteKey)/courses/$($course.id)"
Check "lesson video locked before enroll" (-not $detail.lessons[0].videoUrl)
Call POST "/api/s/$($cp.siteKey)/courses/$($course.id)/enroll" -Token $student.token | Out-Null
$detail = Call GET "/api/s/$($cp.siteKey)/courses/$($course.id)" -Token $student.token
Check "lesson unlocked after free enroll" ([bool]$detail.lessons[0].videoUrl)
Check "site token rejected on other site" ((CallStatus GET "/api/s/$($p.siteKey)/auth/me" -Token $student.token) -eq 401)
Check "site token rejected on app API" ((CallStatus GET "/api/me" -Token $student.token) -eq 401)

Write-Host "== Pro: ads platform" -ForegroundColor Cyan
$a = Call POST "/api/projects" @{ name = "Souq"; description = "Classified ads marketplace for cars"; templateKey = "ads" } $t
$ap = WaitTask $a.id $t
Check "ads template used" ($ap.templateKey -eq "ads")
$adUser = Call POST "/api/s/$($ap.siteKey)/auth/register" @{ name = "Seller"; email = "seller$stamp@test.local"; password = "Passw0rd!" }
Call POST "/api/s/$($ap.siteKey)/ads" @{ title = "Toyota 2020"; description = "Clean"; price = 15000; currency = "AED"; category = "cars"; city = "Dubai"; phone = "0500000000"; images = @() } $adUser.token | Out-Null
$ads = Call GET "/api/s/$($ap.siteKey)/ads"
Check "ad visible publicly" ($ads.total -ge 1 -or $ads.items.Count -ge 1 -or $ads.Count -ge 1)

Write-Host "== Backend hosting for a published platform" -ForegroundColor Cyan
Call POST "/api/admin/projects/$($ap.id)/hosting" @{ tier = "backend"; months = 1 } $at | Out-Null
Call POST "/api/projects/$($ap.id)/publish" -Token $t | Out-Null
Check "paid backend serves the live site" ((CallStatus GET "/api/s/$($ap.siteKey)/ads") -eq 200)
$e = CallError POST "/api/billing/checkout" @{ kind = "hosting"; projectId = $ap.id; tier = "static"; interval = "monthly" } $t
Check "cannot buy static hosting for a backend site" ($e.status -eq 400 -and $e.code -eq "backend_tier_required") "$($e.status) $($e.code)"

Write-Host "== Billing" -ForegroundColor Cyan
$plans = Call GET "/api/billing/plans"
Check "pro monthly price is 18" ($plans.pro.monthlyPrice -eq 18) $plans.pro.monthlyPrice
Check "hosting prices 5 / 10" ($plans.hosting.staticMonthly -eq 5 -and $plans.hosting.backendMonthly -eq 10) ($plans.hosting | ConvertTo-Json -Compress)
# With a Ziina key (user-secrets) checkout returns a Ziina payment page; without one it must fail cleanly.
try {
    $co = Call POST "/api/billing/checkout" @{ kind = "subscription"; interval = "monthly" } $t
    Check "checkout returns a Ziina payment page" ($co.paymentId -and $co.redirectUrl -match "^https://[^/]*ziina\.com/") $co.redirectUrl
} catch {
    Check "checkout fails cleanly without Ziina key" ([int]$_.Exception.Response.StatusCode -ge 400)
}
Check "webhook rejects bad signature" ((& curl.exe -s -o NUL -w "%{http_code}" -X POST -H "X-Hmac-Signature: deadbeef" -d "{}" "$Base/api/billing/ziina/webhook") -eq "401")

Write-Host "== AI cost per user (unit economics)" -ForegroundColor Cyan
Check "non-admin cannot read economics" ((CallStatus GET "/api/admin/economics" -Token $t) -eq 403)
$eco = Call GET "/api/admin/economics" -Token $at
Check "every AI call recorded" ($eco.ai.calls -ge 6 -and $eco.ai.tasks -ge 6) "$($eco.ai.calls) calls / $($eco.ai.tasks) tasks"
Check "cost per active user computed" ($eco.perUser.activeUsers -ge 1 -and $eco.perUser.average -gt 0) ($eco.perUser | ConvertTo-Json -Compress)
Check "free and Pro usage split" ((@($eco.byPlan | ForEach-Object { $_.plan }) -contains "free") -and (@($eco.byPlan | ForEach-Object { $_.plan }) -contains "pro"))
Check "generate and edit costs split" ((@($eco.byPurpose | ForEach-Object { $_.purpose }) -contains "generate") -and (@($eco.byPurpose | ForEach-Object { $_.purpose }) -contains "edit"))
$mine = @($eco.topUsers | Where-Object { $_.userId -eq $ownerId })[0]
Check "owner's calls linked to projects and tasks" ($mine.projects -ge 3 -and $mine.tasks -ge 6) ($mine | ConvertTo-Json -Compress)
Check "Pro margin vs price reported" ($eco.pro.monthlyPrice -eq 18 -and $null -ne $eco.pro.marginPerSubscriber)
$prov = @($eco.byProvider)[0]
$month = $eco.month
$inv = Call PUT "/api/admin/economics/invoices" @{ provider = $prov.provider; month = $month; amountUsd = [Math]::Round($prov.estimatedCost * 2, 6) } $at
Check "invoice reconciled over calls" ($inv.calls -ge 1)
$eco = Call GET "/api/admin/economics" -Token $at
Check "costs now actual (from invoice)" ($eco.ai.actualKnownShare -gt 0) $eco.ai.actualKnownShare
Check "bad month rejected" ((CallStatus GET "/api/admin/economics?month=2026-99" -Token $at) -eq 400)
Check "cost per request with its parts" ($eco.requests.multiPart -ge 3 -and $eco.requests.maxParts -ge 8 -and @($eco.requests.top).Count -ge 1) ($eco.requests | ConvertTo-Json -Depth 2 -Compress)
Check "cost per site" (@($eco.projects.top).Count -ge 3 -and $null -ne $eco.projects.cost) ($eco.projects | ConvertTo-Json -Depth 2 -Compress)
Check "gross margin per Pro user" ($eco.proMargins.users -ge 1 -and $null -ne $eco.proMargins.margin -and $null -ne $eco.proMargins.negative) ($eco.proMargins | ConvertTo-Json -Depth 2 -Compress)

Write-Host "== Admin: system health" -ForegroundColor Cyan
Check "non-admin cannot read system health" ((CallStatus GET "/api/admin/system" -Token $t) -eq 403)
$sys = Call GET "/api/admin/system?minutes=60" -Token $at
Check "process, agent and disk metrics" ($sys.process.workingSetMb -gt 0 -and $sys.agent.workers -ge 1 -and $sys.agent.succeeded -ge 6 -and $sys.disk.freeGb -gt 0) ($sys | ConvertTo-Json -Depth 3 -Compress)
Check "provider 429s recorded (and retried)" ($sys.ai.rateLimited -ge 2) $sys.ai.rateLimited
Check "no credits left on hold" ($sys.credits.activeHolds -eq 0) ($sys.credits | ConvertTo-Json -Compress)

Write-Host "== Server monitoring, alert e-mails and error log" -ForegroundColor Cyan
Check "non-admin cannot read monitoring" ((CallStatus GET "/api/admin/monitor" -Token $t) -eq 403)
function PostMetrics($body, [string]$token) {
    & curl.exe -s -o NUL -w "%{http_code}" -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $token" -d ($body | ConvertTo-Json -Compress).Replace('"', '\"') "$Base/api/internal/metrics"
}
$report = @{ cpu = 12.5; memory = 40; memoryTotalGb = 8; disk = 97; diskUsedGb = 77.6; diskTotalGb = 80; load1 = 0.4; cores = 4; backupAgeHours = 3; uptimeHours = 100; pgReady = $true }
Check "server 2 report with a wrong token rejected" ((PostMetrics $report "wrong") -eq "401")
Check "server 2 report accepted" ((PostMetrics $report "dev-monitor-token") -eq "200")
$mon = Call POST "/api/admin/monitor/check" -Token $at
$s2 = $mon.view.servers | Where-Object { $_.id -eq "server2" }
Check "both servers have readings" ((($mon.view.servers | Where-Object { $_.id -eq "server1" }).readings.Count -ge 3) -and $s2.readings.Count -ge 5) ($mon.view.servers | ConvertTo-Json -Depth 4 -Compress)
Check "server 2 full disk is critical" ((($s2.readings | Where-Object { $_.key -eq "disk" }).level) -eq "critical")
Check "alert raised and e-mailed" ($mon.changes -ge 1 -and @($mon.view.alerts | Where-Object { $_.id -eq "server2:disk" -and $_.level -eq "critical" }).Count -eq 1) ($mon.view.alerts | ConvertTo-Json -Compress)
Check "alerts go to the owner" (@($mon.view.email.recipients) -contains "nasermostafa.ma122@gmail.com" -and $mon.view.email.enabled)
Check "same alert is not e-mailed twice" ((Call POST "/api/admin/monitor/check" -Token $at).changes -eq 0)
$report.disk = 41
PostMetrics $report "dev-monitor-token" | Out-Null
$mon = Call POST "/api/admin/monitor/check" -Token $at
Check "recovery e-mailed and alert cleared" ($mon.changes -ge 1 -and @($mon.view.alerts | Where-Object { $_.id -eq "server2:disk" }).Count -eq 0)
Start-Sleep -Seconds 2
$mon = Call GET "/api/admin/monitor" -Token $at
Check "alert e-mails were sent" ($mon.email.sent -ge 2 -and $mon.email.failed -eq 0) ($mon.email | ConvertTo-Json -Compress)
Check "recent errors listed" ($null -ne $mon.errorsLast10Minutes)
$test = Call POST "/api/admin/email/test" @{ kind = "all"; lang = "en" } $at
Check "test e-mail of every template" ($test.sent -eq 10 -and $test.mode -eq "pickup") ($test | ConvertTo-Json -Compress)
$preview = (& curl.exe -s -H "Authorization: Bearer $at" "$Base/api/admin/email/preview?kind=hosting_expiring&lang=ar") -join "`n"
Check "branded Arabic preview (RTL, logo, renew button)" ($preview -match 'dir="rtl"' -and $preview -match 'data:image/png;base64' -and $preview -match 'renew=')
$me = Call GET "/api/me" -Token $t -Headers @{ "X-Casco-Lang" = "hi" }
Check "user language saved for e-mails" ($me.user.locale -eq "hi") $me.user.locale
Call GET "/api/me" -Token $t -Headers @{ "X-Casco-Lang" = "ar" } | Out-Null
Check "reminder run works" ($null -ne (Call POST "/api/admin/email/reminders" -Token $at).sent)

Write-Host "== Admin: prices" -ForegroundColor Cyan
Check "non-admin cannot read prices" ((CallStatus GET "/api/admin/pricing" -Token $t) -eq 403)
$pr = Call GET "/api/admin/pricing" -Token $at
Check "admin sees current prices" ($pr.current.proMonthlyPrice -eq 18 -and $pr.current.hostingStaticMonthly -eq 5 -and $pr.defaults.proMonthlyPrice -eq 18)
$edit = $pr.current
$edit.proMonthlyPrice = 25; $edit.hostingStaticMonthly = 6; $edit.hostingBackendMonthly = 12
Call PUT "/api/admin/pricing" $edit $at | Out-Null
$plans = Call GET "/api/billing/plans"
Check "new prices live on the pricing page" ($plans.pro.monthlyPrice -eq 25 -and $plans.hosting.staticMonthly -eq 6 -and $plans.hosting.backendMonthly -eq 12) ($plans | ConvertTo-Json -Compress -Depth 5)
Check "new prices used for site hosting" ((Call GET "/api/projects/$($ap.id)/hosting" -Token $t).prices.backend.monthly -eq 12)
$edit.proMonthlyPrice = 0
Check "invalid price rejected" ((CallError PUT "/api/admin/pricing" $edit $at).status -eq 400)
Check "rejected edit changed nothing" ((Call GET "/api/billing/plans").pro.monthlyPrice -eq 25)
Call DELETE "/api/admin/pricing" -Token $at | Out-Null
Check "prices reset to defaults" ((Call GET "/api/billing/plans").pro.monthlyPrice -eq 18)

Write-Host "== Admin: users, who paid, and their sites" -ForegroundColor Cyan
Check "non-admin cannot list users" ((CallStatus GET "/api/admin/users" -Token $t) -eq 403)
$list = Call GET "/api/admin/users?filter=built_unpaid&search=owner$stamp" -Token $at
Check "owner listed as built a site but not paid" ($list.total -eq 1 -and $list.items[0].id -eq $ownerId -and $list.items[0].projects -ge 4) ($list | ConvertTo-Json -Compress -Depth 4)
$bySite = Call GET "/api/admin/users?search=$([uri]::EscapeDataString("Coffee House $stamp"))" -Token $at
Check "users searchable by site name" ($bySite.total -eq 1 -and $bySite.items[0].id -eq $ownerId)
Call POST "/api/admin/projects/$($ap.id)/hosting" @{ tier = "backend"; months = 1; amount = 12 } $at | Out-Null
$list = Call GET "/api/admin/users?filter=paid&search=owner$stamp" -Token $at
Check "manual payment moves owner to paid" ($list.total -eq 1 -and $list.items[0].paid -eq 12) ($list.items | ConvertTo-Json -Compress)
Check "filter counts updated" ($list.counts.paid -eq 1 -and $list.counts.built_unpaid -eq 0 -and $list.counts.built -eq 1)
$detail = Call GET "/api/admin/users/$ownerId" -Token $at
Check "user detail lists every site" (@($detail.sites).Count -ge 4) @($detail.sites).Count
$live = @($detail.sites | Where-Object { $_.id -eq $ap.id })[0]
Check "live site link and hosting shown" ($live.siteUrl -and $live.hosting.active -and $live.hosting.requiredTier -eq "backend") ($live | ConvertTo-Json -Compress -Depth 4)
Check "admin can preview any user's site" ((& curl.exe -s -o NUL -w "%{http_code}" "$($live.previewUrl)index.html") -eq "200")
$pay = @($detail.payments | Where-Object { $_.provider -eq "manual" })[0]
Check "manual payment recorded as revenue" ($pay.amount -eq 12 -and $pay.kind -eq "hosting" -and $pay.projectName -eq $ap.name -and $pay.status -eq "completed" -and $detail.totals.paid -eq 12)
Check "unknown user 404" ((CallStatus GET "/api/admin/users/$([guid]::NewGuid())" -Token $at) -eq 404)

Write-Host "== Pro user running out of credits" -ForegroundColor Cyan
function AiError([string]$Path, $Body) { CallError POST $Path $Body $t }
$me = Call GET "/api/me" -Token $t
Call POST "/api/admin/users/$ownerId/credits" @{ amount = 30 - $me.credits.available; note = "smoke" } $at | Out-Null
$e = AiError "/api/projects/$($p.id)/messages" @{ content = "Redesign everything"; premium = $true }
Check "power mode on 30 credits suggests normal mode" ($e.status -eq 402 -and $e.code -eq "insufficient_credits" -and $e.details.tryStandard -eq $true) "$($e.status) $($e.code)"
$e = AiError "/api/projects/$($p.id)/messages" @{ content = "Make the header darker" }
Check "last 30 credits still usable (full hold is 80)" ($e.status -eq 200) "$($e.status) $($e.code)"
WaitTask $p.id $t | Out-Null
$me = Call GET "/api/me" -Token $t
if ($me.credits.available -gt 3) { Call POST "/api/admin/users/$ownerId/credits" @{ amount = 3 - $me.credits.available; note = "smoke" } $at | Out-Null }
$e = AiError "/api/projects/$($p.id)/messages" @{ content = "Add a footer" }
Check "empty balance: 402 with top-up details" ($e.status -eq 402 -and $e.code -eq "insufficient_credits" -and $e.details.isPro -eq $true -and $e.details.available -le 3 -and $e.details.needed -eq 10) ($e.details | ConvertTo-Json -Compress)
Check "Pro subscriber is not told to subscribe again" ($e.message -notmatch "Pro") $e.message
Call POST "/api/admin/users/$ownerId/credits" @{ amount = 5000; note = "smoke" } $at | Out-Null

Write-Host "== Export" -ForegroundColor Cyan
$code = & curl.exe -s -o NUL -w "%{http_code}" -H "Authorization: Bearer $t" "$Base/api/projects/$($p.id)/export"
Check "pro can export zip" ($code -eq "200") $code

Write-Host "== Delete site" -ForegroundColor Cyan
Check "uploaded image served before delete" ((& curl.exe -s -o NUL -w "%{http_code}" $up.url) -eq "200")
foreach ($id in @($p.id, $sp.id, $cp.id, $ap.id)) { Call DELETE "/api/projects/$id" -Token $t | Out-Null }
Check "deleting a site removes its images" ((& curl.exe -s -o NUL -w "%{http_code}" $up.url) -eq "404")
Check "deleted store data gone" ((CallStatus GET "/api/s/$ssk/store/products") -eq 404)

Write-Host ""
if ($failures -eq 0) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green } else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red; exit 1 }
