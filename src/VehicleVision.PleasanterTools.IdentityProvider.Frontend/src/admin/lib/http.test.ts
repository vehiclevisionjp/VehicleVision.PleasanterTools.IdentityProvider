import { describe, expect, it } from 'vitest';
import { appUrl, loginUrl, normalizeAdminPath, normalizeBasePath } from './basePath';
import { apiUrl, buildHeaders, buildQuery, isMutating, parseBody, parseError } from './http';

describe('normalizeBasePath', () => {
  it('印のままなら空（開発サーバ）', () => {
    expect(normalizeBasePath('__IDP_BASE_PATH__')).toBe('');
    expect(normalizeBasePath('')).toBe('');
    expect(normalizeBasePath(undefined)).toBe('');
  });

  it('末尾の / を除く', () => {
    expect(normalizeBasePath('/idp/')).toBe('/idp');
    expect(normalizeBasePath('/')).toBe('');
  });

  it('自サイト内の経路だけを受け付ける', () => {
    expect(normalizeBasePath('//example.com')).toBe('');
    expect(normalizeBasePath('https://example.com')).toBe('');
    expect(normalizeBasePath('idp')).toBe('');
  });
});

describe('normalizeAdminPath', () => {
  it('印のままなら サブパス + /admin', () => {
    expect(normalizeAdminPath('__IDP_ADMIN_PATH__', '/idp')).toBe('/idp/admin');
    expect(normalizeAdminPath(undefined, '')).toBe('/admin');
  });

  it('サーバが埋めた値（サブパス込み）をそのまま使う', () => {
    expect(normalizeAdminPath('/idp/console', '/idp')).toBe('/idp/console');
    expect(normalizeAdminPath('/console/', '')).toBe('/console');
  });

  it('おかしな値は既定へ', () => {
    expect(normalizeAdminPath('//evil.example', '/idp')).toBe('/idp/admin');
  });
});

describe('apiUrl', () => {
  it('サブパスの有無にかかわらず /api/admin から始まる', () => {
    expect(apiUrl('/status', '')).toBe('/api/admin/status');
    expect(apiUrl('/oidc-clients/a', '/idp')).toBe('/idp/api/admin/oidc-clients/a');
  });

  it('/ で始まらないパスは拒む', () => {
    expect(() => apiUrl('status', '')).toThrow();
    expect(() => appUrl('login')).toThrow();
  });
});

describe('loginUrl', () => {
  it('returnUrl を符号化して付ける', () => {
    expect(loginUrl('/idp/admin', '/idp')).toBe('/idp/login?returnUrl=%2Fidp%2Fadmin');
    expect(loginUrl('/admin', '')).toBe('/login?returnUrl=%2Fadmin');
  });
});

describe('ヘッダ', () => {
  it('更新系だけ X-IdP-Admin: 1 を付ける', () => {
    for (const method of ['POST', 'PUT', 'DELETE', 'post']) {
      expect(isMutating(method)).toBe(true);
      expect(buildHeaders(method, false, 'ja')['X-IdP-Admin']).toBe('1');
    }
    for (const method of ['GET', undefined]) {
      expect(isMutating(method)).toBe(false);
      expect(buildHeaders(method, false, 'ja')).not.toHaveProperty('X-IdP-Admin');
    }
  });

  it('本体があるときだけ Content-Type を付ける', () => {
    expect(buildHeaders('POST', true, 'ja')['Content-Type']).toBe('application/json');
    expect(buildHeaders('POST', false, 'ja')).not.toHaveProperty('Content-Type');
  });

  it('言語を Accept-Language で伝える', () => {
    expect(buildHeaders('GET', false, 'en, ja;q=0.8')['Accept-Language']).toBe('en, ja;q=0.8');
  });
});

describe('buildQuery', () => {
  it('空・未指定・false は含めない', () => {
    expect(buildQuery({ a: '', b: undefined, c: null, d: false })).toBe('');
  });

  it('値を符号化し、数値と true を含める', () => {
    expect(buildQuery({ actor: 'a b&c', limit: 100, offset: 0, failedOnly: true })).toBe(
      '?actor=a+b%26c&limit=100&offset=0&failedOnly=true',
    );
  });
});

describe('parseError', () => {
  it('{ error, message } を読む', () => {
    expect(parseError(409, { error: 'conflict', message: '重複しています' }, '既定')).toEqual({
      status: 409,
      code: 'conflict',
      message: '重複しています',
    });
  });

  it('message が無ければ既定の文言', () => {
    expect(parseError(500, { error: 'x' }, '既定')).toEqual({ status: 500, code: 'x', message: '既定' });
    expect(parseError(500, { message: '' }, '既定').message).toBe('既定');
  });

  it('本体が JSON でないときも壊れない', () => {
    expect(parseError(502, '<html>', '既定')).toEqual({ status: 502, code: '', message: '既定' });
    expect(parseError(502, undefined, '既定').message).toBe('既定');
  });
});

describe('parseBody', () => {
  it('空は undefined、JSON は解釈し、それ以外は文字列のまま', () => {
    expect(parseBody('')).toBeUndefined();
    expect(parseBody('{"a":1}')).toEqual({ a: 1 });
    expect(parseBody('-----BEGIN CERTIFICATE-----')).toBe('-----BEGIN CERTIFICATE-----');
  });
});
