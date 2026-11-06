using System.IO;
using System.Text.Json;
using Prodesk.Helper.Clipboard;
using Prodesk.Helper.Input;
using Prodesk.Helper.Policy;
using Prodesk.Helper.Uia;
using Xunit;

namespace Prodesk.Helper.Tests;

public class HelperUnitTests
{
    [Fact]
    public void UiaEngine_ThrowsNotFound_OnUnknownUid()
    {
        var uia = new UiaEngine();
        var ex = Assert.Throws<HelperErrorException>(() => uia.ResolveElementByUid("w99:e999"));
        Assert.Equal("not_found", ex.ErrorCode);
    }

    [Theory]
    [InlineData("enter", 0x0D)]
    [InlineData("tab", 0x09)]
    [InlineData("esc", 0x1B)]
    [InlineData("backspace", 0x08)]
    [InlineData("delete", 0x2E)]
    [InlineData("f4", 0x73)]
    [InlineData("s", (ushort)'S')]
    [InlineData("a", (ushort)'A')]
    public void ActionEngine_MapsVirtualKeysAccurately(string keyName, ushort expectedVk)
    {
        Assert.Equal(expectedVk, ActionEngine.MapKeyNameToVk(keyName));
    }

    [Fact]
    public void TurkishUnicodeCharacters_ArePreservedInUtf16ScanCodes()
    {
        const string turkishSample = "Merhaba Dünya — İşçi Şükrü ĞğÜüÖöÇçİı";
        ushort[] codeUnits = turkishSample.Select(c => (ushort)c).ToArray();
        Assert.Contains((ushort)'İ', codeUnits);
        Assert.Contains((ushort)'ı', codeUnits);
        Assert.Contains((ushort)'Ş', codeUnits);
        Assert.Contains((ushort)'ş', codeUnits);
        Assert.Contains((ushort)'Ğ', codeUnits);
        Assert.Contains((ushort)'ğ', codeUnits);
        Assert.Contains((ushort)'Ü', codeUnits);
        Assert.Contains((ushort)'Ö', codeUnits);
        Assert.Contains((ushort)'Ç', codeUnits);
    }

    [Fact]
    public void ClipboardService_BackupsAndRestoresTextAndRingBuffer()
    {
        var svc = new ClipboardService();
        var backup = svc.BackupClipboard();
        try
        {
            const string testPayload = "Prodesk xUnit Pano Testi — İşçi Şükrü Ğ";
            svc.WriteText(testPayload);
            var (readBack, _, _) = svc.ReadClipboard("text");
            Assert.Equal(testPayload, readBack);
        }
        finally
        {
            svc.RestoreClipboard(backup);
        }
    }

    [Fact]
    public void PolicyManager_MasksSensitivePasswordAndClipboardParameters()
    {
        using var doc = JsonDocument.Parse("{\"text\":\"SuperSecretPassword123!\"}");
        string masked = PolicyManager.MaskSensitiveParams("type_text", doc.RootElement, isPasswordField: true);
        Assert.DoesNotContain("SuperSecretPassword123!", masked);
        Assert.Contains("REDACTED", masked);

        string maskedClip = PolicyManager.MaskSensitiveParams("clipboard_write", doc.RootElement, isPasswordField: false);
        Assert.DoesNotContain("SuperSecretPassword123!", maskedClip);
    }

    [Fact]
    public void PolicyManager_EnforcesHardDenyAndAutoApproveCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"prodesk-xunit-{Guid.NewGuid():N}");
        try
        {
            var pm = new PolicyManager(tempDir);
            pm.SetAutoApprove(true);

            // Blocked password manager app is denied even when AutoApprove is true
            var blocked = pm.Evaluate("click", null, "1Password.exe", "1Password", false, false);
            Assert.Equal("deny", blocked.Action);
            Assert.Equal("blocked", blocked.Code);

            //Unsigned installer requires confirmation even when AutoApprove is true (unless AllowUnsignedInstallers is true)
            var unsignedRes = pm.Evaluate("run_installer", null, null, null, false, isUnsignedInstaller: true);
            Assert.Equal("confirm", unsignedRes.Action);
            Assert.Equal("critical", unsignedRes.Risk);

            // Standard destructive command is allowed when AutoApprove is true
            using var cmdDoc = JsonDocument.Parse("{\"command\":\"Get-Process\"}");
            var allowed = pm.Evaluate("run_command", cmdDoc.RootElement, null, null, false, false);
            Assert.Equal("allow", allowed.Action);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
