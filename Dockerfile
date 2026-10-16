# syntax=docker/dockerfile:1
#
# IdP のコンテナイメージ。
# **開発環境（ホスト）に .NET SDK も node も要求しない**ようにするためのもの。
# 実行は compose.yaml から行う。

# ---- フロントエンド ---------------------------------------------------------
# **ホストに node を要求しないため、ここでビルドする**
FROM node:24-alpine AS frontend
WORKDIR /frontend

COPY src/VehicleVision.PleasanterTools.IdentityProvider.Frontend/package.json ./
COPY src/VehicleVision.PleasanterTools.IdentityProvider.Frontend/package-lock.json ./
RUN npm ci

COPY src/VehicleVision.PleasanterTools.IdentityProvider.Frontend/ ./
# vite.config.ts の outDir は ../{Web}/wwwroot を指すので、その位置を用意しておく
RUN mkdir -p /VehicleVision.PleasanterTools.IdentityProvider.Web && npm run build

# ---- サーバ -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 依存の解決だけ先に済ませてレイヤをキャッシュする
COPY global.json Directory.Build.props ./
COPY VehicleVision.PleasanterTools.IdentityProvider.slnx ./
COPY src/ src/
RUN dotnet restore src/VehicleVision.PleasanterTools.IdentityProvider.Web

RUN dotnet publish src/VehicleVision.PleasanterTools.IdentityProvider.Web \
        -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 設定は Pleasanter と同じく App_Data/Parameters/*.json 方式。
# **資格情報の実値は入れない。** 環境変数か Key Vault から読む
COPY App_Data/ App_Data/
COPY --from=build /app/publish .
# 画面（Frontend）のビルド成果物
COPY --from=frontend /VehicleVision.PleasanterTools.IdentityProvider.Web/wwwroot ./wwwroot

# **root で動かさない。**
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "VehicleVision.PleasanterTools.IdentityProvider.Web.dll"]
