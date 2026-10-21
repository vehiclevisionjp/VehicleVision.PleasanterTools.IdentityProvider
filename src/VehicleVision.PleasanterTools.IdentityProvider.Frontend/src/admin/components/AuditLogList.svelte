<script lang="ts">
  import { onMount } from 'svelte';
  import { listAuditLogs } from '../lib/api';
  import {
    AUDIT_PAGE_SIZE,
    buildAuditQuery,
    emptyAuditFilter,
    isFailure,
    type AuditFilter,
  } from '../lib/auditQuery';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import type { AuditLog } from '../lib/types';
  import ErrorBanner from './ErrorBanner.svelte';

  let filter = $state<AuditFilter>(emptyAuditFilter());
  let applied = $state<AuditFilter>(emptyAuditFilter());
  let page = $state(0);
  let logs = $state<AuditLog[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function load(): Promise<void> {
    loading = true;
    error = null;
    const result = await listAuditLogs(buildAuditQuery(applied, page));
    if (result.ok) logs = result.value;
    else error = result.message;
    loading = false;
  }

  onMount(() => void load());

  function search(event: SubmitEvent): void {
    event.preventDefault();
    applied = { ...filter };
    page = 0;
    void load();
  }

  function reset(): void {
    filter = emptyAuditFilter();
    applied = emptyAuditFilter();
    page = 0;
    void load();
  }

  function move(delta: number): void {
    page = Math.max(0, page + delta);
    void load();
  }
</script>

<h1>{t('audit.title')}</h1>

<form class="card" onsubmit={search} role="search" aria-label={t('audit.filter')}>
  <div class="filters">
    <div class="field">
      <label class="label" for="audit-from">{t('audit.from')}</label>
      <input id="audit-from" type="datetime-local" bind:value={filter.from} />
    </div>
    <div class="field">
      <label class="label" for="audit-to">{t('audit.to')}</label>
      <input id="audit-to" type="datetime-local" bind:value={filter.to} />
    </div>
    <div class="field">
      <label class="label" for="audit-actor">{t('audit.actor')}</label>
      <input id="audit-actor" type="text" bind:value={filter.actor} autocomplete="off" />
    </div>
    <div class="field">
      <label class="label" for="audit-action">{t('audit.action')}</label>
      <input id="audit-action" type="text" bind:value={filter.action} autocomplete="off" aria-describedby="audit-action-hint" />
      <span class="hint" id="audit-action-hint">{t('audit.action.hint')}</span>
    </div>
  </div>
  <div class="check">
    <input id="audit-failed" type="checkbox" bind:checked={filter.failedOnly} />
    <label for="audit-failed">{t('audit.failedOnly')}</label>
  </div>
  <div class="actions">
    <button type="submit">{t('common.search')}</button>
    <button type="button" class="secondary" onclick={reset}>{t('common.reset')}</button>
  </div>
</form>

<ErrorBanner message={error} onretry={load} />

{#if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if logs.length === 0}
  <p class="muted">{t('audit.empty')}</p>
  {#if page > 0}
    <button type="button" class="secondary" onclick={() => move(-1)}>{t('common.prev')}</button>
  {/if}
{:else}
  <div class="table-wrap">
    <table>
      <caption class="visually-hidden">{t('audit.title')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('audit.occurredAt')}</th>
          <th scope="col">{t('audit.action')}</th>
          <th scope="col">{t('audit.result')}</th>
          <th scope="col">{t('audit.actor')}</th>
          <th scope="col">{t('audit.target')}</th>
          <th scope="col">{t('audit.ipAddress')}</th>
          <th scope="col">{t('audit.detail')}</th>
        </tr>
      </thead>
      <tbody>
        {#each logs as log, index (index)}
          <tr>
            <td>{formatDateTime(log.occurredAt)}</td>
            <td class="mono">{log.action}</td>
            <td>
              {#if isFailure(log)}
                <span class="badge error">
                  <span class="material-icons" aria-hidden="true">error</span>
                  {t('audit.failure')}{log.statusCode !== null ? ` ${log.statusCode}` : ''}
                </span>
              {:else}
                <span class="badge ok">
                  <span class="material-icons" aria-hidden="true">check_circle</span>
                  {t('audit.success')}{log.statusCode !== null ? ` ${log.statusCode}` : ''}
                </span>
              {/if}
            </td>
            <td>{log.actorLoginId ?? log.actorUserKey ?? '—'}</td>
            <td class="mono">{log.targetType ?? '—'}{log.targetId ? ` / ${log.targetId}` : ''}</td>
            <td class="mono">{log.ipAddress ?? '—'}</td>
            <td>
              {#if log.detail}
                <details>
                  <summary>{t('audit.detail.show')}</summary>
                  <pre class="code">{JSON.stringify(log.detail, null, 2)}</pre>
                </details>
              {:else}
                —
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  <nav class="toolbar pager" aria-label={t('common.pager')}>
    <button type="button" class="secondary" onclick={() => move(-1)} disabled={page === 0}>{t('common.prev')}</button>
    <span class="muted">
      {t('common.range', { from: page * AUDIT_PAGE_SIZE + 1, to: page * AUDIT_PAGE_SIZE + logs.length })}
    </span>
    <button type="button" class="secondary" onclick={() => move(1)} disabled={logs.length < AUDIT_PAGE_SIZE}>
      {t('common.next')}
    </button>
  </nav>
{/if}

<style lang="scss">
  .filters {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
    gap: 0 1rem;
  }

  .filters :global(input) {
    max-width: none;
  }

  .pager {
    margin-top: 1rem;
  }

  summary {
    cursor: pointer;
    color: var(--accent);
  }
</style>
