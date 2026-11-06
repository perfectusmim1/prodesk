# Prodesk — Windows Computer Use Plugin for Antigravity

**Prodesk**, Windows 10 ve 11 üzerinde çalışan yapay zeka ajanlarının masaüstü uygulamalarını, pencereleri, dosya sistemini ve yerel diyalogları **kullanıcının imlecini ve odağını çalmadan (arka planda)** görmesini ve kontrol etmesini sağlar.

## Öne Çıkan Özellikler
- **3 Kademeli Arka Plan Eylem Motoru:** Kademe 1 UIA Pattern (`Invoke`, `Value`, `Toggle`, `SelectionItem`) -> Kademe 2 Arka Plan Win32 Mesajları -> Kademe 3 `SendInput` (imleç ve aktif pencereyi anında geri yükler).
- **UI Automation Snapshot & Set-of-Mark:** Etkileşimli öğeler için kararlı `uid` (`[w1:e5]`) ve numaralı kutularla işaretlenmiş ekran görüntüleri (`screenshot({ annotate: true })`).
- **Yerleşik Windows OCR:** Ek kurulum gerektirmeyen `Windows.Media.Ocr` (`tr-TR` ve `en-US`) desteği.
- **Güvenlik, Auto-Approve & Acil Durdurma:** Türkçe WPF onay penceresi, sistem tepsisinden tek tıkla açılan **"Soru sormadan otomatik onayla" (Auto-Approve)** seçeneği, `Ctrl+Alt+Shift+X` acil durdurma kısayolu ve sol üst köşe `(0,0)` failsafe.
- **Prowser Entegrasyonu:** Web sayfası işlerini yerel WebSocket köprüsüyle Prowser'a (`new_window`) devreder.
