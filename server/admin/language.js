/* Admin panel localization: English (default) and Indonesian. */
(function () {
  'use strict';
  const id = {
    'Bahasa Indonesia':'English',
    'Sign in':'Masuk',
    'Enter the configured --admin-token':'Masukkan --admin-token yang sudah dikonfigurasi',
    'Economy':'Ekonomi',
    'Log out':'Keluar',
    'Loading...':'Memuat...',
    'Edit config.json and click Save to apply changes.':'Edit config.json lalu klik Save untuk menerapkan perubahan.',
    'Enable or disable game systems.':'Aktifkan atau nonaktifkan sistem game.',
    'One name or entity ID per line':'Satu nama atau entity ID per baris',
    'Economy monitoring — the server uses T Stone':'Pemantauan ekonomi — server menggunakan T Stone',
    'Refresh':'Segarkan',
    'Total currency':'Total mata uang',
    'Total characters':'Total karakter',
    'Accounts with funds':'Akun yang memiliki saldo',
    'Average per character':'Rata-rata per karakter',
    'Median balance':'Saldo median',
    'Highest balance':'Saldo tertinggi',
    'Change since previous refresh':'Perubahan sejak penyegaran sebelumnya',
    'Refresh twice to compare changes.':'Segarkan dua kali untuk membandingkan perubahan.',
    'Balance distribution':'Distribusi saldo',
    'Top holders':'Pemilik saldo terbesar',
    'Select the game data you want to inspect.':'Pilih data game yang ingin diperiksa.',
    '— Select data —':'— Pilih data —',
    'Enter an announcement':'Masukkan pengumuman',
    'Send announcement':'Kirim pengumuman',
    'When enabled, new players cannot join; current players can continue.':'Jika aktif, pemain baru tidak dapat bergabung; pemain yang sudah online dapat melanjutkan.',
    'Enable maintenance':'Aktifkan maintenance',
    'Disable maintenance':'Nonaktifkan maintenance',
    'Reload config.json without restarting the server.':'Muat ulang config.json tanpa memulai ulang server.',
    'Dashboard':'Dasbor',
    'Config Editor':'Editor Konfigurasi',
    'Feature Toggles':'Pengaturan Fitur',
    'Islands':'Pulau',
    'Players':'Pemain',
    'Game Data':'Data Game',
    'Server Actions':'Aksi Server',
    'Players Online':'Pemain Online',
    'Max Players':'Maksimum Pemain',
    'Uptime':'Waktu Aktif',
    'Worlds Loaded':'Dunia Dimuat',
    'Last Save':'Penyimpanan Terakhir',
    'Save Failures':'Kegagalan Penyimpanan',
    'Loop Errors':'Error Loop',
    'Online Players':'Pemain Online',
    'Unhandled Packets':'Paket Tanpa Handler',
    'Reload Config':'Muat Ulang Konfigurasi',
    'Save Config':'Simpan Konfigurasi',
    'Save Features':'Simpan Fitur',
    'Reload':'Muat Ulang',
    'Save Islands':'Simpan Pulau',
    'Change since previous refresh':'Perubahan sejak penyegaran sebelumnya',
    'Total currency':'Total mata uang',
    'Characters':'Karakter',
    'Accounts':'Akun',
    'Median':'Median',
    'Name':'Nama',
    'Online':'Online',
    'No online players':'Tidak ada pemain online',
    'No banned players':'Tidak ada pemain yang diblokir',
    'Token is invalid or the server did not respond':'Token salah atau server tidak merespons',
    'Could not connect to server: ':'Tidak dapat terhubung ke server: ',
    'Total funds changed ':'Perubahan total saldo ',
    'in ':'dalam ',
    ' seconds (≈ ':' detik (≈ ',
    ' /hr.)<br>Characters changed ':' /jam)<br>Perubahan karakter ',
    'No characters have funds yet':'Belum ada karakter yang memiliki saldo',
    'Failed to load: ':'Gagal memuat: ',
    'Could not load config: ':'Tidak dapat memuat konfigurasi: ',
    'Invalid JSON: ':'JSON tidak valid: ',
    'Saving...':'Menyimpan...',
    '✓ Saved successfully':'✓ Berhasil disimpan',
    'Unknown reason':'Alasan tidak diketahui',
    'Kick ${name} (${entityId})?':'Keluarkan ${name} (${entityId})?',
    'Kicked by administrator':'Dikeluarkan oleh administrator',
    'Kicked successfully':'Berhasil mengeluarkan pemain',
    'Failed to kick':'Gagal mengeluarkan pemain',
    'Ban ${name} (${entityId})? The player will be kicked immediately':'Blokir ${name} (${entityId})? Pemain akan langsung dikeluarkan',
    'Ban reason:':'Alasan pemblokiran:',
    'Banned by administrator':'Diblokir oleh administrator',
    'Banned successfully':'Berhasil memblokir pemain',
    'Failed to ban: ':'Gagal memblokir pemain: ',
    'Please enter a message':'Silakan masukkan pesan',
    '✓ Sent successfully to ${result.sent} players':'✓ Berhasil dikirim ke ${result.sent} pemain',
    '✓ Maintenance Mode enabled':'✓ Mode maintenance diaktifkan',
    '✓ Maintenance Mode disabled':'✓ Mode maintenance dinonaktifkan',
    'Reloading...':'Memuat ulang...',
    '✓ Reload successful':'✓ Berhasil dimuat ulang',
    'Spawn (Animals)':'Spawn (Hewan)',
    'Starter (Starting Items)':'Starter (Item Awal)'
  };
  const en = {'Masuk':'Sign in','Keluar':'Log out','Ekonomi':'Economy','Memuat...':'Loading...'};
  let language = 'en';
  const originalText = new WeakMap();
  const originalAttrs = new WeakMap();
  function translate(value) {
    if (language === 'en') return en[value] || value;
    return id[value] || value;
  }
  function translateNode(node) {
    if (node.nodeType === Node.TEXT_NODE) {
      if (!originalText.has(node)) originalText.set(node, node.nodeValue);
      const value = originalText.get(node), trimmed = value.trim();
      if (!trimmed) return;
      const result = translate(trimmed);
      node.nodeValue = result === trimmed ? value : value.replace(trimmed, result);
    } else if (node.nodeType === Node.ELEMENT_NODE) {
      for (const attr of ['placeholder', 'title', 'aria-label']) {
        if (node.hasAttribute(attr)) {
          let values = originalAttrs.get(node);
          if (!values) { values = {}; originalAttrs.set(node, values); }
          if (!(attr in values)) values[attr] = node.getAttribute(attr);
          const val = values[attr], next = translate(val);
          if (next !== node.getAttribute(attr)) node.setAttribute(attr, next);
        }
      }
      if (node.childNodes) node.childNodes.forEach(translateNode);
    }
  }
  function apply() {
    document.documentElement.lang = language;
    translateNode(document.body);
    const button = document.getElementById('language-toggle');
    if (button) button.textContent = language === 'en' ? 'Bahasa Indonesia' : 'English';
  }
  window.DurangoI18n = {
    get language() { return language; },
    setLanguage(next) { language = next === 'id' ? 'id' : 'en'; apply(); },
    t: translate,
    apply
  };
  document.addEventListener('DOMContentLoaded', () => {
    const button = document.getElementById('language-toggle');
    if (button) button.addEventListener('click', () => window.DurangoI18n.setLanguage(language === 'en' ? 'id' : 'en'));
    const observer = new MutationObserver(() => { if (language === 'id') apply(); });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    apply();
  });
})();
