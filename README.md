# Durango Custom Server

เซิร์ฟเวอร์ทดลองสำหรับ Durango: Wild Lands พัฒนาด้วย .NET 9

> หมายเหตุ: repository นี้เก็บซอร์สเซิร์ฟเวอร์เป็นหลัก ไฟล์เกมจริงไม่ได้อยู่ใน Git และต้องจัดเตรียมแยกต่างหากอย่างถูกต้องตามสิทธิ์การใช้งาน

## โครงสร้าง repository

- `server/` — โค้ดเซิร์ฟเวอร์ .NET 9, protocol, world, gateway และ data ที่มีอยู่ใน repository
- `tools/start-server.ps1` — เมนู Windows สำหรับ build, start และ selftest
- `เปิดเซิร์ฟ.bat` — ตัวเปิดเมนูบน Windows
- `game/` — โฟลเดอร์ lokal untuk file game; diabaikan oleh Git dan tidak disertakan di repository

ไฟล์ client source, dokumen roadmap, dan skrip lama yang disebut di dokumentasi sebelumnya tidak tersedia di branch ini. Karena itu, jangan menganggap perintah build client atau scan-protocol tersedia.

## Persyaratan

- Windows 10/11
- .NET 9 SDK
- PowerShell (tersedia di Windows)

Unduh .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0

## Menjalankan di Windows

1. Clone atau unduh repository.
2. Pastikan .NET 9 SDK terpasang.
3. Klik dua kali `เปิดเซิร์ฟ.bat`.
4. Pilih menu `2` untuk build server, atau `3` untuk build lalu menjalankan server.
5. Pilih menu `4` untuk selftest.

Menu launcher:
- `1` Jalankan server (gateway HTTP 8190, game TCP 8191)
- `2` Build server Release
- `3` Build lalu jalankan server
- `4` Jalankan selftest menggunakan port sementara 18290/18291
- `5` Buka folder game lokal jika tersedia
- `6` Lihat 100 baris log terakhir
- `0` Keluar dari menu

Jendela server berjalan terpisah. Untuk menghentikan dengan aman, fokuskan jendela server lalu tekan **Ctrl+C** dan tunggu pesan bahwa data selesai disimpan. Jangan mematikan proses secara paksa kecuali diperlukan.

Log launcher disimpan di `logs/server-latest.log` pada komputer lokal; folder log tidak dikomit ke Git.

## Build dari terminal

Jalankan dari folder utama repository:

```powershell
dotnet --list-sdks
dotnet build server/DurangoServer.csproj -c Release
```

Jika build gagal, salin seluruh pesan error pertama dari terminal untuk diperiksa. Jangan langsung menghapus folder `bin`, `obj`, atau data save sebelum ada diagnosis.

## Port server

| Port | Fungsi |
|---|---|
| 8190/TCP | HTTP gateway, termasuk endpoint seperti `/knock`, `/sessions`, dan `/entry` |
| 8191/TCP | Koneksi game dan handshake |

Untuk pengujian di komputer yang sama, gunakan `127.0.0.1`. Agar perangkat lain dapat terhubung, pastikan binding gateway, Windows Firewall, router/VPS firewall, dan alamat gateway client dikonfigurasi sesuai lingkungan. Jangan membuka port ke internet sebelum pengaturan akses dan keamanan ditinjau.

## Status

Launcher Windows telah ditambahkan kembali karena file `เปิดเซิร์ฟ.bat` memanggil `tools/start-server.ps1`, sedangkan skrip tersebut sebelumnya tidak ada di repository. Build dan selftest tetap perlu dijalankan pada mesin Windows dengan .NET 9 SDK; perubahan pada GitHub tidak berarti build sudah teruji di mesin pengguna.
