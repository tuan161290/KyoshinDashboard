# ==========================================================================
#  Kyoshin - cap nhat len ban moi (update.cmd goi script nay voi quyen Administrator)
#  (Windows PowerShell 5.1; chi dung ky tu ASCII)
#
#   (khong tham so)  tai ban moi nhat tu GitHub Releases cua $Repo (can Internet)
#   -Zip <file>      cai tu file Kyoshin-win-x64.zip (khong can Internet)
#   -CheckOnly       chi xem ban dang cai va ban moi nhat tren GitHub
#   -Force           cai ca khi goi khong moi hon ban dang cai (cai lai / quay ve ban cu)
#
#  1. Lay goi: tai tu GitHub + kiem tra SHA256 (hoac file zip), giai nen, kiem tra goi
#  2. Dung KyoshinAgent roi KyoshinServer
#  3. Sao luu chuong trinh + CSDL + cau hinh vao <Root>\backup\<ban cu>_<thoi gian> (giu 3 ban gan nhat)
#  4. Chep file moi, GIU NGUYEN appsettings.json, Kyoshin.db (du lieu), agent.db (hang doi)
#  5. Khoi dong Server, cho web tra loi, roi khoi dong Agent
#  Loi o buoc 4-5: tra lai ban cu tu sao luu va khoi dong lai.
#  Nhat ky: <Root>\update.log
# ==========================================================================
param(
    [string]$Root = 'C:\Kyoshin',
    [string]$Zip = '',
    [string]$Repo = 'tuan161290/KyoshinDashboard',
    [switch]$CheckOnly,
    [switch]$Force,
    [switch]$NoServices   # chi de thu tren may dev: chay thang 2 file exe thay cho 2 Windows Service
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # no progress bars from the web cmdlets (slow on PS 5.1)

$AssetName = 'Kyoshin-win-x64.zip'
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$ServerDir = Join-Path $Root 'KyoshinServer'
$AgentDir = Join-Path $Root 'KyoshinAgent'
$LogFile = Join-Path $Root 'update.log'
$KeepServer = @('appsettings.json', 'Kyoshin.db', 'Kyoshin.db-shm', 'Kyoshin.db-wal')
$KeepAgent = @('appsettings.json', 'agent.db', 'agent.db-shm', 'agent.db-wal')
$Work = Join-Path $env:TEMP ('kyoshin-update-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$GitHubHeaders = @{ 'User-Agent' = 'KyoshinUpdater'; 'Accept' = 'application/vnd.github+json' }
$script:Started = @()   # -NoServices: processes started by this script

function Log([string]$msg) {
    Write-Host $msg
    try { Add-Content -Path $LogFile -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $msg) -Encoding ASCII } catch { }
}

# "v1.2.0", "1.2.0-local" -> [version] 1.2.0; anything else -> 0.0 (always older)
function Get-VersionValue([string]$text) {
    $core = (($text -replace '^\s*[vV]', '').Trim() -split '[-+\s]')[0]
    $v = $null
    if ([version]::TryParse($core, [ref]$v)) { return $v }
    return [version]'0.0'
}

function Read-TextFile([string]$path) {
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path)).Trim() }
    return ''
}

# Port of the web server (Kestrel:Endpoints:Http:Url in KyoshinServer\appsettings.json)
function Get-Port {
    try { $url = (ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $ServerDir 'appsettings.json')))).Kestrel.Endpoints.Http.Url } catch { $url = $null }
    if (-not $url) { return 5000 }
    return ([uri]($url -replace '0\.0\.0\.0|\*|\+', 'localhost')).Port
}

