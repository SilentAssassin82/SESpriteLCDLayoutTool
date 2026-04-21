$file = "SESpriteLCDLayoutTool\Services\CodeNavigationService.cs"
$lines = [IO.File]::ReadAllLines($file)
$out = New-Object System.Collections.Generic.List[string]
$removed = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^\s*codeBox\.EnsureVisible\(') {
        $next = if ($i + 1 -lt $lines.Count) { $lines[$i+1] } else { "" }
        if ($next -match '^\s*codeBox\.EnsureVisible\(') {
            $removed++
            continue
        }
    }
    $out.Add($line)
}
[IO.File]::WriteAllLines($file, $out, [Text.Encoding]::UTF8)
Write-Host "Removed $removed duplicate EnsureVisible calls"
