using System.Runtime.CompilerServices;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>
/// このアプリの DB の設定。DB の基盤（接続・暗号化検査・マイグレーションと排他）は Core.Data にあり、
/// <b>アプリごとに違う値はここで持って、引数で渡す</b>。
/// </summary>
public static class IdentityProviderDatabase
{
    /// <summary>SQLite の既定ファイル名。</summary>
    public const string DefaultSqliteFileName = "idp.db";

    /// <summary>接続文字列を与える設定名。</summary>
    public const string ConnectionStringSetting = "IDP_DB_CONNECTIONSTRING";

    /// <summary>暗号化の検査を緩める設定名。</summary>
    public const string AllowInsecureSetting = "IDP_DB_ALLOW_INSECURE";

    /// <summary>
    /// 起動時のマイグレーションの排他に使う名前。
    /// **アプリごとに別にする。** 同じ DB サーバを複数のアプリが使うとき、名前が同じだと別のアプリのマイグレーションを待たせる。
    /// </summary>
    public const string MigrationLockName = "idp-startup-migrations";

    /// <summary>このアプリのマイグレーションを適用する。</summary>
    public static DatabaseMigrator Migrator { get; } =
        new(typeof(IdentityProviderDatabase).Assembly, MigrationLockName);

    /// <summary>
    /// このアセンブリのモデル型（列挙型・record）を、SQLite の型の対応表へ知らせる。
    /// **アセンブリの読み込み時に 1 回だけ呼ぶ**（<c>[ModuleInitializer]</c>）。
    /// </summary>
    /// <remarks>
    /// Core.Data は、知らされたアセンブリの型だけを SQLite 用に登録する。実際の登録は SQLite の接続を作るときだけで、
    /// ほかの RDBMS の挙動は変わらない。
    /// </remarks>
    // CA2255 は「ライブラリでの [ModuleInitializer]」への注意。**ここは再利用するライブラリではなく、このアプリのデータ層**。
    // アセンブリの読み込み時に必ず通る経路が要る（ファクトリやストアを、このクラスを経由せずに作る試験や呼び出しもあるため）
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterModelAssembly() =>
        SqliteTypeHandlers.AddModelAssemblies(typeof(IdentityProviderDatabase).Assembly);

    /// <summary>接続文字列を解決する（SQLite は <c>App_Data</c> 配下へ既定のファイルを置く）。</summary>
    public static string ResolveConnectionString(
        DatabaseProvider provider,
        string? configured,
        string contentRootPath) =>
        DatabaseConnectionString.Resolve(
            provider,
            configured,
            contentRootPath,
            DefaultSqliteFileName,
            ConnectionStringSetting);

    /// <summary>接続が暗号化されているかを見る。緩める設定が <c>true</c> のときだけ許す。</summary>
    public static void EnsureSecure(DatabaseProvider provider, string connectionString, bool allowInsecure) =>
        ConnectionSecurity.EnsureSecure(provider, connectionString, allowInsecure, AllowInsecureSetting);
}
