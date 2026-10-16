import { describe, expect, it } from 'vitest';
import type { OidcClient, SamlProvider } from './types';
import {
  applyImportedMetadata,
  emptyOidcForm,
  emptySamlForm,
  formatTenantIds,
  validateAccessRules,
  looksLikeCertificate,
  oidcFormFromClient,
  parseLines,
  parseOptionalSeconds,
  parseScopeInput,
  parseTenantIds,
  samlFormFromProvider,
  validateAttributeMappings,
  validateOidcForm,
  validateRedirectUri,
  validateSamlForm,
  validateScopes,
} from './validation';

describe('parseLines', () => {
  it('空行と前後の空白を除き、重複を 1 つにする', () => {
    expect(parseLines(' https://a.example/cb \r\n\n https://a.example/cb\nhttps://b.example/cb\n')).toEqual([
      'https://a.example/cb',
      'https://b.example/cb',
    ]);
  });
});

describe('parseTenantIds', () => {
  it('空は全テナントを意味する空配列', () => {
    expect(parseTenantIds('')).toEqual({ ok: true, value: [] });
    expect(parseTenantIds('  \n ')).toEqual({ ok: true, value: [] });
  });

  it('カンマ・空白・改行・全角区切りを受け付け、重複を除いて昇順にする', () => {
    expect(parseTenantIds('3, 1 2\n2、5')).toEqual({ ok: true, value: [1, 2, 3, 5] });
  });

  it('正の整数以外は失敗する', () => {
    for (const bad of ['a', '1.5', '-1', '0', '1,x', '１２']) {
      expect(parseTenantIds(bad).ok, bad).toBe(false);
    }
    expect(parseTenantIds('1, x')).toMatchObject({ ok: false, error: { params: { value: 'x' } } });
  });

  it('formatTenantIds で戻せる', () => {
    expect(formatTenantIds([1, 2, 3])).toBe('1, 2, 3');
    expect(parseTenantIds(formatTenantIds([4, 9]))).toEqual({ ok: true, value: [4, 9] });
  });
});

describe('validateRedirectUri', () => {
  it('https と、ループバックの http を許す', () => {
    expect(validateRedirectUri('https://app.example.com/callback')).toBeNull();
    expect(validateRedirectUri('http://localhost:3000/cb')).toBeNull();
    expect(validateRedirectUri('http://127.0.0.1:8080/cb')).toBeNull();
    expect(validateRedirectUri('http://[::1]:8080/cb')).toBeNull();
  });

  it('ループバック以外の http は拒む', () => {
    expect(validateRedirectUri('http://app.example.com/cb')?.key).toBe('validation.redirectUri.insecure');
  });

  it('ワイルドカード・フラグメント・相対・別のスキームは拒む', () => {
    expect(validateRedirectUri('https://*.example.com/cb')?.key).toBe('validation.redirectUri.wildcard');
    expect(validateRedirectUri('https://a.example.com/cb#x')?.key).toBe('validation.redirectUri.fragment');
    expect(validateRedirectUri('/callback')?.key).toBe('validation.redirectUri.invalid');
    expect(validateRedirectUri('javascript:alert(1)')?.key).toBe('validation.redirectUri.scheme');
    expect(validateRedirectUri('ftp://a.example.com/')?.key).toBe('validation.redirectUri.scheme');
    // カスタムスキームは公開クライアントだけ（逆ドメイン形式のみ）
    expect(validateRedirectUri('com.example.app:/cb')?.key).toBe('validation.redirectUri.scheme');
    expect(validateRedirectUri('com.example.app:/cb', true)).toBeNull();
    expect(validateRedirectUri('myapp:/cb', true)?.key).toBe('validation.redirectUri.scheme');
  });
});

describe('スコープ', () => {
  it('openid が要る', () => {
    expect(validateScopes(['profile'])).toMatchObject({ ok: false, error: { key: 'validation.scope.openid' } });
    expect(validateScopes([])).toMatchObject({ ok: false });
  });

  it('知らないスコープは拒む', () => {
    expect(validateScopes(['openid', 'admin'])).toMatchObject({
      ok: false,
      error: { key: 'validation.scope.unknown', params: { value: 'admin' } },
    });
  });

  it('決まった順に並べ直す', () => {
    expect(validateScopes(['email', 'offline_access', 'openid'])).toEqual({
      ok: true,
      value: ['openid', 'email', 'offline_access'],
    });
  });

  it('スペース区切りの入力を解釈する', () => {
    expect(parseScopeInput('openid  profile\temail profile')).toEqual({
      ok: true,
      value: ['openid', 'profile', 'email'],
    });
    expect(parseScopeInput('profile').ok).toBe(false);
  });
});

