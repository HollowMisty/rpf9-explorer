param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$rpfPublishPath = [IO.Path]::GetFullPath($OutputDirectory)
dotnet publish (Join-Path $PSScriptRoot 'App\RpfPreview.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $rpfPublishPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'rpf9-explorer publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $rpfPublishPath 'README.md')
$rpfScreenshotOutput = Join-Path $rpfPublishPath 'docs\screenshots'
New-Item -ItemType Directory -Path $rpfScreenshotOutput -Force | Out-Null
foreach ($rpfScreenshotName in @('texture-search.png','texture-dictionary.png','model-preview.png','staged-texture.png')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('docs\screenshots\' + $rpfScreenshotName)) -Destination (Join-Path $rpfScreenshotOutput $rpfScreenshotName)
}
foreach ($rpfToolName in @('bink-preview.exe','bink-player.exe','opus-preview.exe','ffmpeg.exe')) {
    $rpfToolSource = Join-Path $PSScriptRoot ('Tools\' + $rpfToolName)
    if (Test-Path -LiteralPath $rpfToolSource) {
        $rpfToolsOutput = Join-Path $rpfPublishPath 'Tools'
        New-Item -ItemType Directory -Path $rpfToolsOutput -Force | Out-Null
        Copy-Item -LiteralPath $rpfToolSource -Destination (Join-Path $rpfToolsOutput $rpfToolName)
    }
}
Write-Host ('Built: ' + (Join-Path $rpfPublishPath 'rpf9-explorer.exe'))
