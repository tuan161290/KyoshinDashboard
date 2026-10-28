# ==========================================================================
#  Kyoshin - doc / sua cau hinh cho run-console.cmd va install-service.cmd
#  (Windows PowerShell 5.1; chi dung ky tu ASCII)
#
#   -Action Show        in KEY=VALUE cho batch doc bang for /f
#   -Action Prepare     doi cong PLC theo -Com hoac bien moi truong KY_COM (COM5 / mock / rong = giu),
#                       roi lien ket Server + Agent
#   -Action Link        lien ket Server + Agent: khoa API chung, Agent gui ve 127.0.0.1:<port>
#   -Action WaitServer  cho web server tra loi (toi da 30 s)
#   -Action Configured  CONFIGURED=True neu Agent da duoc cau hinh (co khoa API) - chi doc file Agent
#
#  Sua JSON bang regex de giu nguyen dinh dang file appsettings.json.
# ==========================================================================
param(
    [Parameter(Mandatory = $true)][ValidateSet('Show', 'Prepare', 'Link', 'WaitServer', 'Configured')][string]$Action,
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Agent,
    [string]$Com = $env:KY_COM
)
$ErrorActionPreference = 'Stop'
$serverCfg = Join-Path $Server 'appsettings.json'
$agentCfg = Join-Path $Agent 'appsettings.json'
$q = [char]34
$placeholderKey = 'DOI-THANH-KHOA-BI-MAT-CUA-BAN'

function Read-Json([string]$path) { ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path)) }

# Replace the value of "name": "..." (first occurrence) without reformatting the file
function Set-JsonString([string]$text, [string]$name, [string]$value) {
    $re = New-Object Text.RegularExpressions.Regex($q + [regex]::Escape($name) + $q + '\s*:\s*' + $q + '[^' + $q + ']*' + $q)
    if (-not $re.IsMatch($text)) { throw "Khong thay muc $name trong cau hinh" }
    return $re.Replace($text, $q + $name + $q + ': ' + $q + $value.Replace('$', '$$') + $q, 1)
}

function Set-JsonBool([string]$text, [string]$name, [bool]$value) {
    $pattern = $q + [regex]::Escape($name) + $q + '\s*:\s*(true|false)'
    $re = New-Object Text.RegularExpressions.Regex -ArgumentList $pattern, 'IgnoreCase'
    if (-not $re.IsMatch($text)) { throw "Khong thay muc $name trong cau hinh" }
    return $re.Replace($text, $q + $name + $q + ': ' + $value.ToString().ToLower(), 1)
}

# Port of the web server (Kestrel:Endpoints:Http:Url in Server\appsettings.json)
function Get-Port {
    $url = (Read-Json $serverCfg).Kestrel.Endpoints.Http.Url
    if (-not $url) { return 5000 }
    return ([uri]($url -replace '0\.0\.0\.0|\*|\+', 'localhost')).Port
}

# Same API key on both sides; the server only accepts data from this machine
function Invoke-Link {
    $port = Get-Port
    $s = [IO.File]::ReadAllText($serverCfg)
    $key = (Read-Json $serverCfg).Ingest.ApiKey
    if (-not $key -or $key -eq $placeholderKey) {
        $key = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        $s = Set-JsonString $s 'ApiKey' $key
    }
    $s = Set-JsonString $s 'AllowedIps' '127.0.0.1'
    [IO.File]::WriteAllText($serverCfg, $s)

    $a = [IO.File]::ReadAllText($agentCfg)
    $a = Set-JsonString $a 'Url' ('http://127.0.0.1:' + $port)
    $a = Set-JsonString $a 'ApiKey' $key
    [IO.File]::WriteAllText($agentCfg, $a)
}

