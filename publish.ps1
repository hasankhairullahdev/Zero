# ZERO Publish Script
# Publishes all projects as self-contained Windows executables
# Output: publish/ folder — copy this anywhere and run Zero.Core.exe

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$out     = Join-Path $root "publish"
$runtime = "win-x64"
$config  = "Release"

Write-Host "`n=== ZERO Publish ===" -ForegroundColor Cyan

# Stop ZERO if running (it locks native dlls in publish/)
$zeroProc = Get-Process -Name "Zero.Core" -ErrorAction SilentlyContinue
if ($zeroProc) {
    Write-Host "Stopping Zero.Core.exe..." -ForegroundColor Yellow
    $zeroProc | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Host "  -> stopped." -ForegroundColor Green
}

# Clean output
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

# MCP servers — single-file exe, no native libs
$mcpProjects = @(
    @{ Name = "Zero.FileManager";    Path = "src/Zero.FileManager/Zero.FileManager.csproj" },
    @{ Name = "Zero.SystemControl";  Path = "src/Zero.SystemControl/Zero.SystemControl.csproj" },
    @{ Name = "Zero.WebAccess";      Path = "src/Zero.WebAccess/Zero.WebAccess.csproj" }
)

foreach ($proj in $mcpProjects) {
    Write-Host "`n--- Publishing $($proj.Name)..." -ForegroundColor Yellow

    $projOut = Join-Path $out "_tmp_$($proj.Name)"

    dotnet publish $proj.Path `
        --configuration $config `
        --runtime $runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -o $projOut

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: publish failed for $($proj.Name)" -ForegroundColor Red
        exit 1
    }

    $exeFile = Join-Path $projOut "$($proj.Name).exe"
    if (Test-Path $exeFile) {
        Copy-Item $exeFile $out
        Write-Host "  -> $($proj.Name).exe" -ForegroundColor Green
    }

    Remove-Item $projOut -Recurse -Force
}

# Zero.Core — native Whisper/Kokoro libs must sit beside the exe (not bundled)
Write-Host "`n--- Publishing Zero.Core..." -ForegroundColor Yellow

$coreOut = Join-Path $out "_tmp_Zero.Core"

dotnet publish src/Zero.Core/Zero.Core.csproj `
    --configuration $config `
    --runtime $runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none `
    -o $coreOut

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: publish failed for Zero.Core" -ForegroundColor Red
    exit 1
}

# Copy everything from coreOut to publish root (exe + all native dlls + voices/)
Get-ChildItem $coreOut | Copy-Item -Destination $out -Recurse -Force
Write-Host "  -> Zero.Core.exe + native libs" -ForegroundColor Green

# Merge CUDA Whisper runtime into runtimes\win-x64\ (published separately under runtimes\cuda\win-x64)
$cudaSrc = Join-Path $coreOut "runtimes\cuda\win-x64"
$cudaDst = Join-Path $out "runtimes\win-x64"
if (Test-Path $cudaSrc) {
    New-Item -ItemType Directory -Path $cudaDst -Force | Out-Null
    Copy-Item "$cudaSrc\*" $cudaDst -Force
    Write-Host "  -> runtimes\win-x64\ggml-cuda-whisper.dll" -ForegroundColor Green
} else {
    Write-Host "  [!] CUDA Whisper runtime not found - STT will use CPU" -ForegroundColor Yellow
}

# Copy CUDA 12 runtime DLLs required by ggml-cuda-whisper.dll
$cuda12Bin = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6\bin"
if (Test-Path $cuda12Bin) {
    foreach ($dll in @("cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll")) {
        $src = Join-Path $cuda12Bin $dll
        if (Test-Path $src) {
            Copy-Item $src $cudaDst -Force
            Write-Host "  -> $dll" -ForegroundColor Green
        }
    }
} else {
    Write-Host "  [!] CUDA 12.6 not found at $cuda12Bin - Whisper CUDA may fail" -ForegroundColor Yellow
}

# Ensure voices/ folder is present (KokoroSharp looks for it beside the exe)
$voicesSrc = Join-Path $coreOut "voices"
if (-not (Test-Path $voicesSrc)) {
    # Fall back to build output if publish didn't include it
    $voicesSrc = "src/Zero.Core/bin/Release/net10.0-windows/voices"
}
if (Test-Path $voicesSrc) {
    Copy-Item $voicesSrc $out -Recurse -Force
    Write-Host "  -> voices/" -ForegroundColor Green
} else {
    Write-Host "  [!] voices/ folder not found - Kokoro TTS may fail. Run once in dev mode first to download voices." -ForegroundColor Yellow
}

Remove-Item $coreOut -Recurse -Force

# Copy kokoro.onnx model (must sit beside exe — avoids download to system32)
Write-Host "`n--- Copying kokoro.onnx..." -ForegroundColor Yellow
$onnxSrc = Join-Path $root "kokoro.onnx"
if (Test-Path $onnxSrc) {
    Copy-Item $onnxSrc $out
    Write-Host "  -> kokoro.onnx" -ForegroundColor Green
} else {
    Write-Host "  [!] kokoro.onnx not found at project root - Kokoro TTS will try to auto-download on first run." -ForegroundColor Yellow
}

# Copy config folder
Write-Host "`n--- Copying config..." -ForegroundColor Yellow
Copy-Item (Join-Path $root "config") $out -Recurse
Write-Host "  -> config/" -ForegroundColor Green

Write-Host "`n=== Done! ===" -ForegroundColor Cyan
Write-Host "Output folder: $out"
Write-Host ""
Write-Host "To run ZERO:"
Write-Host "  1. Make sure Ollama is running:  ollama run qwen3:14b"
Write-Host "  2. Launch:                        $out\Zero.Core.exe"
Write-Host "  3. Enable auto-start: Right-click tray icon, Start with Windows"
Write-Host ""
