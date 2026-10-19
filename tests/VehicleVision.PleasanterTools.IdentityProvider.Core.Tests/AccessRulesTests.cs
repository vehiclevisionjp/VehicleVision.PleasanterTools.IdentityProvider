using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Tests;

public sealed class AccessRulesTests
{
    private static readonly AccessSubject Taro = new(UserId: 7, DeptId: 3, GroupIds: [10, 11]);

    private static AccessRule Inc(string kind, int id) => new(AccessRules.Include, kind, id);

    private static AccessRule Exc(string kind, int id) => new(AccessRules.Exclude, kind, id);

    [Fact]
    public void ルールが無ければ誰でも通る() =>
        Assert.True(AccessRules.Allows([], Taro));

    [Fact]
    public void Includeがあれば当たる人だけ通る()
    {
        Assert.True(AccessRules.Allows([Inc(AccessRules.Group, 10)], Taro));
        Assert.True(AccessRules.Allows([Inc(AccessRules.Dept, 99), Inc(AccessRules.User, 7)], Taro));
        Assert.False(AccessRules.Allows([Inc(AccessRules.Group, 99)], Taro));
        Assert.False(AccessRules.Allows([Inc(AccessRules.Dept, 4), Inc(AccessRules.User, 8)], Taro));
    }

    [Fact]
    public void Excludeに当たれば拒否しExcludeだけなら当たらない人は通る()
    {
        Assert.False(AccessRules.Allows([Exc(AccessRules.Dept, 3)], Taro));
        Assert.True(AccessRules.Allows([Exc(AccessRules.Dept, 4)], Taro));
    }

    [Fact]
    public void ExcludeはIncludeより強い() =>
        Assert.False(AccessRules.Allows([Inc(AccessRules.Group, 10), Exc(AccessRules.User, 7)], Taro));

    [Fact]
    public void 組織なしの人は組織のルールに当たらない()
    {
        var noDept = new AccessSubject(7, 0, []);
        Assert.False(AccessRules.Allows([Inc(AccessRules.Dept, 3)], noDept));
        Assert.True(AccessRules.Allows([Exc(AccessRules.Dept, 0)], noDept));
    }

    [Fact]
    public void 検証は不正な値を拒否する()
    {
        Assert.Empty(AccessRules.Validate([Inc(AccessRules.User, 1)]));
        Assert.NotEmpty(AccessRules.Validate([new AccessRule("allow", AccessRules.User, 1)]));
        Assert.NotEmpty(AccessRules.Validate([new AccessRule(AccessRules.Include, "tenant", 1)]));
        Assert.NotEmpty(AccessRules.Validate([Inc(AccessRules.User, 0)]));
        Assert.NotEmpty(AccessRules.Validate([Inc(AccessRules.User, 1), Exc(AccessRules.User, 1)]));
        Assert.NotEmpty(AccessRules.Validate([.. Enumerable.Range(1, 201).Select(id => Inc(AccessRules.User, id))]));
    }

    [Fact]
    public void 設定値を読む()
    {
        var rules = AccessRules.ParseList("user:12, dept:3;group:5", AccessRules.Include);
        Assert.Equal(3, rules.Count);
        Assert.Contains(new AccessRule(AccessRules.Include, AccessRules.Group, 5), rules);
        Assert.Empty(AccessRules.ParseList("  ", AccessRules.Include));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("tenant:1")]
    [InlineData("user:x")]
    [InlineData("user:0")]
    public void 設定値の誤りは黙って捨てない(string text) =>
        Assert.Throws<FormatException>(() => AccessRules.ParseList(text, AccessRules.Include));
}
