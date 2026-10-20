using FluentMigrator;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data.Migrations;

/// <summary>旧 <c>AuditLogs</c> 表を落とす（監査ログは Core.Data の共有表 <c>Core_AuditLogs</c> へ移った）。</summary>
/// <remarks>
/// 稼働データは無い。M0001 が旧表を作る（公開済みの番号は書き換えない）ので、新しい DB では作ってから落とす。
/// 共有表は Core の移行（M10005）が当てる。
/// </remarks>
[Migration(3, "旧 AuditLogs を落とす（Core_AuditLogs へ移った）")]
public sealed class M0003_DropLegacyAuditLogs : Migration
{
    public override void Up() => Delete.Table("AuditLogs").IfExists();

    public override void Down()
    {
        // 戻さない（旧表は使わない）
    }
}
