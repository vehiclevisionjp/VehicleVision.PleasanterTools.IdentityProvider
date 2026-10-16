# VehicleVision.PleasanterTools.IdentityProvider

**Pleasanter の DB（ユーザ情報）を使って SAML 2.0 / OpenID Connect を提供する IdP サーバ。**

- Pleasanter 側で TOTP（二次認証）が有効なユーザは、Pleasanter の秘密鍵（`Users.SecretKey`）をそのまま使って TOTP を検証する
- Pleasanter 本体は改造しない（本体コードの取り込みも禁止）

ソリューション構成・規約は `VehicleVision.PleasanterTools.Questionnaire` に準拠する。
