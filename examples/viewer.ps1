# Live view of agent runs (stream-json logs produced by the watcher scripts).
# Run in a desktop window: powershell -File viewer.ps1 -RunsDir <STATE_PATH>\logs\runs
# Follows the newest run-*.jsonl and switches when a newer one appears.
# -Once: process existing content of the newest file and exit (for testing).
param(
  [Parameter(Mandatory = $true)][string]$RunsDir,
  [switch]$Once
)
$ErrorActionPreference = 'SilentlyContinue'

function Show-Line([string]$line) {
  if (-not $line.Trim()) { return }
  try { $j = $line | ConvertFrom-Json } catch { return }
  $ts = Get-Date -Format 'HH:mm:ss'
  switch ($j.type) {
    'system' {
      if ($j.subtype -eq 'init') { Write-Host "[$ts] === START (session $($j.session_id), model $($j.model)) ===" -ForegroundColor Cyan }
    }
    'assistant' {
      foreach ($c in $j.message.content) {
        if ($c.type -eq 'text' -and $c.text.Trim()) {
          Write-Host "[$ts] " -NoNewline -ForegroundColor DarkGray
          Write-Host $c.text.Trim() -ForegroundColor White
        } elseif ($c.type -eq 'tool_use') {
          $inp = ($c.input | ConvertTo-Json -Compress -Depth 3)
          if ($inp.Length -gt 160) { $inp = $inp.Substring(0, 160) + '...' }
          Write-Host "[$ts]   -> $($c.name) $inp" -ForegroundColor Yellow
        }
      }
    }
    'user' {
      foreach ($c in $j.message.content) {
        if ($c.type -eq 'tool_result') {
          $txt = if ($c.content -is [string]) { $c.content } else { ($c.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text }
          $len = if ($txt) { $txt.Length } else { 0 }
          $prev = if ($txt) { ($txt -replace '\s+', ' ').Substring(0, [Math]::Min(100, $txt.Length)) } else { '' }
          $color = if ($c.is_error) { 'Red' } else { 'DarkGreen' }
          Write-Host "[$ts]   <- result ($len ch.) $prev" -ForegroundColor $color
        }
      }
    }
    'result' {
      $snippet = if ($j.result) { $j.result.Substring(0, [Math]::Min(300, $j.result.Length)) } else { '' }
      Write-Host "[$ts] === END ($([math]::Round($j.duration_ms/1000,1))s, $($j.num_turns) turns) ===" -ForegroundColor Cyan
      if ($snippet) { Write-Host $snippet -ForegroundColor Green }
    }
  }
}

Write-Host "viewer: watching $RunsDir (Ctrl+C to quit)" -ForegroundColor DarkGray
$current = $null; $pos = 0
while ($true) {
  $newest = Get-ChildItem "$RunsDir\run-*.jsonl" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($newest -and $newest.FullName -ne $current) {
    $current = $newest.FullName; $pos = 0
    Write-Host "`n########## $($newest.Name) ##########" -ForegroundColor Magenta
  }
  if ($current) {
    $fs = [IO.File]::Open($current, 'Open', 'Read', 'ReadWrite')
    try {
      if ($fs.Length -gt $pos) {
        $fs.Position = $pos
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        while (-not $sr.EndOfStream) { Show-Line $sr.ReadLine() }
        $pos = $fs.Length
      }
    } finally { $fs.Dispose() }
  }
  if ($Once) { break }
  Start-Sleep -Seconds 2
}