try {
    switch ($Action) {
        'Show' {
            $port = Get-Port
            $plc = (Read-Json $agentCfg).Plc
            $serverKey = (Read-Json $serverCfg).Ingest.ApiKey
            $agentKey = (Read-Json $agentCfg).Server.ApiKey
            $stable = if ($null -ne $plc.StableSeconds) { $plc.StableSeconds } else { 15 }
            $onSec = if ($null -ne $plc.OnConfirmSeconds) { $plc.OnConfirmSeconds } else { $stable }
            $offSec = if ($null -ne $plc.OffConfirmSeconds) { $plc.OffConfirmSeconds } else { $stable }
            $command = if ($plc.Command) { $plc.Command } else { 'WR' }
            if ($plc.UseMock) { $mode = 'GIA LAP - du lieu mo phong, khong doc PLC' }
            else { $mode = 'PLC that, cong ' + $plc.ComPort + ', doc X bang ' + $command + ', sum check ' + $plc.SumCheck + ', xac nhan ON ' + $onSec + ' s / OFF ' + $offSec + ' s' }
            # Errors:OnlyWhenGreenOrYellow, missing = true (the server default)
            $onlyGY = (Read-Json $serverCfg).Errors.OnlyWhenGreenOrYellow
            if ($onlyGY -eq $false) { $errors = 'ghi loi doc lap, khong phu thuoc den (Errors:OnlyWhenGreenOrYellow = false)' }
            else { $errors = 'chi ghi loi khi den Xanh/Vang (Errors:OnlyWhenGreenOrYellow = true)' }

            $ports = [IO.Ports.SerialPort]::GetPortNames() | Sort-Object
            $busy = ''
            $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($listener) { $busy = (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName; if (-not $busy) { $busy = 'PID ' + $listener.OwningProcess } }
            $lan = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
                ForEach-Object { 'http://' + $_.IPAddress + ':' + $port }

            'PORT=' + $port
            'MODE=' + $mode
            'ERRORS=' + $errors
            'AGENT_KEYSET=' + [bool]$agentKey
            'LINKED=' + [bool]($serverKey -and $serverKey -ne $placeholderKey -and $agentKey -eq $serverKey)
            if ($ports) { 'COMPORTS=' + ($ports -join ', ') } else { 'COMPORTS=khong co - kiem tra bo USB-RS485 va driver' }
            'PORT_BUSY=' + $busy
            'LAN_URLS=' + ($lan -join '  ')
        }
        'Prepare' {
            if ($Com) {
                $a = [IO.File]::ReadAllText($agentCfg)
                if ($Com.Trim() -eq 'mock') { $a = Set-JsonBool $a 'UseMock' $true }
                else {
                    $c = $Com.Trim().ToUpper()
                    if ($c -notmatch '^COM\d+$') { 'ERROR=Cong COM khong hop le: ' + $c; exit 2 }
                    $a = Set-JsonString $a 'ComPort' $c
                    $a = Set-JsonBool $a 'UseMock' $false
                }
                [IO.File]::WriteAllText($agentCfg, $a)
            }
            Invoke-Link
            'OK=True'
        }
        'Configured' {
            $configured = $false
            if (Test-Path $agentCfg) { $configured = [bool](Read-Json $agentCfg).Server.ApiKey }
            'CONFIGURED=' + $configured
        }
        'Link' {
            Invoke-Link
            'PORT=' + (Get-Port)
        }
        'WaitServer' {
            $url = 'http://127.0.0.1:' + (Get-Port) + '/api/machineapi/agent'
            for ($i = 0; $i -lt 60; $i++) {
                try {
                    $req = [Net.WebRequest]::Create($url)
                    $req.Timeout = 2000
                    $req.GetResponse().Close()
                    'READY=True'; exit 0
                }
                catch [Net.WebException] {
                    # An HTTP error answer still means the server is up
                    if ($_.Exception.Response) { 'READY=True'; exit 0 }
                }
                Start-Sleep -Milliseconds 500
            }
            'READY=False'; exit 1
        }
    }
}
catch {
    'ERROR=' + ($_.Exception.Message -replace '[\r\n]+', ' ')
    exit 1
}
