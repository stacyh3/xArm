param(
    [Parameter(Mandatory)][string]$Port,
    [ValidateRange(10, 3600)][int]$CaptureSeconds = 600,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    throw "Output directory must already exist: $OutputDirectory"
}
$serial = [System.IO.Ports.SerialPort]::new($Port, 115200)
$serial.DtrEnable = $true
$serial.ReadTimeout = 250
$serial.WriteTimeout = 1000
$serial.NewLine = "`n"
$raw = [IO.StreamWriter]::new((Join-Path $OutputDirectory 'live-firmware-serial.log'), $false)
$script:previous = $null
$script:bad = 0
$script:gaps = 0
$script:hello = $null
$script:phase = ''

function Encode([string]$Payload) {
    [byte]$sum = 0
    foreach ($b in [Text.Encoding]::ASCII.GetBytes($Payload)) { $sum = $sum -bxor $b }
    return "$Payload*$('{0:X2}' -f $sum)`n"
}

function Capture([string]$Name, [int]$Seconds) {
    $script:phase = $Name
    $frames = [Collections.Generic.List[object]]::new()
    $events = 0
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        try { $line = $serial.ReadLine().TrimEnd("`r") }
        catch [TimeoutException] { continue }
        $raw.WriteLine("$Name`t$line")
        if ($Name -eq 'warmup') { continue }
        if ($line.Length -gt 128 -or $line -notmatch '^([ -~]+)\*([0-9A-Fa-f]{2})$') {
            $script:bad++
            continue
        }
        $payload = $Matches[1]
        $expected = [Convert]::ToByte($Matches[2], 16)
        [byte]$sum = 0
        foreach ($b in [Text.Encoding]::ASCII.GetBytes($payload)) { $sum = $sum -bxor $b }
        if ($sum -ne $expected) { $script:bad++; continue }
        $f = $payload.Split(',')
        if ($f[0] -eq 'H' -and $f.Count -eq 8) {
            $script:hello = [pscustomobject]@{
                Version = [int]$f[1]; Firmware = $f[2]; ResetCause = [int]$f[3]
                Capabilities = [int]$f[4]; Rejects = [long]$f[5]
                Drops = [long]$f[6]; MaxLoopUs = [long]$f[7]
            }
        } elseif (($f[0] -eq 'S' -and $f.Count -eq 13) -or ($f[0] -eq 'E' -and $f.Count -eq 5)) {
            [uint32]$sequence = $f[1]
            if ($null -ne $script:previous -and $sequence -ne (($script:previous + 1L) % 4294967296L)) {
                $script:gaps++
            }
            $script:previous = [long]$sequence
            if ($f[0] -eq 'E') { $events++; continue }
            if ($f[4] -notmatch '^[01]{3}$' -or [int]$f[5] -notin 0,1 -or
                [int]$f[6] -gt 1023 -or [int]$f[7] -gt 1023) { $script:bad++; continue }
            $frames.Add([pscustomobject]@{
                Sequence = $sequence; Ms = [long]$f[2]; Range = [int]$f[3]
                Ir = $f[4]; Touch = [int]$f[5]; Light = [int]$f[6]; Sound = [int]$f[7]
                Red = [int]$f[8]; Green = [int]$f[9]; Blue = [int]$f[10]
                Clear = [int]$f[11]; Status = [int]$f[12]
            })
        } else { $script:bad++ }
    }
    $rate = 0.0
    $minInterval = 0L
    $maxInterval = 0L
    if ($frames.Count -gt 1) {
        $rate = 1000.0 * ($frames.Count - 1) / ($frames[-1].Ms - $frames[0].Ms)
        $intervals = for ($i = 1; $i -lt $frames.Count; $i++) { $frames[$i].Ms - $frames[$i - 1].Ms }
        $stats = $intervals | Measure-Object -Minimum -Maximum
        $minInterval = $stats.Minimum
        $maxInterval = $stats.Maximum
    }
    $result = [pscustomobject]@{
        Phase = $Name; Seconds = $Seconds; Frames = $frames.Count; Events = $events
        Hz = [math]::Round($rate, 3); MinIntervalMs = $minInterval; MaxIntervalMs = $maxInterval
        StatusValues = @($frames.Status | Sort-Object -Unique)
        RangeMm = ($frames.Range | Measure-Object -Minimum -Maximum | Select-Object Minimum,Maximum)
        Light = ($frames.Light | Measure-Object -Minimum -Maximum | Select-Object Minimum,Maximum)
        Sound = ($frames.Sound | Measure-Object -Minimum -Maximum | Select-Object Minimum,Maximum)
        LastFrame = $(if ($frames.Count) { $frames[-1] } else { $null })
    }
    Write-Host ($result | ConvertTo-Json -Compress -Depth 4)
    return $result
}

