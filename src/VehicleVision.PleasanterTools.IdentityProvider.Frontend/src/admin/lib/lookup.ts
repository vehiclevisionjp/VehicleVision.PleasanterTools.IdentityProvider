/**
 * ユーザ照会の結果の読み解き。**「なぜこの人は TOTP を求められる / 求められないのか」**を
 * 運用者が確かめるための文言の鍵を選ぶ。Svelte に依存させない。
 */
import type { MessageKey } from './i18n/messages';
import type { UserLookup } from './types';

export type TotpVerdict = 'notFound' | 'required' | 'requiredNoDevice' | 'notRequired' | 'notRequiredHasTotp';

/** 結果の分類。 */
export function totpVerdict(lookup: UserLookup): TotpVerdict {
  if (!lookup.found) {return 'notFound';}
  if (lookup.totpRequired) {return lookup.hasTotp ? 'required' : 'requiredNoDevice';}
  return lookup.hasTotp ? 'notRequiredHasTotp' : 'notRequired';
}

const SUMMARY_KEYS: Record<TotpVerdict, MessageKey> = {
  notFound: 'lookup.verdict.notFound',
  required: 'lookup.verdict.required',
  requiredNoDevice: 'lookup.verdict.requiredNoDevice',
  notRequired: 'lookup.verdict.notRequired',
  notRequiredHasTotp: 'lookup.verdict.notRequiredHasTotp',
};

const DETAIL_KEYS: Record<TotpVerdict, MessageKey> = {
  notFound: 'lookup.verdict.notFound.detail',
  required: 'lookup.verdict.required.detail',
  requiredNoDevice: 'lookup.verdict.requiredNoDevice.detail',
  notRequired: 'lookup.verdict.notRequired.detail',
  notRequiredHasTotp: 'lookup.verdict.notRequiredHasTotp.detail',
};

export function verdictSummaryKey(verdict: TotpVerdict): MessageKey {
  return SUMMARY_KEYS[verdict];
}

export function verdictDetailKey(verdict: TotpVerdict): MessageKey {
  return DETAIL_KEYS[verdict];
}

/** 色だけに頼らず出す、状態の種類（画面は印と文字を添える）。 */
export function verdictTone(verdict: TotpVerdict): 'ok' | 'warn' | 'info' | 'error' {
  switch (verdict) {
    case 'required':
      return 'ok';
    case 'requiredNoDevice':
      return 'warn';
    case 'notFound':
      return 'error';
    default:
      return 'info';
  }
}
