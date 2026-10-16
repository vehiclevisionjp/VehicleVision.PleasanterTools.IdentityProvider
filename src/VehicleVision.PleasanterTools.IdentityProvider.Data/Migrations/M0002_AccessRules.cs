using FluentMigrator;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data.Migrations;

/// <summary>連携先ごとのアクセスのルール（ユーザー・組織・グループの Include / Exclude）。</summary>
/// <remarks>
/// NULL を許す列にする（MySQL の TEXT は既定値を持てないため）。NULL は「ルール無し」と読む。
/// </remarks>
[Migration(2, "連携先へアクセスのルールを足す")]
public sealed class M0002_AccessRules : Migration
{
    public override void Up()
    {
        Alter.Table("OidcClients").AddColumn("AccessRulesJson").AsString(int.MaxValue).Nullable();
        Alter.Table("SamlProviders").AddColumn("AccessRulesJson").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        Delete.Column("AccessRulesJson").FromTable("OidcClients");
        Delete.Column("AccessRulesJson").FromTable("SamlProviders");
    }
}
