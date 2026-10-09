# Durango CustomServer launcher
# Compatible with Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "server\DurangoServer.csproj"
$OutputDir = Join-Path $Root "server\bin\Release\net9.0"
$ServerDll = Join-Path $OutputDir "DurangoServer.dll"
$LogDir = Join-Path $Root "logs"
$LogFile = Join-Path $LogDir "server-latest.log"

function Pause-Menu {
    Write-Host ""
    [void](Read-Host "Tekan Enter untuk kembali ke menu")
}

function Test-DotNet9 {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Host "[ERROR] .NET SDK tidak ditemukan. Instal .NET 9 SDK terlebih dahulu." -ForegroundColor Red
        Write-Host "Unduh: https://dotnet.microsoft.com/download/dotnet/9.0"
        return $false
    }
    $sdks = & dotnet --list-sdks 2>$null
    if (-not ($sdks | Where-Object { $_ -match '^9\.' })) {
        Write-Host "[ERROR] .NET SDK 9.x belum terpasang." -ForegroundColor Red
        Write-Host "Unduh: https://dotnet.microsoft.com/download/dotnet/9.0"
        return $false
    }
    return $true
}

function Build-Server {
    if (-not (Test-DotNet9)) { return $false }
    if (-not (Test-Path $Project)) {
        Write-Host "[ERROR] File proyek tidak ditemukan: $Project" -ForegroundColor Red
        return $false
    }
    Write-Host "[BUILD] Membangun server Release..." -ForegroundColor Cyan
    & dotnet build $Project --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build gagal dengan exit code $LASTEXITCODE." -ForegroundColor Red
        return $false
    }
    if (-not (Test-Path $ServerDll)) {
        Write-Host "[ERROR] Build selesai tetapi DLL tidak ditemukan: $ServerDll" -ForegroundColor Red
        return $false
    }
    Write-Host "[OK] Build server berhasil." -ForegroundColor Green
    return $true
}

function Start-Server {
    if (-not (Test-Path $ServerDll)) {
        Write-Host "[INFO] DLL belum ada; build server terlebih dahulu." -ForegroundColor Yellow
        if (-not (Build-Server)) { return }
    }
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    $escapedDll = $ServerDll.Replace("'", "''")
    $escapedLog = $LogFile.Replace("'", "''")
    $command = "& dotnet '$escapedDll' --name nx --gateway-port 8190 --game-port 8191 *>> '$escapedLog'"
    try {
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) -WorkingDirectory $Root -PassThru
        Start-Sleep -Seconds 2
        if ($process.HasExited) {
            Write-Host "[ERROR] Jendela proses server langsung berhenti. Periksa log: $LogFile" -ForegroundColor Red
        } else {
            Write-Host "[OK] Jendela server dibuka (PID launcher $($process.Id))." -ForegroundColor Green
            Write-Host "Gateway HTTP: http://127.0.0.1:8190"
            Write-Host "Game TCP:     8191"
            Write-Host "Log:          $LogFile"
            Write-Host "Untuk berhenti dengan aman, fokuskan jendela server lalu tekan Ctrl+C dan tunggu proses menyimpan data."
        }
    } catch {
        Write-Host "[ERROR] Tidak bisa membuka server: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Run-SelfTest {
    if (-not (Test-Path $ServerDll)) {
        if (-not (Build-Server)) { return }
    }
    Write-Host "[TEST] Menjalankan selftest pada port sementara 18290/18291..."
    & dotnet $ServerDll --selftest --gateway-port 18290 --game-port 18291
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[OK] Selftest selesai tanpa error." -ForegroundColor Green
    } else {
        Write-Host "[ERROR] Selftest gagal dengan exit code $LASTEXITCODE." -ForegroundColor Red
    }
}

function Show-LatestLog {
    if (Test-Path $LogFile) {
        Get-Content -Path $LogFile -Tail 100
    } else {
        Write-Host "Log belum tersedia. Jalankan server terlebih dahulu." -ForegroundColor Yellow
    }
}

while ($true) {
    Clear-Host
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "       DURANGO CUSTOM SERVER" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "1. Jalankan server"
    Write-Host "2. Build server"
    Write-Host "3. Build lalu jalankan server"
    Write-Host "4. Selftest server"
    Write-Host "5. Buka folder game (jika tersedia)"
    Write-Host "6. Lihat log server terbaru"
    Write-Host "0. Keluar"
    Write-Host ""
    $choice = Read-Host "Pilih menu"
    switch ($choice) {
        "1" { Start-Server; Pause-Menu }
        "2" { [void](Build-Server); Pause-Menu }
        "3" { if (Build-Server) { Start-Server }; Pause-Menu }
        "4" { Run-SelfTest; Pause-Menu }
        "5" {
            $gameDir = Join-Path $Root "game"
            if (Test-Path $gameDir) {
                Start-Process explorer.exe -ArgumentList $gameDir
            } else {
                Write-Host "Folder game tidak ada. Salin file game yang sah ke folder game lokal (folder ini tidak disimpan di Git)." -ForegroundColor Yellow
                Pause-Menu
            }
        }
        "6" { Show-LatestLog; Pause-Menu }
        "0" { break }
        default { Write-Host "Pilihan tidak valid."; Start-Sleep -Seconds 1 }
    }
    if ($choice -eq "0") { break }
}
