$ErrorActionPreference = 'Stop'

$target = $args[0]
$data   = $args[1]

$text = [System.IO.File]::ReadAllText($target, [System.Text.Encoding]::UTF8)
if ($text.Contains("`r`n")) { $nl = "`r`n"; $nlName = 'CRLF' } else { $nl = "`n"; $nlName = 'LF' }
Write-Output ("target  = " + $target)
Write-Output ("newline = " + $nlName + " ; length = " + $text.Length)

$raw = [System.IO.File]::ReadAllText($data, [System.Text.Encoding]::UTF8)

$pattern = "(?s)@@@(?<tag>[^\r\n]+)\r?\n<<<OLD\r?\n(?<old>.*?)\r?\n>>>OLD\r?\n<<<NEW\r?\n(?<new>.*?)\r?\n>>>NEW"
$ms = [regex]::Matches($raw, $pattern)
Write-Output ("edits parsed = " + $ms.Count)
Write-Output ''

$fail = 0
foreach ($m in $ms) {
    $tag = $m.Groups['tag'].Value
    $old = ($m.Groups['old'].Value -replace "`r`n", "`n") -replace "`n", $nl
    $new = ($m.Groups['new'].Value -replace "`r`n", "`n") -replace "`n", $nl

    $c = 0; $i = 0
    while (($i = $text.IndexOf($old, $i)) -ge 0) { $c++; $i += $old.Length }

    if ($c -ne 1) {
        Write-Output ("  " + $tag + "  FAIL  matches=" + $c)
        $fail++
        continue
    }
    $text = $text.Replace($old, $new)
    Write-Output ("  " + $tag + "  OK")
}

Write-Output ''
if ($fail -gt 0) { throw ("aborted: " + $fail + " edit(s) did not match exactly once") }

[System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("written. new length = " + $text.Length)
