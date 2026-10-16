<script lang="ts">
  import { onMount } from 'svelte';
  import { listSessions, revokeSession } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import type { SsoSession } from '../lib/types';
  import ErrorBanner from './ErrorBanner.svelte';

  const PAGE_SIZE = 50;

  let sessions = $state<SsoSession[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let loginIdInput = $state('');
  let appliedLoginId = $state('');
  let offset = $state(0);

  async function load(): Promise<void> {
    loading = true;
    error = null;
    const result = await listSessions({ loginId: appliedLoginId, limit: PAGE_SIZE, offset });
    if (result.ok) sessions = result.value;
    else error = result.message;
    loading = false;
  }

  onMount(() => void load());

  function search(event: SubmitEvent): void {
    event.preventDefault();
    appliedLoginId = loginIdInput.trim();
    offset = 0;
    void load();
  }

  function move(delta: number): void {
    offset = Math.max(0, offset + delta);
    void load();
  }

  async function revoke(session: SsoSession): Promise<void> {
    const ok = await confirmAction({
      title: t('sessions.revoke.title'),
      message: session.current
        ? t('sessions.revoke.messageSelf')
        : t('sessions.revoke.message', { loginId: session.loginId }),
      confirmLabel: t('sessions.revoke'),
      danger: true,
    });
    if (!ok) return;

    error = null;
    const result = await revokeSession(session.sessionId);
    if (result.ok) sessions = sessions.filter((item) => item.sessionId !== session.sessionId);
    else error = result.message;
  }
</script>

<h1>{t('sessions.title')}</h1>

<form class="toolbar" onsubmit={search} role="search">
  <label for="session-login">{t('sessions.loginId')}</label>
  <input id="session-login" type="search" bind:value={loginIdInput} autocomplete="off" class="narrow" />
  <button type="submit">{t('common.search')}</button>
</form>

<ErrorBanner message={error} onretry={load} />

{#if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if sessions.length === 0}
  <p class="muted">{t('sessions.empty')}</p>
  {#if offset > 0}
    <button type="button" class="secondary" onclick={() => move(-PAGE_SIZE)}>{t('common.prev')}</button>
  {/if}
{:else}
  <div class="table-wrap">
    <table>
      <caption class="visually-hidden">{t('sessions.title')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('sessions.user')}</th>
          <th scope="col">{t('sessions.authTime')}</th>
          <th scope="col">{t('sessions.lastSeenAt')}</th>
          <th scope="col">{t('sessions.expiresAt')}</th>
          <th scope="col">{t('sessions.client')}</th>
          <th scope="col">{t('sessions.authMethods')}</th>
          <th scope="col"><span class="visually-hidden">{t('common.actions')}</span></th>
        </tr>
      </thead>
      <tbody>
        {#each sessions as session (session.sessionId)}
          <tr>
            <td>
              {session.name}
              <span class="muted">({session.loginId})</span>
              {#if session.current}
                <!-- **自分のセッションは印を付ける。** 色だけでなく文字でも -->
                <span class="badge info">
                  <span class="material-icons" aria-hidden="true">person</span>
                  {t('sessions.current')}
                </span>
              {/if}
              <div class="muted small-text">{t('sessions.tenant', { tenantId: session.tenantId })}</div>
            </td>
            <td>{formatDateTime(session.authTime)}</td>
            <td>{formatDateTime(session.lastSeenAt)}</td>
            <td>{formatDateTime(session.expiresAt)}</td>
            <td>
              <div class="mono">{session.ipAddress ?? '—'}</div>
              <div class="muted small-text ua">{session.userAgent ?? ''}</div>
            </td>
            <td>{session.authMethods.join(', ')}</td>
            <td>
              <button type="button" class="secondary danger small" onclick={() => revoke(session)}>
                {t('sessions.revoke')}
              </button>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  <nav class="toolbar pager" aria-label={t('common.pager')}>
    <button type="button" class="secondary" onclick={() => move(-PAGE_SIZE)} disabled={offset === 0}>
      {t('common.prev')}
    </button>
    <span class="muted">{t('common.range', { from: offset + 1, to: offset + sessions.length })}</span>
    <button type="button" class="secondary" onclick={() => move(PAGE_SIZE)} disabled={sessions.length < PAGE_SIZE}>
      {t('common.next')}
    </button>
  </nav>
{/if}

<style lang="scss">
  // **style 属性を使わない。** CSP（style-src 'self'）がインラインのスタイルを拒むため
  .narrow {
    max-width: 16rem;
  }

  .small-text {
    font-size: 0.8rem;
  }

  .ua {
    max-width: 22rem;
    word-break: break-all;
  }

  .pager {
    margin-top: 1rem;
  }
</style>
