# Load test: N Pro users each start a big multi-part build at the same moment, while server health is sampled.
# Needs Turnstile test keys (Development, or a staging server started with them) because it registers users.
#
# Fake AI (measures Casco itself; #demo-slow-N adds N seconds per AI call to mimic real model latency):
#   powershell -File scripts/load-test.ps1 -Base http://localhost:5080 -Users 8
# Real AI (costs money; run on staging with real keys):
#   powershell -File scripts/load-test.ps1 -Base https://staging.example -Users 8 -Prompt "Build a 12-page company website: ..."
param(
    [string]$Base = "http://localhost:5080",
    [int]$Users = 8,
    [string]$Prompt = "Build the inner pages #demo-pages-16 #demo-slow-3",
    [string]$AdminEmail = "admin@casco.local",
    [string]$AdminPassword = "AdminPass1!",
    [int]$TimeoutMinutes = 30,
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

function WaitIdle([string]$ProjectId, [string]$Token, [int]$Seconds = 600) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $p = Call GET "/api/projects/$ProjectId" -Token $Token
        if (-not $p.activeTask) { return $p }
        Start-Sleep 2
    }
    throw "Project $ProjectId still busy after $Seconds s"
}

$stamp = Get-Random
try { $admin = Call POST "/api/auth/login" @{ email = $AdminEmail; password = $AdminPassword } }
catch { $admin = Call POST "/api/auth/register" @{ name = "Admin"; email = $AdminEmail; password = $AdminPassword; turnstileToken = $Captcha } }
$at = $admin.token

Write-Host "== Preparing $Users Pro users with a site each" -ForegroundColor Cyan
$runs = @()
for ($i = 1; $i -le $Users; $i++) {
    $u = Call POST "/api/auth/register" @{ name = "Load $i"; email = "load$i-$stamp@test.local"; password = "Passw0rd!"; turnstileToken = $Captcha }
    $me = Call GET "/api/me" -Token $u.token
    Call POST "/api/admin/users/$($me.user.id)/activate" @{ interval = "monthly" } $at | Out-Null
    $created = Call POST "/api/projects" @{ name = "Load site $i"; description = "Company website for a contracting business number $i" } $u.token
    $runs += [pscustomobject]@{ User = $i; Token = $u.token; UserId = $me.user.id; ProjectId = $created.id; TaskId = $null; Error = $null }
}
foreach ($r in $runs) { WaitIdle $r.ProjectId $r.Token | Out-Null }

Write-Host "== Starting $Users big builds at once" -ForegroundColor Cyan
$started = Get-Date
# All sends go out together (one background job each) so the builds really overlap.
$jobs = foreach ($r in $runs) {
    # A unique suffix per run and user, or identical requests are answered from the AI response cache.
    Start-Job -ArgumentList $Base, $r.ProjectId, $r.Token, "$Prompt (run $stamp, user $($r.User))" -ScriptBlock {
        param($Base, $ProjectId, $Token, $Prompt)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $body = [Text.Encoding]::UTF8.GetBytes((@{ content = $Prompt } | ConvertTo-Json))
            $res = Invoke-RestMethod -Method POST -Uri "$Base/api/projects/$ProjectId/messages" -Headers @{ Authorization = "Bearer $Token" } -ContentType "application/json; charset=utf-8" -Body $body
            @{ TaskId = $res.taskId; Ms = $sw.ElapsedMilliseconds; Error = $null }
        }
        catch { @{ TaskId = $null; Ms = $sw.ElapsedMilliseconds; Error = $_.Exception.Message } }
    }
}
$sent = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job
for ($i = 0; $i -lt $runs.Count; $i++) {
    $runs[$i].TaskId = $sent[$i].TaskId
    $runs[$i].Error = $sent[$i].Error
    $runs[$i] | Add-Member -NotePropertyName SendMs -NotePropertyValue $sent[$i].Ms
}