function Copy-Tree([string]$from, [string]$to, [string[]]$exclude) {
    $rc = @($from, $to, '/E', '/R:10', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($exclude -and $exclude.Count) { $rc += '/XF'; $rc += $exclude }
    & "$env:SystemRoot\System32\robocopy.exe" @rc | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Chep file that bai: $from -> $to (robocopy $LASTEXITCODE)" }
}

# KyoshinDashboard.exe / KyoshinAgent.exe still running from this install (e.g. a run-console.cmd window)
function Get-RunningFromRoot {
    $prefix = $Root + '\'
    @(Get-Process -Name 'KyoshinDashboard', 'KyoshinAgent' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}

function Stop-Kyoshin {
    if ($NoServices) {
        foreach ($p in $script:Started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
        $script:Started = @()
        Start-Sleep -Seconds 1
        return
    }
    foreach ($name in 'KyoshinAgent', 'KyoshinServer') {
        $svc = Get-Service -Name $name
        if ($svc.Status -eq 'Stopped') { continue }
        Log "  Dung $name ..."
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
        catch {
            $procId = (Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId
            if ($procId) {
                Log "  $name khong dung sau 30 s, dung han tien trinh $procId"
                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
            }
        }
    }
}

# True when the web server answers (any HTTP status counts) within 60 s
function Wait-Server([int]$port, $process) {
    $url = "http://127.0.0.1:$port/api/machineapi/agent"
    for ($i = 0; $i -lt 60; $i++) {
        if ($process -and $process.HasExited) { return $false }
        try {
            $req = [Net.WebRequest]::Create($url)
            $req.Timeout = 2000
            $req.GetResponse().Close()
            return $true
        }
        catch [Net.WebException] { if ($_.Exception.Response) { return $true } }
        catch { }
        Start-Sleep -Seconds 1
    }
    return $false
}

# Start Server, wait for the web, start Agent; true when both run
function Start-Kyoshin {
    $port = Get-Port
    try {
        $serverProcess = $null
        if ($NoServices) {
            $serverProcess = Start-Process -FilePath (Join-Path $ServerDir 'KyoshinDashboard.exe') -WorkingDirectory $ServerDir -WindowStyle Hidden -PassThru
            $script:Started += $serverProcess
        }
        else { Start-Service -Name 'KyoshinServer' }
        if (-not (Wait-Server $port $serverProcess)) { Log "  [!] Web server khong tra loi tren cong $port"; return $false }
        Log "  Server OK: http://127.0.0.1:$port"

        if ($NoServices) {
            $agentProcess = Start-Process -FilePath (Join-Path $AgentDir 'KyoshinAgent.exe') -WorkingDirectory $AgentDir -WindowStyle Hidden -PassThru
            $script:Started += $agentProcess
            Start-Sleep -Seconds 5
            if ($agentProcess.HasExited) { Log '  [!] Agent dung ngay sau khi chay'; return $false }
        }
        else {
            Start-Service -Name 'KyoshinAgent'
            Start-Sleep -Seconds 5
            if ((Get-Service -Name 'KyoshinAgent').Status -ne 'Running') { Log '  [!] Service KyoshinAgent khong chay'; return $false }
        }
        Log '  Agent OK'
        return $true
    }
    catch {
        Log "  [!] $($_.Exception.Message)"
        return $false
    }
}

# Download to a file. Not Invoke-WebRequest: on PS 5.1 it can hang for hours on a large file.
# A stalled connection fails after 60 s; 3 attempts.
function Save-Url([string]$url, [string]$path) {
    for ($attempt = 1; ; $attempt++) {
        try {
            $req = [Net.HttpWebRequest]::Create($url)
            $req.UserAgent = 'KyoshinUpdater'
            $req.Timeout = 30000            # connect + first answer
            $req.ReadWriteTimeout = 60000   # no data for 60 s = failed
            $resp = $req.GetResponse()
            try {
                $total = $resp.ContentLength
                $in = $resp.GetResponseStream()
                $out = [IO.File]::Create($path)
                try {
                    $buf = New-Object byte[] 1048576
                    $done = 0L
                    $next = 10MB
                    while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
                        $out.Write($buf, 0, $n)
                        $done += $n
                        if ($total -gt 20MB -and $done -ge $next) { Write-Host ('  {0:N0} / {1:N0} MB' -f ($done / 1MB), ($total / 1MB)); $next += 10MB }
                    }
                }
                finally { $out.Dispose(); $in.Dispose() }
                if ($total -gt 0 -and $done -ne $total) { throw "tai thieu ($done / $total byte)" }
            }
            finally { $resp.Close() }
            return
        }
        catch {
            if ($attempt -ge 3) { throw "Tai that bai sau 3 lan: $($_.Exception.Message)" }
            Log "  [!] Tai loi ($($_.Exception.Message)), thu lai lan $($attempt + 1) ..."
            Start-Sleep -Seconds 5
        }
    }
}

function Remove-OldBackups {
    Get-ChildItem -LiteralPath (Join-Path $Root 'backup') -Directory -ErrorAction SilentlyContinue |
        Sort-Object CreationTime -Descending | Select-Object -Skip 3 |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force; Log "  Xoa sao luu cu: $($_.Name)" }
}

# Download the latest release from GitHub into $Work; returns $null when nothing to install
function Get-FromGitHub([string]$installed) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Log "Kiem tra ban moi nhat tren GitHub ($Repo) ..."
    try { $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $GitHubHeaders -TimeoutSec 30 }
    catch { throw "Khong lay duoc thong tin tu GitHub: $($_.Exception.Message). May khong co Internet? Cai tu file: update.cmd <file Kyoshin-win-x64.zip>" }

    $latest = $rel.tag_name -replace '^[vV]', ''
    Log "Ban moi nhat tren GitHub: $latest (phat hanh $($rel.published_at))"
    $newer = (Get-VersionValue $latest) -gt (Get-VersionValue $installed)
    if ($CheckOnly) {
        if ($newer) { Log '=> Co ban moi. Chay update.cmd de cai.' } else { Log '=> Dang dung ban moi nhat.' }
        return $null
    }
    if (-not $newer -and -not $Force) { Log '=> Dang dung ban moi nhat, khong can cap nhat. (Cai lai: update.cmd force)'; return $null }

    $asset = $rel.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) { throw "Ban $($rel.tag_name) tren GitHub chua co file $AssetName (GitHub Actions chua build xong?)" }
    $zipPath = Join-Path $Work $AssetName
    Log ("Tai {0} ({1:N1} MB) ..." -f $AssetName, ($asset.size / 1MB))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Save-Url $asset.browser_download_url $zipPath
    Log ('  Tai xong sau {0:N0} s' -f $sw.Elapsed.TotalSeconds)
    $sumAsset = $rel.assets | Where-Object { $_.name -eq "$AssetName.sha256" } | Select-Object -First 1
    if ($sumAsset) { Save-Url $sumAsset.browser_download_url "$zipPath.sha256" }
    return @{ Zip = $zipPath; Source = "GitHub $($rel.tag_name)" }
}

