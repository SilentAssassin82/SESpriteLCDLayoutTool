$file = "SESpriteLCDLayoutTool\Services\CodeNavigationService.cs"
$lines = [IO.File]::ReadAllLines($file)
$out = New-Object System.Collections.Generic.List[string]
$inserted = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^\s*codeBox\.ScrollToCaret\(\);') {
        $prev = if ($i -gt 0) { $lines[$i-1] } else { "" }
        if ($prev -notmatch 'EnsureVisible') {
            $selectLine = ""
            for ($j = $i-1; $j -ge ([Math]::Max(0, $i-3)); $j--) {
                if ($lines[$j] -match 'codeBox\.Select\(') { $selectLine = $lines[$j]; break }
            }
            $posVar = "lineStart"
            if ($selectLine -match 'codeBox\.Select\(\s*([^,]+),') { $posVar = $Matches[1].Trim() }
            $indent = [regex]::Match($line, '^\s*').Value
            $out.Add("${indent}codeBox.EnsureVisible(codeBox.LineFromPosition($posVar));")
            $inserted++
        }
    }
    $out.Add($line)
}
[IO.File]::WriteAllLines($file, $out, [Text.Encoding]::UTF8)
Write-Host "Inserted $inserted EnsureVisible calls"
