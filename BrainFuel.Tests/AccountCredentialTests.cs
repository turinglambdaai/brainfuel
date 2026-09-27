using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

public sealed class AccountCredentialTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bf-cred-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TryWriteAll_TryReadAll_RoundTripsDict()
    {
        var dir = TempDir();
        var keys = new Dictionary<string, string> { ["a1"] = "key-one", ["a2"] = "key-two" };

        Assert.True(CredentialStore.TryWriteAll(dir, keys));
        Assert.True(CredentialStore.TryReadAll(dir, out var back));
        Assert.Equal(2, back.Count);
        Assert.Equal("key-one", back["a1"]);
        Assert.Equal("key-two", back["a2"]);
    }

    [Fact]
    public void LegacyBareKeyBlob_ReadsAsDefaultAccount()
    {
        var dir = TempDir();
        // v0.5.x wrote the bare key through the single-value API.
        Assert.True(CredentialStore.TryWrite(dir, "legacy-plain-key"));

        Assert.True(CredentialStore.TryReadAll(dir, out var keys));
        Assert.Single(keys);
        Assert.Equal(SettingsService.LegacyAccountId, keys.Keys.Single());
        Assert.Equal("legacy-plain-key", keys[SettingsService.LegacyAccountId]);
    }

    [Fact]
    public void EmptyDictWrite_DeletesBlob()
    {
        var dir = TempDir();
        CredentialStore.TryWriteAll(dir, new Dictionary<string, string> { ["a1"] = "k" });
        CredentialStore.TryWriteAll(dir, new Dictionary<string, string>());
        Assert.False(CredentialStore.TryReadAll(dir, out var keys));
        Assert.Empty(keys);
    }

    [Fact]
    public void AccountDisplay_StringFallsBackToPlatform()
    {
        Assert.Equal("工作号", new AccountConfig { Name = "工作号" }.ToString());
        Assert.Equal("Z.ai", new AccountConfig { BaseDomain = "https://api.z.ai" }.ToString());
        Assert.Equal("bigmodel.cn", new AccountConfig().ToString());
    }
}
