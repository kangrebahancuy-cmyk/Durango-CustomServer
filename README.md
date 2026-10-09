# Durango Custom Server

Server eksperimental untuk Durango: Wild Lands, dikembangkan dengan .NET 9.

> Catatan: repository ini terutama berisi source code server. File game tidak disertakan di Git dan harus disiapkan secara terpisah sesuai hak penggunaan yang berlaku.

## Struktur repository

- `server/` — source code server .NET 9, protocol, world, gateway, dan data yang tersedia di repository.
- `tools/start-server.ps1` — menu Windows untuk build, menjalankan server, dan selftest.
- `เปิดเซิร์ฟ.bat` — pembuka menu pada Windows.
- `game/` — folder lokal untuk file game; diabaikan oleh Git dan tidak disertakan dalam repository.

Source client, dokumen roadmap, dan beberapa skrip lama yang disebut dalam dokumentasi sebelumnya tidak tersedia pada branch ini. Karena itu, perintah build client dan scan-protocol tidak disediakan oleh launcher ini.

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
5. Pilih menu `4` untuk menjalankan selftest.

Menu launcher:

| Menu | Fungsi |
|---|---|
| 1 | Jalankan server (gateway HTTP 8190, game TCP 8191) |
| 2 | Build server Release |
| 3 | Build lalu jalankan server |
| 4 | Jalankan selftest pada port sementara 18290/18291 |
| 5 | Buka folder game lokal jika tersedia |
| 6 | Lihat 100 baris log terakhir |
| 0 | Keluar dari menu |

Jendela server berjalan terpisah. Untuk menghentikan server dengan aman, fokuskan jendela server lalu tekan **Ctrl+C** dan tunggu sampai proses selesai menyimpan data. Hindari mematikan proses secara paksa.

Log disimpan secara lokal di `logs/server-latest.log`; folder log tidak dikomit ke Git.

## Build dari terminal

Jalankan dari folder utama repository:

```powershell
dotnet --list-sdks
dotnet build server/DurangoServer.csproj -c Release
```

Jika build gagal, simpan seluruh pesan error dari terminal agar penyebabnya dapat diperiksa. Jangan menghapus folder `bin`, `obj`, atau data save sebelum ada diagnosis.

## Port server

| Port | Fungsi |
|---|---|
| 8190/TCP | HTTP gateway, termasuk endpoint seperti `/knock`, `/sessions`, dan `/entry` |
| 8191/TCP | Koneksi game dan handshake |

Untuk pengujian pada komputer yang sama, gunakan `127.0.0.1`. Agar perangkat lain dapat terhubung, pastikan binding gateway, Windows Firewall, firewall router/VPS, serta alamat gateway pada client dikonfigurasi dengan benar. Jangan membuka port ke internet sebelum akses dan keamanan ditinjau.

## Status

File `เปิดเซิร์ฟ.bat` memanggil `tools/start-server.ps1`, tetapi skrip tersebut sebelumnya tidak ada di repository. Skrip launcher kini ditambahkan kembali pada branch perbaikan ini.

Build dan selftest belum dijalankan di lingkungan ini. Jalankan menu 2 dan menu 4 pada Windows dengan .NET 9 SDK sebelum menggunakan server secara publik.