describe('parseOptionalSeconds', () => {
  it('空は既定（null）', () => {
    expect(parseOptionalSeconds('')).toEqual({ ok: true, value: null });
    expect(parseOptionalSeconds('  ')).toEqual({ ok: true, value: null });
  });

  it('正の整数を受け付ける', () => {
    expect(parseOptionalSeconds('3600')).toEqual({ ok: true, value: 3600 });
  });

  it('0・負・小数・上限超過・文字は拒む', () => {
    for (const bad of ['0', '-5', '1.5', '31536001', 'abc', '1e3']) {
      expect(parseOptionalSeconds(bad).ok, bad).toBe(false);
    }
  });
});

describe('validateOidcForm', () => {
  const valid = () => ({
    ...emptyOidcForm(),
    displayName: '勤怠アプリ',
    redirectUris: 'https://app.example.com/cb\nhttps://app.example.com/cb2',
    tenantIds: '2, 1',
    accessRules: [],
    accessTokenLifetimeSeconds: '600',
  });

  it('正しい入力を送る本体へ直す（作成）', () => {
    const result = validateOidcForm({ ...valid(), clientId: ' my-app ' }, 'create');
    expect(result).toEqual({
      ok: true,
      value: {
        clientId: 'my-app',
        displayName: '勤怠アプリ',
        clientType: 'confidential',
        redirectUris: ['https://app.example.com/cb', 'https://app.example.com/cb2'],
        postLogoutRedirectUris: [],
        allowedScopes: ['openid', 'profile', 'email'],
        allowedTenantIds: [1, 2],
        accessRules: [],
        accessTokenLifetimeSeconds: 600,
        refreshTokenLifetimeSeconds: null,
        enabled: true,
      },
    });
  });

  it('クライアント ID を省けば本体にも含めない。編集では含めない', () => {
    const created = validateOidcForm(valid(), 'create');
    expect(created.ok && 'clientId' in created.value).toBe(false);
    const edited = validateOidcForm({ ...valid(), clientId: 'x' }, 'edit');
    expect(edited.ok && 'clientId' in edited.value).toBe(false);
  });

  it('項目ごとの失敗を返す', () => {
    const result = validateOidcForm(
      {
        ...emptyOidcForm(),
        clientId: 'bad id',
        redirectUris: 'http://evil.example.com/cb',
        postLogoutRedirectUris: 'nope',
        scopes: ['profile'],
        tenantIds: 'a',
        accessRules: [],
        accessTokenLifetimeSeconds: '-1',
        refreshTokenLifetimeSeconds: 'x',
      },
      'create',
    );
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(Object.keys(result.errors).sort()).toEqual(
      [
        'accessTokenLifetimeSeconds',
        'clientId',
        'displayName',
        'postLogoutRedirectUris',
        'redirectUris',
        'refreshTokenLifetimeSeconds',
        'scopes',
        'tenantIds',
      ].sort(),
    );
  });

  it('リダイレクト URI が 1 件も無ければ失敗', () => {
    const result = validateOidcForm({ ...valid(), redirectUris: '\n' }, 'edit');
    expect(result).toMatchObject({ ok: false, errors: { redirectUris: { key: 'validation.redirectUri.required' } } });
  });

  it('既存のクライアントから入力欄の状態を作れる', () => {
    const client: OidcClient = {
      clientId: 'c1',
      displayName: 'C1',
      clientType: 'public',
      redirectUris: ['https://a/cb'],
      postLogoutRedirectUris: [],
      allowedScopes: ['openid'],
      allowedTenantIds: [3],
      accessRules: [],
      accessTokenLifetimeSeconds: null,
      refreshTokenLifetimeSeconds: 86400,
      enabled: false,
      hasSecret: false,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    };
    expect(oidcFormFromClient(client)).toMatchObject({
      tenantIds: '3',
      accessRules: [],
      accessTokenLifetimeSeconds: '',
      refreshTokenLifetimeSeconds: '86400',
    });
  });
});

