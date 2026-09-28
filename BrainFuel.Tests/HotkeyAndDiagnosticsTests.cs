using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

public sealed class HotkeyParseTests
{
    [Theory]
    [InlineData("Ctrl+Alt+B", true)]
    [InlineData("ctrl+alt+b", true)]
    [InlineData("Ctrl+Shift+5", true)]
    [InlineData("Win+F12", true)]
    [InlineData("Alt+F1", true)]
    [InlineData("Ctrl+Alt+Win+Shift+Z", true)]
    public void ValidCombos_Parse(string text, bool expected)
    {
        Assert.Equal(expected, HotkeyParse.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("B")]              // bare key would swallow typing everywhere
    [InlineData("Ctrl")]           // modifier alone does nothing
    [InlineData("Ctrl+B+G")]       // two non-modifier keys
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Alt+Foo")]
    [InlineData("Ctrl+Alt+F25")]
    public void InvalidCombos_AreRejected(string? text)
    {
        Assert.False(HotkeyParse.TryParse(text, out _, out _));
    }

    [Fact]
    public void ModifierFlags_AreDistinctBits()
    {
        HotkeyParse.TryParse("Ctrl+Alt+B", out var mods, out var vk);
        Assert.Equal(0x0003u, mods); // control | alt
        Assert.Equal('B', (char)vk);
        HotkeyParse.TryParse("Win+F12", out mods, out vk);
        Assert.Equal(0x0008u, mods);
        Assert.Equal(0x7Bu, vk);     // VK_F12
    }
}

public sealed class DiagnosticsBuilderTests
{
    private const string SecretKey = "233a15a4db6c460bbaae22d3faf3da1c.SECRET";

    [Fact]
    public void Bundle_NeverContainsKeyMaterial()
    {
        var settings = new AppSettings
        {
            Accounts =
            {
                new AccountConfig
                {
                    Id = "acc1",
                    Name = "Test",
                    BaseDomain = "https://open.bigmodel.cn",
                    Provider = "glm",
                    Configured = true,
                },
            },
            ActiveAccountId = "acc1",
            HotkeyEnabled = true,
            HotkeyCombo = "Ctrl+Alt+B",
        };
        // The key lives only in the keyring/credentials store — it must not be
        // reachable through any settings field the bundle prints.
        SettingsService.SetKey("acc1", SecretKey);
        try
        {
            var bundle = DiagnosticsBuilder.Build(settings);
            Assert.Contains("acc1", bundle);
            Assert.DoesNotContain(SecretKey, bundle);
            Assert.DoesNotContain("ApiKey", bundle);
        }
        finally
        {
            SettingsService.SetKey("acc1", null);
        }
    }
}