$samples = @()
$apiMs = @()
$deadline = $started.AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Call GET "/api/projects/$($runs[0].ProjectId)" -Token $runs[0].Token | Out-Null
    $apiMs += $sw.ElapsedMilliseconds
    $s = Call GET "/api/admin/system?minutes=$([Math]::Max(1, [int]((Get-Date) - $started).TotalMinutes + 1))" -Token $at
    $samples += $s
    $open = @($runs | Where-Object { $_.TaskId -and (Call GET "/api/tasks/$($_.TaskId)" -Token $_.Token).status -in @("queued", "running") }).Count
    Write-Host ("  t={0,4}s running={1} queued={2} cpu={3}% ram={4}MB api={5}ms ai-latency={6}ms open={7}" -f [int]((Get-Date) - $started).TotalSeconds,
        $s.agent.running, $s.agent.queued, $s.process.cpuPercent, $s.process.workingSetMb, $apiMs[-1], $s.ai.avgLatencyMs, $open)
    if ($open -eq 0) { break }
    Start-Sleep 3
}

Write-Host "== Results" -ForegroundColor Cyan
$rows = foreach ($r in $runs) {
    $task = if ($r.TaskId) { Call GET "/api/tasks/$($r.TaskId)" -Token $r.Token } else { $null }
    $p = Call GET "/api/projects/$($r.ProjectId)" -Token $r.Token
    $v = $p.versions | Select-Object -First 1
    [pscustomobject]@{
        user = $r.User
        status = if ($task) { $task.status } else { "not started: $($r.Error)" }
        sendMs = $r.SendMs
        queueWaitS = if ($task -and $task.startedAt) { [Math]::Round(([datetime]$task.startedAt - [datetime]$task.createdAt).TotalSeconds, 1) } else { $null }
        durationS = if ($task -and $task.completedAt -and $task.startedAt) { [Math]::Round(([datetime]$task.completedAt - [datetime]$task.startedAt).TotalSeconds, 1) } else { $null }
        parts = $v.parts
        filesChanged = $v.filesChanged
        pages = @($p.files | Where-Object { $_ -like "*.html" }).Count
        credits = $task.creditsCharged
        continuable = [bool]$p.continuable
        error = $task.error
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 250 | Write-Host

$done = @($rows | Where-Object { $_.durationS -ne $null })
$last = $samples[-1]
$summary = [ordered]@{
    users = $Users
    succeeded = @($rows | Where-Object status -eq "succeeded").Count
    failed = @($rows | Where-Object status -ne "succeeded").Count
    avgDurationS = if ($done.Count) { [Math]::Round(($done | Measure-Object durationS -Average).Average, 1) } else { 0 }
    maxDurationS = if ($done.Count) { ($done | Measure-Object durationS -Maximum).Maximum } else { 0 }
    maxQueueWaitS = ($rows | Measure-Object queueWaitS -Maximum).Maximum
    maxSendMs = ($rows | Measure-Object sendMs -Maximum).Maximum
    apiAvgMs = if ($apiMs.Count) { [int]($apiMs | Measure-Object -Average).Average } else { 0 }
    apiMaxMs = if ($apiMs.Count) { ($apiMs | Measure-Object -Maximum).Maximum } else { 0 }
    peakRunning = ($samples | ForEach-Object { $_.agent.running } | Measure-Object -Maximum).Maximum
    peakQueued = ($samples | ForEach-Object { $_.agent.queued } | Measure-Object -Maximum).Maximum
    peakCpuPercent = ($samples | ForEach-Object { $_.process.cpuPercent } | Measure-Object -Maximum).Maximum
    peakRamMb = ($samples | ForEach-Object { $_.process.workingSetMb } | Measure-Object -Maximum).Maximum
    peakDbConnections = ($samples | ForEach-Object { $_.database.connections } | Measure-Object -Maximum).Maximum
    aiAvgLatencyMs = $last.ai.avgLatencyMs
    aiP90LatencyMs = $last.ai.p90LatencyMs
    aiRateLimited = $last.ai.rateLimited
    diskFreeGb = $last.disk.freeGb
    creditsStillHeld = (Call GET "/api/admin/system" -Token $at).credits.heldCredits
}
[pscustomobject]$summary | Format-List | Out-String | Write-Host

$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $outDir | Out-Null
$file = Join-Path $outDir ("load-test-{0:yyyyMMdd-HHmm}.csv" -f (Get-Date))
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 $file
[pscustomobject]$summary | Export-Csv -NoTypeInformation -Encoding UTF8 ($file -replace "\.csv$", "-summary.csv")
Write-Host "Saved $file"
if ($summary.failed -gt 0 -or $summary.creditsStillHeld -gt 0) { exit 1 }
