<script lang="ts">
  import { lookupPleasanterUser } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import { totpVerdict, verdictDetailKey, verdictSummaryKey, verdictTone } from '../lib/lookup';
  import type { UserLookup } from '../lib/types';
  import ErrorBanner from './ErrorBanner.svelte';

  let loginId = $state('');
  let loading = $state(false);
  let error = $state<string | null>(null);
  let result = $state<UserLookup>();
  let searched = $state('');

  const verdict = $derived(result ? totpVerdict(result) : null);
  const tone = $derived(verdict ? verdictTone(verdict) : 'info');
  const ICONS = { ok: 'check_circle', warn: 'warning', error: 'error', info: 'info' } as const;
  const icon = $derived(ICONS[tone]);

  async function lookup(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    const value = loginId.trim();
    if (value === '') return;

    loading = true;
    error = null;
    result = undefined;
    const response = await lookupPleasanterUser(value);
    if (response.ok) {
      result = response.value;
      searched = value;
    } else error = response.message;
    loading = false;
  }

  const yesNo = (value: boolean): string => (value ? t('common.yes') : t('common.no'));
</script>

<h1>{t('lookup.title')}</h1>
<p class="muted">{t('lookup.description')}</p>

<form class="toolbar" onsubmit={lookup} role="search">
  <label for="lookup-login">{t('lookup.loginId')}</label>
  <input id="lookup-login" type="search" bind:value={loginId} autocomplete="off" style="max-width: 18rem" required />
  <button type="submit" disabled={loading}>{loading ? t('common.loading') : t('lookup.run')}</button>
</form>

<ErrorBanner message={error} />

{#if result && verdict}
  <!-- **結果は先に結論を、色だけでなく印と文字で出す。** -->
  <div class="banner {tone}" role="status">
    <span class="material-icons" aria-hidden="true">{icon}</span>
    <div>
      <strong>{t(verdictSummaryKey(verdict))}</strong>
      <div>{t(verdictDetailKey(verdict))}</div>
    </div>
  </div>

  {#if result.found}
    <section class="card" aria-labelledby="lookup-detail">
      <h2 id="lookup-detail">{t('lookup.detail', { loginId: result.loginId ?? searched })}</h2>
      <dl class="kv">
        <dt>{t('lookup.name')}</dt>
        <dd>{result.name ?? '—'}</dd>
        <dt>{t('lookup.tenantId')}</dt>
        <dd>{result.tenantId ?? '—'}</dd>
        <dt>{t('lookup.userId')}</dt>
        <dd>{result.userId ?? '—'}</dd>
        <dt>{t('lookup.dept')}</dt>
        <dd>{result.dept ?? '—'}</dd>
        <dt>{t('lookup.mailAddresses')}</dt>
        <dd>{result.mailAddresses.length > 0 ? result.mailAddresses.join(', ') : '—'}</dd>
        <dt>{t('lookup.groups')}</dt>
        <dd>{result.groups.length > 0 ? result.groups.join(', ') : '—'}</dd>
        <dt>{t('lookup.disabled')}</dt>
        <dd>
          <span class="badge {result.disabled ? 'error' : ''}">{yesNo(result.disabled)}</span>
        </dd>
        <dt>{t('lookup.lockout')}</dt>
        <dd>
          <span class="badge {result.lockout ? 'error' : ''}">{yesNo(result.lockout)}</span>
        </dd>
        <dt>{t('lookup.tenantManager')}</dt>
        <dd>{yesNo(result.tenantManager)}</dd>
        <dt>{t('lookup.serviceManager')}</dt>
        <dd>{yesNo(result.serviceManager)}</dd>
        <dt>{t('lookup.hasTotp')}</dt>
        <dd>{yesNo(result.hasTotp)}</dd>
        <dt>{t('lookup.totpRequired')}</dt>
        <dd>
          <span class="badge {result.totpRequired ? 'ok' : ''}">{yesNo(result.totpRequired)}</span>
        </dd>
      </dl>
    </section>
  {/if}
{/if}