$results = [Collections.Generic.List[object]]::new()
try {
    $serial.Open()
    $null = Capture 'warmup' 3
    $serial.Write((Encode 'P'))
    $results.Add((Capture 'baseline20Hz' 10))
    if ($null -eq $script:hello -or $script:hello.Version -ne 2) { throw 'No protocol-v2 hello response.' }
    if ($script:hello.ResetCause -gt 15 -and $script:hello.ResetCause -ne 255) {
        throw "Invalid ATmega328P reset flags: $($script:hello.ResetCause). Check the startup hook in the compiled ELF."
    }
    $baselineRejects = $script:hello.Rejects
    $serial.Write((Encode 'C,hz,50'))
    $results.Add((Capture 'configured50Hz' 6))
    $serial.Write((Encode 'C,hz,20'))
    $null = Capture 'restore20Hz' 2
    $serial.Write("garbage`nP*00`n" + ('x' * 200) + "`n" + (Encode 'C,hz,0') + (Encode 'C,hz,51'))
    $serial.Write((Encode 'P'))
    $null = Capture 'malformedCommands' 3
    if ($script:hello.Rejects -ne $baselineRejects + 5) { throw "Expected five counted command rejects, got $($script:hello.Rejects - $baselineRejects)." }
    $serial.Write('P*')
    $null = Capture 'partialCommand' 1
    $serial.Write("50`r`n")
    $null = Capture 'partialCompletion' 2
    $serial.Write((Encode 'P'))
    $null = Capture 'preSoakDiagnostics' 1
    $soakGapsBefore = $script:gaps
    $soakBadBefore = $script:bad
    $results.Add((Capture 'soak20Hz' $CaptureSeconds))
    $serial.Write((Encode 'P'))
    $null = Capture 'finalDiagnostics' 2
    $report = [pscustomobject]@{
        At = [DateTimeOffset]::Now.ToString('o'); Port = $Port
        Results = $results.ToArray(); BadLines = $script:bad; SequenceGaps = $script:gaps
        SoakBadLines = $script:bad - $soakBadBefore; SoakSequenceGaps = $script:gaps - $soakGapsBefore
        Diagnostics = $script:hello; ExpectedInjectedRejects = 5
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'live-firmware-results.json')
    if ($results[0].Hz -lt 19 -or $results[0].Hz -gt 21 -or
        $results[1].Hz -lt 48 -or $results[1].Hz -gt 52 -or
        $results[2].Hz -lt 19 -or $results[2].Hz -gt 21 -or
        $report.BadLines -ne 0 -or $report.SequenceGaps -ne 0 -or
        $report.Diagnostics.Drops -ne 0 -or $report.Diagnostics.MaxLoopUs -ge 5000 -or
        @($results[2].StatusValues | Where-Object { ($_ -band 7) -ne 0 }).Count -gt 0) {
        throw 'Hardware acceptance check failed; inspect live-firmware-results.json.'
    }
    Write-Host 'PASS: rates, checksums, sequence continuity, command rejection/resync, sensor health and observed loop deadline.'
} finally {
    try { if ($serial.IsOpen) { $serial.Write((Encode 'C,hz,20')) } }
    finally { $serial.Dispose(); $raw.Dispose() }
}
