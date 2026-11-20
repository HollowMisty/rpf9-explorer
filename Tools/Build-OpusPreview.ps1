$ErrorActionPreference = 'Stop'
$compilerRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717'
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVersion = '10.0.26100.0'
$opusRoot = 'C:\Program Files\Epic Games\UE_5.7\Engine\Source\ThirdParty\libOpus\opus-1.3.1-12'
$opusLib = Join-Path $opusRoot 'bin\Win64\Release\opus.lib'
if (!(Test-Path -LiteralPath $opusLib)) { throw 'The installed trusted libopus SDK is required.' }
$env:INCLUDE = (Join-Path $opusRoot 'include') + ';' + (Join-Path $compilerRoot 'include') + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\ucrt") + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\um") + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\shared")
$env:LIB = (Join-Path $compilerRoot 'lib\x64') + ';' + (Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64") + ';' + (Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")
$env:PATH = (Join-Path $compilerRoot 'bin\Hostx64\x64') + ';' + $env:PATH
& (Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe') /nologo /O2 /EHsc /W4 /std:c++17 /MT /D_CRT_SECURE_NO_WARNINGS (Join-Path $PSScriptRoot 'OpusPreview.cpp') ('/Fo'+(Join-Path $PSScriptRoot 'OpusPreview.obj')) ('/Fe'+(Join-Path $PSScriptRoot 'opus-preview.exe')) $opusLib /link /NODEFAULTLIB:MSVCRT /IGNORE:4217
if ($LASTEXITCODE -ne 0) { throw 'Opus preview helper build failed.' }
