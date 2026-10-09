# Durango Custom Server

A .NET 9 experimental server project for Durango: Wild Lands.

Server eksperimen berbasis .NET 9 untuk Durango: Wild Lands.

> **Distribution note / Catatan distribusi:** Game files are not included in Git. Prepare them separately and only distribute files you are authorized to use. / File game tidak disertakan di Git. Siapkan secara terpisah dan distribusikan hanya file yang memang boleh kamu gunakan.

## Repository structure / Struktur repository

- `server/` — .NET 9 server, protocol, world, gateway, and available game data. / Server .NET 9, protokol, dunia, gateway, dan data game yang tersedia.
- `server/admin/` — browser-based server administration panel. / Panel administrasi server melalui browser.
- `tools/start-server.ps1` — Windows build/start/selftest menu. / Menu Windows untuk build, menjalankan server, dan selftest.
- `เปิดเซิร์ฟ.bat` — Windows launcher entry point. / File pembuka launcher Windows.
- `game/` — local game files only; not tracked by Git. / Hanya untuk file game lokal; tidak dilacak oleh Git.

Some client source files, roadmap documents, and older utility scripts mentioned by earlier documentation are not present in this repository snapshot. The launcher does not attempt to build a client. / Beberapa source client, dokumen roadmap, dan skrip utilitas lama yang disebut pada dokumentasi terdahulu tidak tersedia di snapshot repository ini. Launcher tidak mencoba membangun client.

## Languages / Bahasa

- The admin panel defaults to **English** and has a **Bahasa Indonesia** toggle. / Panel admin menggunakan **Bahasa Inggris** sebagai default dan menyediakan tombol **Bahasa Indonesia**.
- Human-readable server logs, admin API errors, configuration descriptions, and the animal/island names in `config.json` have been translated to English or bilingual English/Indonesian. / Log server, pesan error API admin, deskripsi konfigurasi, serta nama hewan dan pulau di `config.json` telah diterjemahkan ke Bahasa Inggris atau format bilingual Inggris/Indonesia.
- The existing `server/data/locales/th/LC_MESSAGES/messages.mo` is a compiled gettext catalog used for game strings. It is intentionally preserved for now: deleting or replacing this binary catalog without generating and validating replacement catalogs could make server-sent item names fall back to the original game strings. Full in-game English/Indonesian localization requires separate, validated catalogs and testing. / File `server/data/locales/th/LC_MESSAGES/messages.mo` adalah katalog gettext terkompilasi untuk teks game. File ini sementara dipertahankan: menghapus atau menggantinya tanpa membuat dan memvalidasi katalog pengganti dapat membuat nama item yang dikirim server kembali ke teks asli game. Pelokalan penuh di dalam game ke Bahasa Inggris/Indonesia memerlukan katalog terpisah yang tervalidasi dan pengujian.

## Requirements / Persyaratan

- Windows 10/11
- .NET 9 SDK
- PowerShell

Download the .NET 9 SDK / Unduh .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0

## Start on Windows / Menjalankan di Windows

1. Clone or download the repository. / Clone atau unduh repository.
2. Install the .NET 9 SDK. / Instal .NET 9 SDK.
3. Double-click `เปิดเซิร์ฟ.bat`. / Klik dua kali `เปิดเซิร์ฟ.bat`.
4. Select menu `2` to build, or `3` to build and start. / Pilih menu `2` untuk build, atau `3` untuk build lalu menjalankan server.
5. Select menu `4` to run the selftest. / Pilih menu `4` untuk menjalankan selftest.

| Menu | English | Bahasa Indonesia |
|---|---|---|
| 1 | Start server (HTTP gateway 8190, game TCP 8191) | Jalankan server (gateway HTTP 8190, game TCP 8191) |
| 2 | Build Release server | Build server Release |
| 3 | Build and start server | Build lalu jalankan server |
| 4 | Run selftest on temporary ports 18290/18291 | Jalankan selftest pada port sementara 18290/18291 |
| 5 | Open local game folder if present | Buka folder game lokal jika tersedia |
| 6 | Show the last 100 log lines | Tampilkan 100 baris log terakhir |
| 0 | Exit menu | Keluar dari menu |

The server runs in a separate window. To stop it safely, focus that window, press **Ctrl+C**, and wait for the save/shutdown process to finish. Avoid force-closing it. / Server berjalan di jendela terpisah. Untuk menghentikannya dengan aman, fokuskan jendela server, tekan **Ctrl+C**, lalu tunggu proses penyimpanan dan shutdown selesai. Hindari menutup proses secara paksa.

Launcher logs are stored locally at `logs/server-latest.log`; the logs folder is not committed to Git. / Log launcher disimpan secara lokal di `logs/server-latest.log`; folder log tidak dikomit ke Git.

## Build from terminal / Build dari terminal

Run from the repository root / Jalankan dari folder utama repository:

```powershell
dotnet --list-sdks
dotnet build server/DurangoServer.csproj -c Release
```

If the build fails, keep the complete terminal output for diagnosis. Do not delete `bin`, `obj`, or save data without identifying the cause first. / Jika build gagal, simpan seluruh output terminal untuk diagnosis. Jangan menghapus `bin`, `obj`, atau data save sebelum penyebabnya diketahui.

## Server ports / Port server

| Port | Purpose / Fungsi |
|---|---|
| 8190/TCP | HTTP gateway, including `/knock`, `/sessions`, and `/entry` |
| 8191/TCP | Game connection and handshake |

For same-PC testing, use `127.0.0.1`. For other devices, configure gateway binding, Windows Firewall, router/VPS firewall, and the client's gateway address. Do not expose the server publicly until access controls and security have been reviewed. / Untuk pengujian pada PC yang sama, gunakan `127.0.0.1`. Untuk perangkat lain, atur binding gateway, Windows Firewall, firewall router/VPS, dan alamat gateway client. Jangan membuka server ke publik sebelum kontrol akses dan keamanan diperiksa.

## Validation status / Status validasi

The Windows launcher was restored because `เปิดเซิร์ฟ.bat` calls `tools/start-server.ps1`. The language and documentation changes have not been validated by a .NET build or browser test in this environment. Run menu 2 and menu 4 on Windows with .NET 9 SDK before public deployment. / Launcher Windows dipulihkan karena `เปิดเซิร์ฟ.bat` memanggil `tools/start-server.ps1`. Perubahan bahasa dan dokumentasi belum divalidasi melalui build .NET atau pengujian browser di lingkungan ini. Jalankan menu 2 dan menu 4 di Windows dengan .NET 9 SDK sebelum server dipublikasikan.
