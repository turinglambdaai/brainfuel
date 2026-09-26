using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

public sealed class UsageFailureTests
{
    [Theory]
    [InlineData(UsageFailureKind.Network, true)]
    [InlineData(UsageFailureKind.Tls, true)]
    [InlineData(UsageFailureKind.Proxy, true)]
    [InlineData(UsageFailureKind.Timeout, true)]
    [InlineData(UsageFailureKind.RateLimited, true)]
    [InlineData(UsageFailureKind.ServiceUnavailable, true)]
    [InlineData(UsageFailureKind.InvalidResponse, true)]
    [InlineData(UsageFailureKind.Authentication, false)]
    [InlineData(UsageFailureKind.NoCodingPlan, false)]
    [InlineData(UsageFailureKind.Unknown, false)]
    public void IsTransient_MatchesRecoverySemantics(UsageFailureKind kind, bool expected)
    {
        Assert.Equal(expected, UsageFailureText.IsTransient(kind));
    }

    [Theory]
    [InlineData(UsageFailureKind.Authentication, false)]
    [InlineData(UsageFailureKind.NoCodingPlan, false)]
    [InlineData(UsageFailureKind.Network, true)]
    [InlineData(UsageFailureKind.Timeout, true)]
    [InlineData(UsageFailureKind.Unknown, true)]
    public void AllowsSaveAnyway_OnlyBlocksUserFixableCauses(UsageFailureKind kind, bool expected)
    {
        Assert.Equal(expected, UsageFailureText.AllowsSaveAnyway(kind));
    }

    [Fact]
    public void CardText_CoversEveryKindInBothLanguages()
    {
        foreach (var kind in Enum.GetValues<UsageFailureKind>())
        {
            Strings.ApplyLanguage(AppLanguage.Zh);
            var zh = UsageFailureText.Card(kind);
            Strings.ApplyLanguage(AppLanguage.En);
            var en = UsageFailureText.Card(kind);
            Assert.False(string.IsNullOrWhiteSpace(zh));
            Assert.False(string.IsNullOrWhiteSpace(en));
            Assert.NotEqual(zh, en);
        }
        Strings.ApplyLanguage(AppLanguage.Zh);
    }
}
