<script lang="ts">
  import { onMount } from 'svelte';
  import { getStatus } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';
  import type { Status, StatusEndpoints } from '../lib/types';
  import CopyButton from './CopyButton.svelte';
  import ErrorBanner from './ErrorBanner.svelte';

  let status = $state<Status>();
  let error = $state<string | null>(null);
  let loading = $state(true);

  async function load(): Promise<void> {
    loading = true;
    error = null;
    const result = await getStatus();
    if (result.ok) status = result.value;
    else error = result.message;
    loading = false;
  }

  onMount(() => void load());

  // 表示順。**OIDC → SAML**
  const endpointRows: { key: keyof StatusEndpoints; label: MessageKey }[] = [
    { key: 'discovery', label: 'dashboard.endpoint.discovery' },
    { key: 'jwks', label: 'dashboard.endpoint.jwks' },
    { key: 'authorize', label: 'dashboard.endpoint.authorize' },
    { key: 'token', label: 'dashboard.endpoint.token' },
    { key: 'userinfo', label: 'dashboard.endpoint.userinfo' },
    { key: 'endSession', label: 'dashboard.endpoint.endSession' },
    { key: 'revocation', label: 'dashboard.endpoint.revocation' },
    { key: 'samlMetadata', label: 'dashboard.endpoint.samlMetadata' },
    { key: 'samlSso', label: 'dashboard.endpoint.samlSso' },
    { key: 'samlSlo', label: 'dashboard.endpoint.samlSlo' },
  ];
</script>

<h1>{t('dashboard.title')}</h1>

<ErrorBanner message={error} onretry={load} />

{#if loading && !status}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if status}
  <section class="card" aria-labelledby="endpoints-title">
    <h2 id="endpoints-title">{t('dashboard.endpoints')}</h2>
    <dl class="kv">
      <dt>{t('dashboard.issuer')}</dt>
      <dd>
        <span class="mono">{status.issuer}</span>
        <CopyButton text={status.issuer} subject={t('dashboard.issuer')} />
      </dd>
      {#each endpointRows as row (row.key)}
        <dt>{t(row.label)}</dt>
        <dd>
          <span class="mono">{status.endpoints[row.key]}</span>
          <CopyButton text={status.endpoints[row.key]} subject={t(row.label)} />
        </dd>
      {/each}
    </dl>
  </section>

  <div class="grid">
    <section class="card" aria-labelledby="db-title">
      <h2 id="db-title">{t('dashboard.database')}</h2>
      <dl class="kv">
        <dt>{t('dashboard.database.provider')}</dt>
        <dd>{status.database.provider}</dd>
        <dt>{t('dashboard.database.version')}</dt>
        <dd>
          {status.database.appliedVersion ?? '—'} / {status.database.latestVersion}
          {#if status.database.pendingCount > 0}
            <span class="badge warn">
              <span class="material-icons" aria-hidden="true">warning</span>
              {t('dashboard.database.pending', { count: status.database.pendingCount })}
            </span>
          {:else}
            <span class="badge ok">
              <span class="material-icons" aria-hidden="true">check_circle</span>
              {t('dashboard.database.upToDate')}
            </span>
          {/if}
        </dd>
      </dl>
    </section>

    <section class="card" aria-labelledby="pleasanter-title">
      <h2 id="pleasanter-title">{t('dashboard.pleasanter')}</h2>
      <dl class="kv">
        <dt>{t('dashboard.pleasanter.state')}</dt>
        <dd>
          {#if status.pleasanter.connected}
            <span class="badge ok">
              <span class="material-icons" aria-hidden="true">check_circle</span>
              {t('dashboard.pleasanter.connected')}
            </span>
          {:else}
            <span class="badge error">
              <span class="material-icons" aria-hidden="true">error</span>
              {t('dashboard.pleasanter.disconnected')}
            </span>
          {/if}
        </dd>
        <dt>{t('dashboard.pleasanter.provider')}</dt>
        <dd>{status.pleasanter.provider}</dd>
        <dt>{t('dashboard.pleasanter.userCount')}</dt>
        <dd>{status.pleasanter.userCount ?? '—'}</dd>
        {#if status.pleasanter.error}
          <dt>{t('dashboard.pleasanter.error')}</dt>
          <dd class="error-line">{status.pleasanter.error}</dd>
        {/if}
      </dl>
    </section>

    <section class="card" aria-labelledby="totp-title">
      <h2 id="totp-title">{t('dashboard.totp')}</h2>
      <dl class="kv">
        <dt>{t('dashboard.totp.mode')}</dt>
        <dd>
          {status.totp.mode}
          <span class="muted">
            ({status.totp.mode === 'Auto' ? t('dashboard.totp.mode.auto') : t('dashboard.totp.mode.pleasanter')})
          </span>
        </dd>
        <dt>{t('dashboard.totp.tolerances')}</dt>
        <dd>{status.totp.countTolerances}</dd>
      </dl>
    </section>

    <section class="card" aria-labelledby="session-title">
      <h2 id="session-title">{t('dashboard.session')}</h2>
      <dl class="kv">
        <dt>{t('dashboard.session.lifetime')}</dt>
        <dd>{t('common.minutes', { count: status.session.lifetimeMinutes })}</dd>
        <dt>{t('dashboard.session.idle')}</dt>
        <dd>{t('common.minutes', { count: status.session.idleMinutes })}</dd>
      </dl>
    </section>
  </div>

  <p class="muted version">{t('dashboard.version', { version: status.version })}</p>
{/if}

<style lang="scss">
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    gap: 1rem;
  }

  .grid .card {
    margin-bottom: 0;
  }

  .error-line {
    color: var(--error);
    word-break: break-word;
  }

  .version {
    margin-top: 1rem;
    font-family: var(--font-mono);
    font-size: 0.85rem;
  }
</style>