function Invoke-Update {
    if (-not $NoServices -and -not $CheckOnly) {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Can quyen Administrator' }
        foreach ($name in 'KyoshinServer', 'KyoshinAgent') {
            if (-not (Get-Service -Name $name -ErrorAction SilentlyContinue)) {
                throw "Chua cai service $name. Cai lan dau bang install-service.cmd trong goi Kyoshin."
            }
        }
    }
    if (-not (Test-Path (Join-Path $ServerDir 'KyoshinDashboard.exe')) -or -not (Test-Path (Join-Path $AgentDir 'KyoshinAgent.exe'))) {
        throw "Khong thay ban cai trong $Root (KyoshinServer, KyoshinAgent)"
    }

    $installed = Read-TextFile (Join-Path $Root 'version.txt')
    $installedText = if ($installed) { $installed } else { 'khong ro' }
    Log '=========================================================='
    Log "Ban dang cai: $installedText  ($Root)"
    New-Item -ItemType Directory -Path $Work | Out-Null

    # --- 1. Package ---
    if ($Zip) {
        if (-not (Test-Path -LiteralPath $Zip)) { throw "Khong thay file $Zip" }
        $pkgFile = @{ Zip = (Resolve-Path -LiteralPath $Zip).Path; Source = (Resolve-Path -LiteralPath $Zip).Path }
    }
    else {
        $pkgFile = Get-FromGitHub $installed
        if (-not $pkgFile) { return 0 }
    }
    Log '[1/5] Kiem tra goi ...'
    $sumText = Read-TextFile ($pkgFile.Zip + '.sha256')
    if ($sumText) {
        $expected = ($sumText -split '\s+')[0]
        $actual = (Get-FileHash -LiteralPath $pkgFile.Zip -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw 'File zip bi hong (SHA256 khong khop). Tai lai roi chay lai.' }
        Log '  SHA256 OK'
    }
    else { Log '  (khong co file .sha256 ben canh file zip - bo qua kiem tra SHA256)' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path $Work 'pkg'
    [IO.Compression.ZipFile]::ExtractToDirectory($pkgFile.Zip, $stage)
    $pkg = $stage
    if (-not (Test-Path (Join-Path $pkg 'Server\KyoshinDashboard.exe'))) {
        # zip with one top folder
        $sub = Get-ChildItem -LiteralPath $stage -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'Server\KyoshinDashboard.exe') } | Select-Object -First 1
        if ($sub) { $pkg = $sub.FullName }
    }
    foreach ($f in 'Server\KyoshinDashboard.exe', 'Agent\KyoshinAgent.exe', 'update.cmd', 'kyoshin-update.ps1') {
        if (-not (Test-Path (Join-Path $pkg $f))) { throw "Goi cap nhat thieu $f - day khong phai goi Kyoshin?" }
    }
    $newVersion = Read-TextFile (Join-Path $pkg 'version.txt')
    if (-not $newVersion) { $newVersion = 'khong ro' }
    Log "  Goi moi: $newVersion  ($($pkgFile.Source))"
    if ($CheckOnly) {
        if ((Get-VersionValue $newVersion) -gt (Get-VersionValue $installed)) { Log '=> Goi nay moi hon ban dang cai.' } else { Log '=> Goi nay khong moi hon ban dang cai.' }
        return 0
    }
    if ($Zip -and -not $Force -and -not ((Get-VersionValue $newVersion) -gt (Get-VersionValue $installed))) {
        Log '=> Goi nay khong moi hon ban dang cai. Muon cai lai / quay ve ban nay: update.cmd <file zip> force'
        return 0
    }

    # --- 2. Stop ---
    Log '[2/5] Dung service ...'
    Stop-Kyoshin
    $running = Get-RunningFromRoot
    if ($running.Count) {
        throw ("Van con chuong trinh chay tu {0}: {1} (cua so run-console.cmd?). Dong lai roi chay update.cmd" -f $Root, (($running | ForEach-Object { "$($_.Name) PID $($_.Id)" }) -join ', '))
    }

    # --- 3. Backup (program + data + config, consistent because everything is stopped) ---
    Log '[3/5] Sao luu ban dang cai ...'
    $label = $installed -replace '[^0-9A-Za-z\.\-]', ''
    if (-not $label) { $label = 'khongro' }
    $backup = Join-Path $Root ('backup\' + $label + '_' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    try {
        Copy-Tree $ServerDir (Join-Path $backup 'KyoshinServer') @()
        Copy-Tree $AgentDir (Join-Path $backup 'KyoshinAgent') @()
        foreach ($f in 'version.txt', 'update.cmd', 'kyoshin-update.ps1', 'kyoshin-config.ps1') {
            $p = Join-Path $Root $f
            if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $backup }
        }
    }
    catch {
        Log "  [!] $($_.Exception.Message)"
        Log '  Khong sao luu duoc - khong cap nhat. Khoi dong lai ban dang cai ...'
        [void](Start-Kyoshin)
        throw 'Cap nhat bi huy vi khong sao luu duoc (o dia day?)'
    }
    Log "  -> $backup"

    # --- 4. Copy + 5. Start ---
    $ok = $false
    try {
        Log '[4/5] Chep ban moi (giu cau hinh, du lieu, hang doi) ...'
        Copy-Tree (Join-Path $pkg 'Server') $ServerDir $KeepServer
        Copy-Tree (Join-Path $pkg 'Agent') $AgentDir $KeepAgent
        # Default settings of the new version, to compare with appsettings.json (the apps only read appsettings.json)
        foreach ($pair in @(@('Server', $ServerDir), @('Agent', $AgentDir))) {
            $src = Join-Path $pkg ($pair[0] + '\appsettings.json')
            if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $pair[1] 'appsettings.default.json') -Force }
        }
        Log '[5/5] Khoi dong ...'
        $ok = Start-Kyoshin
    }
    catch { Log "  [!] $($_.Exception.Message)" }

    if ($ok) {
        foreach ($f in 'update.cmd', 'kyoshin-update.ps1', 'kyoshin-config.ps1', 'version.txt') {
            $p = Join-Path $pkg $f
            if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination $Root -Force }
        }
        Remove-OldBackups
        Log "=> XONG: da cap nhat $installedText -> $newVersion"
        return 0
    }

    # --- Rollback ---
    Log '=> Ban moi khong chay duoc. Tra lai ban cu tu sao luu ...'
    Stop-Kyoshin
    # A WAL file written by the new version must not be applied to the restored database
    foreach ($f in 'Kyoshin.db-wal', 'Kyoshin.db-shm') { Remove-Item -LiteralPath (Join-Path $ServerDir $f) -Force -ErrorAction SilentlyContinue }
    Copy-Tree (Join-Path $backup 'KyoshinServer') $ServerDir @()
    Copy-Tree (Join-Path $backup 'KyoshinAgent') $AgentDir @('agent.db', 'agent.db-shm', 'agent.db-wal')
    if (Start-Kyoshin) { Log "=> Da tra lai ban $installedText. Xem nguyen nhan: $LogFile va Event Viewer > Windows Logs > Application" }
    else { Log "=> [LOI] Ban cu cung khong khoi dong duoc. Ban sao luu: $backup" }
    return 1
}

$exitCode = 1
$mutex = New-Object Threading.Mutex($false, 'Global\KyoshinUpdate')
if (-not $mutex.WaitOne(0)) { Write-Host '[LOI] Dang co mot lan cap nhat khac chay.'; exit 1 }
try {
    $exitCode = @(Invoke-Update)[-1]   # last value = the return code, even if something else was written to the output
}
catch {
    Log "[LOI] $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    if ($NoServices) { Stop-Kyoshin }   # dev test: leave nothing running
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    $mutex.ReleaseMutex()
}
exit $exitCode
