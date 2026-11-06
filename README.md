# Prodesk — Background-First Computer Use MCP Server & Antigravity Plugin for Windows

<p align="center">
  <img src="plugin/assets/logo.svg" alt="Prodesk Logo" width="120" height="120" />
</p>

**Prodesk**, yapay zeka ajanlarının (Antigravity, OpenCode, Claude Desktop vb.) Windows 10/11 bilgisayarınızı **çalışmanızı bölmeden, arka planda, hızlı ve güvenli** şekilde görmesini, anlamasını ve kontrol etmesini sağlayan kapsamlı bir Computer Use MCP sunucusu ve Antigravity plugin paketidir.

---

## 🇹🇷 Türkçe Dokümantasyon

### Öne Çıkan Özellikler (10 Ana Modül, 66 Araç)
1. **Masaüstünü Bölmeyen 3 Kademeli Eylem Motoru (`mode: "auto"`):**
   - **Kademe 1 (UIA Pattern):** `InvokePattern`, `ValuePattern`, `TogglePattern`, `SelectionItemPattern`, `ExpandCollapsePattern`, `ScrollPattern` — imleç oynamaz, odak çalınmaz.
   - **Kademe 2 (Win32 Pencere Mesajları):** Arka plandaki HWND'ye `WM_LBUTTONDOWN/UP`, `BM_CLICK`, `WM_CHAR`, `WM_SETTEXT` gönderir.
   - **Kademe 3 (`SendInput` + Otomatik Geri Yükleme):** Yalnızca gerektiğinde (örn. Paint tuval çizimi) kullanılır; işlem öncesi imleç ve aktif pencere kaydedilir, işlem bitince anında geri yüklenir. Türkçe karakterler (`İ ı Ş ş Ğ ğ Ü ü Ö ö Ç ç`) `KEYEVENTF_UNICODE` ile klavye düzeninden bağımsız yazılır.
2. **Per-Monitor V2 DPI & Çoklu Monitör Koordinat Motoru:** Negatif koordinatlı ikincil monitörleri ve %100..%200+ DPI ölçeklerini otomatik dönüştürür.
3. **UIA Ağacı (`snapshot`) & Set-of-Mark (`screenshot({ annotate: true })`):** Her etkileşimli öğeye kararlı `uid` (`[w1:e5]`) verir; UWP (`ApplicationFrameHost.exe`) pencerelerinin gerçek süreçlerini çözer.
4. **Yerleşik Windows OCR (`Windows.Media.Ocr`):** Ek kurulum gerektirmeden `tr-TR` ve `en-US` metin tanıma (`ocr`, `find_text`).
5. **Pencere & Uygulama Yönetimi:** `launch_app({ background: true })` (`SW_SHOWNOACTIVATE`) ile odak çalmadan uygulama başlatma, Başlat menüsü ve UWP `AUMID` çözümleme.
6. **Dosya Sistemi & Geri Dönüşüm Kutusu:** Atomik yazma, kodlama algılama (`UTF-8`, `UTF-16`, `Windows-1254`) ve varsayılan olarak Geri Dönüşüm Kutusu'na silme.
7. **İndirme, GitHub Release, Winget & Authenticode İmza Doğrulama:** `download_file`, `github_release`, `winget_search`/`winget_install`, `verify_file` (`WinVerifyTrust`) ve `run_installer`.
8. **Pano & Geçmiş:** `clipboard_read`/`write`, `clipboard_history` (Windows Pano Geçmişi + Helper bellek halka tamponu; parola yöneticisi içerikleri hariç tutulur).
9. **Ekran Kaydı (MP4/GIF), Oturum Raporu & Anlamsal Makrolar:** `record_start`/`record_stop` (MP4), `record_to_gif` (GIF), `session_report` (HTML zaman çizelgesi) ve pencere taşınsa da çalışan anlamsal makrolar (`macro_record_start`/`macro_run`).
10. **Prowser Kardeş Proje Köprüsü (`browser`):** Web sayfası DOM işlerini `ws://127.0.0.1:17890..17899/peer` üzerinden Prowser'a (`new_window`) devreder.

### Güvenlik Modeli
- **Auto-Approve ("Soru sormadan otomatik onayla"):** Sistem tepsisi (Tray) menüsünden veya Ayarlar penceresinden tek tıkla açılır; açıkken tüm onay pencereleri atlanarak maksimum akıcılık sağlanır (tray simgesi yeşile döner).
- **Hard Deny Koruması (Auto-Approve Aşamaz):** Parola yöneticileri (`1Password`, `Bitwarden`, `KeePass`), `consent.exe`, `.ssh` anahtarları, tarayıcı `User Data` parola/çerez veritabanları ve Prodesk/Prowser kendi güvenlik/token dosyaları her zaman engellenir.
- **Acil Durdurma (Kill Switch):** `Ctrl + Alt + Shift + X` kısayolu veya fareyi ekranın sol üst köşesine `(0,0)` çarpmak tüm işlemleri anında durdurur ve basılı modifier tuşlarını serbest bırakır.

### Tek Tık Kurulum & Kaldırma
```bat
install.bat
```
- `packages/server/dist/index.cjs` ve `prodesk-helper.exe` dosyalarını `%LOCALAPPDATA%\Prodesk\app\` dizinine kopyalar.
- Antigravity plugin paketini `~/.gemini/config/plugins/prodesk/` altına kurar.
- OpenCode (`opencode.json` / `opencode.jsonc`) ve Claude Desktop yapılandırmalarını yedek alarak günceller.
- Kaldırmak için `uninstall.bat` çalıştırmanız yeterlidir.

> **Antivirüs / SendInput Notu:** Prodesk masaüstü otomasyonu için Windows `SendInput` ve `UIAutomation` API'lerini kullanan yerel bir yardımcı (`prodesk-helper.exe`) içerir. Bazı güvenlik yazılımları otomasyon araçlarını yanlış pozitif olarak işaretleyebilir; kaynak kodu tamamen açıktır ve isteğe bağlı olarak kendi Authenticode sertifikanızla imzalayabilirsiniz.

---

## 🇬🇧 English Summary

**Prodesk** is a production-grade Windows Computer Use MCP server and Antigravity plugin designed around a strict **non-disruptive background-first architecture**:
- **Tier 1 UIA Patterns -> Tier 2 Win32 Messages -> Tier 3 SendInput (with instant cursor & foreground window restoration).**
- **66 MCP Tools** covering UIA snapshots, Per-Monitor V2 DPI screenshots with Set-of-Mark annotations, native `Windows.Media.Ocr`, window/app management, safe file system operations, `winget` & Authenticode verification, clipboard history, UTF-8 shell jobs, MP4/GIF recording, semantic macros, and Prowser WebSocket peer handoff.
- Run `install.bat` for one-click setup across Antigravity, OpenCode, and Claude Desktop, or `uninstall.bat` to cleanly roll back.
