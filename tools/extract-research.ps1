# Dev helper: extracts research agent reports from a workflow journal into docs/research/*.md
param([Parameter(Mandatory)][string]$Journal)
$out = Join-Path $PSScriptRoot '..\docs\research'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$p = $Journal
if (-not $p.StartsWith('\\?\')) { $p = '\\?\' + $p }
$labels = @{}
foreach ($line in [IO.File]::ReadAllLines($p, [Text.Encoding]::UTF8)) {
    $o = $line | ConvertFrom-Json
    if ($o.type -eq 'started') { $labels[$o.key] = $o.label }
    if ($o.type -eq 'result') {
        $label = $labels[$o.key]
        if (-not $label) { $label = 'research:unknown' }
        $name = $label -replace '^research:', ''
        $text = $o.result
        if ($text -isnot [string]) { $text = $text | ConvertTo-Json -Depth 20 }
        [IO.File]::WriteAllText((Join-Path $out "$name.md"), $text, (New-Object Text.UTF8Encoding $false))
        "$name.md  $($text.Length) chars"
    }
}