describe('SAML', () => {
  const valid = () => ({
    ...emptySamlForm(),
    entityId: 'https://sp.example.com/metadata',
    displayName: 'SP',
    acsUrl: 'https://sp.example.com/acs',
  });

  it('証明書らしさを判定する', () => {
    expect(
      looksLikeCertificate('-----BEGIN CERTIFICATE-----\nMIIBkTCB+wIJAKHBfpegPjMC\n-----END CERTIFICATE-----'),
    ).toBe(true);
    expect(looksLikeCertificate('MIIBkTCB+wIJAKHBfpegPjMC')).toBe(true);
    expect(looksLikeCertificate('not a cert!')).toBe(false);
    expect(looksLikeCertificate('')).toBe(false);
  });

  it('属性マッピングの空・重複を拒む', () => {
    expect(validateAttributeMappings([{ attributeName: ' ', source: 'email' }])?.key).toBe(
      'validation.attribute.empty',
    );
    expect(
      validateAttributeMappings([
        { attributeName: 'mail', source: 'email' },
        { attributeName: ' mail ', source: 'loginId' },
      ])?.key,
    ).toBe('validation.attribute.duplicate');
    expect(validateAttributeMappings([{ attributeName: 'mail', source: 'email' }])).toBeNull();
  });

  it('正しい入力を送る本体へ直す。空の任意項目は null', () => {
    const result = validateSamlForm({
      ...valid(),
      attributeMappings: [{ attributeName: ' mail ', source: 'email' }],
      tenantIds: '5',
      accessRules: [],
    });
    expect(result).toMatchObject({
      ok: true,
      value: {
        sloUrl: null,
        signingCertificate: null,
        attributeMappings: [{ attributeName: 'mail', source: 'email' }],
        allowedTenantIds: [5],
        accessRules: [],
      },
    });
  });

  it('必須・URL・署名付き必須のときの証明書を調べる', () => {
    const result = validateSamlForm({
      ...emptySamlForm(),
      acsUrl: 'ftp://x',
      sloUrl: 'nope',
      requireSignedRequests: true,
    });
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.errors.entityId?.key).toBe('validation.required');
    expect(result.errors.displayName?.key).toBe('validation.required');
    expect(result.errors.acsUrl?.key).toBe('validation.url');
    expect(result.errors.sloUrl?.key).toBe('validation.url');
    expect(result.errors.signingCertificate?.key).toBe('validation.certificate.required');
  });

  it('メタデータの取り込みは返された項目だけを重ね、他は残す', () => {
    const base = { ...valid(), nameIdFormat: 'persistent' as const, signResponse: true };
    const imported = {
      entityId: 'https://new.example.com',
      displayName: '',
      acsUrl: 'https://new.example.com/acs',
      sloUrl: null,
      signingCertificate: 'MIIBkTCB+wIJAKHBfpegPjMC',
    } as SamlProvider;
    expect(applyImportedMetadata(base, imported)).toMatchObject({
      entityId: 'https://new.example.com',
      displayName: 'SP',
      acsUrl: 'https://new.example.com/acs',
      sloUrl: '',
      signingCertificate: 'MIIBkTCB+wIJAKHBfpegPjMC',
      nameIdFormat: 'persistent',
      signResponse: true,
    });
  });

  it('既存のプロバイダから入力欄の状態を作ると、元の配列と切り離される', () => {
    const provider = {
      ...valid(),
      providerId: 'p',
      sloUrl: null,
      signingCertificate: null,
      attributeMappings: [{ attributeName: 'a', source: 'email' }],
      allowedTenantIds: [1],
      accessRules: [],
      createdAt: '',
      updatedAt: '',
    } as SamlProvider;
    const form = samlFormFromProvider(provider);
    form.attributeMappings[0]!.attributeName = 'changed';
    expect(provider.attributeMappings[0]!.attributeName).toBe('a');
  });
});

describe('validateAccessRules', () => {
  const rule = (effect: 'include' | 'exclude', kind: 'user' | 'dept' | 'group', targetId: number) => ({
    effect,
    kind,
    targetId,
    label: null,
  });

  it('空や、重ならないルールは通る', () => {
    expect(validateAccessRules([])).toBeNull();
    expect(validateAccessRules([rule('include', 'group', 1), rule('exclude', 'user', 1), rule('include', 'dept', 1)])).toBeNull();
  });

  it('同じ対象を許可と拒否の両方へ入れると失敗する', () => {
    expect(validateAccessRules([rule('include', 'group', 1), rule('exclude', 'group', 1)])).toMatchObject({
      key: 'validation.accessRules.conflict',
    });
  });

  it('同じ対象の同じ種別が重なるのは構わない（サーバが重複を畳む）', () => {
    expect(validateAccessRules([rule('include', 'group', 1), rule('include', 'group', 1)])).toBeNull();
  });

  it('200 件を超えると失敗する', () => {
    const many = Array.from({ length: 201 }, (_, i) => rule('include', 'user', i + 1));
    expect(validateAccessRules(many)).toMatchObject({ key: 'validation.accessRules.tooMany' });
  });
});
