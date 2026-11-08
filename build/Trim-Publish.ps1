# Makes the publish folder easy to scan: keep MeetSimple.exe obvious, drop unused RID
# folders and debugger leftovers, and leave a short note for end users.
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir
)

$ErrorActionPreference = "Stop"
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path

# Leave only the native Whisper layouts this win-x64 build needs.
$runtimes = Join-Path $PublishDir "runtimes"
if (Test-Path -LiteralPath $runtimes) {
    Get-ChildItem -LiteralPath $runtimes -Directory | ForEach-Object {
        if ($_.Name -eq "vulkan") {
            Get-ChildItem -LiteralPath $_.FullName -Directory |
                Where-Object { $_.Name -ne "win-x64" } |
                Remove-Item -Recurse -Force
        }
        elseif ($_.Name -ne "win-x64") {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }
}

# Drop satellite culture folders if any slipped through, plus debug helpers.
$keepTop = @(
    "MeetSimple.exe",
    "runtimes",
    "D3DCompiler_47_cor3.dll",
    "PenImc_cor3.dll",
    "PresentationNative_cor3.dll",
    "vcruntime140_cor3.dll",
    "wpfgfx_cor3.dll",
    "Read me.txt"
)

Get-ChildItem -LiteralPath $PublishDir -Force | ForEach-Object {
    $name = $_.Name
    if ($keepTop -contains $name) { return }
    # .NET culture satellite folders (cs, de, zh-Hans, …)
    if ($_.PSIsContainer -and $name -match '^[a-z]{2}(-[A-Za-z]+)?$') {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
        return
    }
    if ($name -match '\.(pdb|xml)$' -or
        $name -like 'createdump*' -or
        $name -like 'mscordac*' -or
        $name -like 'mscordbi*' -or
        $name -like 'Microsoft.DiaSymReader*') {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$readme = @"
Meet Simple
===========

Double-click  MeetSimple.exe  to start.

The other files next to it (and the runtimes folder) are required support
libraries. Keep them together — do not move the .exe out on its own.

Nothing is uploaded. Transcription and summaries run on this computer.
"@
Set-Content -LiteralPath (Join-Path $PublishDir "Read me.txt") -Value $readme -Encoding utf8

$fileCount = (Get-ChildItem -LiteralPath $PublishDir -Recurse -File).Count
$topCount = (Get-ChildItem -LiteralPath $PublishDir -Force).Count
Write-Host "Publish folder ready: $topCount top-level items, $fileCount files total."
