import { describe, expect, it } from 'vitest';
import { buildAuditQuery, emptyAuditFilter, isFailure, localToIso } from './auditQuery';
import { interpolate, negotiateLanguage } from './i18n/language';
import { en, ja, translator, type MessageKey } from './i18n/messages';
import { totpVerdict, verdictTone } from './lookup';
import { clientHash, navPageOf, parseRoute, samlProviderHash } from './route';
import { parseThemePreference, resolveTheme } from './theme';
import type { UserLookup } from './types';

describe('parseRoute', () => {
  it('知らないハッシュはダッシュボード', () => {
    for (const hash of ['', '#', '#/', '#/unknown']) {
      expect(parseRoute(hash)).toEqual({ page: 'dashboard' });
    }
  });

  it('一覧・作成・編集を読み分ける', () => {
    expect(parseRoute('#/clients')).toEqual({ page: 'clients' });
    expect(parseRoute('#/clients/new')).toEqual({ page: 'client', clientId: null });
    expect(parseRoute('#/clients/my%20app')).toEqual({ page: 'client', clientId: 'my app' });
    expect(parseRoute('#/saml/new')).toEqual({ page: 'samlProvider', providerId: null });
    expect(parseRoute('#/saml/0f8f')).toEqual({ page: 'samlProvider', providerId: '0f8f' });
    expect(parseRoute('#/keys')).toEqual({ page: 'keys' });
    expect(parseRoute('#/sessions')).toEqual({ page: 'sessions' });
    expect(parseRoute('#/audit')).toEqual({ page: 'audit' });
    expect(parseRoute('#/lookup')).toEqual({ page: 'lookup' });
  });

  it('壊れた符号化は一覧へ戻す', () => {
    expect(parseRoute('#/clients/%E0%A4%A')).toEqual({ page: 'clients' });
  });

  it('ハッシュの組み立てと往復できる', () => {
    expect(parseRoute(clientHash('a/b'))).toEqual({ page: 'client', clientId: 'a/b' });
    expect(parseRoute(clientHash(null))).toEqual({ page: 'client', clientId: null });
    expect(parseRoute(samlProviderHash('x y'))).toEqual({ page: 'samlProvider', providerId: 'x y' });
  });

  it('編集画面はナビの一覧項目に属する', () => {
    expect(navPageOf(parseRoute('#/clients/new'))).toBe('clients');
    expect(navPageOf(parseRoute('#/saml/x'))).toBe('saml');
  });
});

describe('監査ログの問い合わせ', () => {
  it('空の条件は含めない', () => {
    expect(buildAuditQuery(emptyAuditFilter(), 0)).toEqual({
      from: undefined,
      to: undefined,
      actor: undefined,
      action: undefined,
      failedOnly: undefined,
      limit: 100,
      offset: 0,
    });
  });

  it('ページ位置から offset を作り、条件を整える', () => {
    const query = buildAuditQuery(
      { ...emptyAuditFilter(), actor: ' admin ', action: 'auth.login', failedOnly: true },
      2,
    );
    expect(query).toMatchObject({ actor: 'admin', action: 'auth.login', failedOnly: true, offset: 200 });
  });

  it('日時を UTC の ISO 8601 へ直す。読めない値は無視', () => {
    expect(localToIso('')).toBeUndefined();
    expect(localToIso('xx')).toBeUndefined();
    expect(localToIso('2026-10-17T09:00')).toMatch(/^2026-10-1[67]T\d\d:00:00\.000Z$/);
  });

  it('失敗の判定', () => {
    expect(isFailure({ statusCode: 401, action: 'x' })).toBe(true);
    expect(isFailure({ statusCode: null, action: 'auth.login.failure' })).toBe(true);
    expect(isFailure({ statusCode: 200, action: 'auth.login.success' })).toBe(false);
    expect(isFailure({ statusCode: null, action: 'oidc.token.issued' })).toBe(false);
  });
});

describe('ユーザ照会の読み解き', () => {
  const base: UserLookup = {
    found: true,
    tenantId: 1,
    userId: 1,
    loginId: 'a',
    name: 'A',
    disabled: false,
    lockout: false,
    tenantManager: false,
    serviceManager: false,
    hasTotp: false,
    totpRequired: false,
    mailAddresses: [],
    groups: [],
    dept: null,
  };

  it('5 通りに分類する', () => {
    expect(totpVerdict({ ...base, found: false })).toBe('notFound');
    expect(totpVerdict({ ...base, hasTotp: true, totpRequired: true })).toBe('required');
    expect(totpVerdict({ ...base, totpRequired: true })).toBe('requiredNoDevice');
    expect(totpVerdict(base)).toBe('notRequired');
    expect(totpVerdict({ ...base, hasTotp: true })).toBe('notRequiredHasTotp');
  });

  it('登録の無いまま求められる場合は警告にする', () => {
    expect(verdictTone('requiredNoDevice')).toBe('warn');
    expect(verdictTone('notFound')).toBe('error');
  });
});

describe('テーマ', () => {
  it('保存値の解釈と解決', () => {
    expect(parseThemePreference('dark')).toBe('dark');
    expect(parseThemePreference('x')).toBe('system');
    expect(parseThemePreference(null)).toBe('system');
    expect(resolveTheme('system', true)).toBe('dark');
    expect(resolveTheme('system', false)).toBe('light');
    expect(resolveTheme('light', true)).toBe('light');
  });
});

describe('言語と文言', () => {
  it('既定は ja。対応外は次の候補へ', () => {
    expect(negotiateLanguage(null, [])).toBe('ja');
    expect(negotiateLanguage('en-US')).toBe('en');
    expect(negotiateLanguage('de', ['fr', 'en-GB'])).toBe('en');
    expect(negotiateLanguage('de', ['fr'])).toBe('ja');
  });

  it('差し込み', () => {
    expect(interpolate('{a} と {b}', { a: 'x', b: 1234 }, 'en')).toBe('x と 1,234');
    expect(interpolate('{c}', {}, 'ja')).toBe('{c}');
  });

  it('日英で鍵が揃い、空の文言がなく、差し込み名が一致する', () => {
    expect(Object.keys(en).sort()).toEqual(Object.keys(ja).sort());
    const names = (value: string) => [...value.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    for (const key of Object.keys(ja) as MessageKey[]) {
      expect(en[key], key).not.toBe('');
      expect(names(en[key]), key).toEqual(names(ja[key]));
    }
  });

  it('translator が文言を引く', () => {
    expect(translator('ja')('common.minutes', { count: 5 })).toBe('5 分');
    expect(translator('en')('common.minutes', { count: 5 })).toBe('5 min');
  });
});
