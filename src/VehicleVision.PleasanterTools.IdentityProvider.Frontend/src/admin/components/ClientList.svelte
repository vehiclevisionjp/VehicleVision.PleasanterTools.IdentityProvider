<script lang="ts">
  import { onMount } from 'svelte';
  import { deleteOidcClient, listOidcClients, reissueOidcClientSecret } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import { clientHash } from '../lib/route';
  import type { OidcClient } from '../lib/types';
  import ErrorBanner from './ErrorBanner.svelte';
  import SecretPanel from './SecretPanel.svelte';

  let clients = $state<OidcClient[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let issued = $state<{ clientId: string; secret: string } | null>(null);

  async function load(): Promise<void> {
    loading = true;
    error = null;
    const result = await listOidcClients();
    if (result.ok) {clients = result.value;}
    else {error = result.message;}
    loading = false;
  }

  onMount(() => void load());

  async function reissue(client: OidcClient): Promise<void> {
    const ok = await confirmAction({
      title: t('clients.reissue.title'),
      message: t('clients.reissue.message', { name: client.displayName }),
      confirmLabel: t('clients.reissue'),
      danger: true,
    });
    if (!ok) {return;}

    error = null;
    const result = await reissueOidcClientSecret(client.clientId);
    if (result.ok) {issued = { clientId: client.clientId, secret: result.value.clientSecret };}
    else {error = result.message;}
  }

  async function remove(client: OidcClient): Promise<void> {
    const ok = await confirmAction({
      title: t('clients.delete.title'),
      message: t('clients.delete.message', { name: client.displayName, clientId: client.clientId }),
      confirmLabel: t('common.delete'),
      danger: true,
    });
    if (!ok) {return;}

    error = null;
    const result = await deleteOidcClient(client.clientId);
    if (result.ok) {clients = clients.filter((item) => item.clientId !== client.clientId);}
    else {error = result.message;}
  }
</script>

<h1>{t('clients.title')}</h1>

<div class="toolbar">
  <a class="button-link" href={clientHash(null)}>
    <span class="material-icons" aria-hidden="true">add</span>
    {t('clients.create')}
  </a>
</div>

<ErrorBanner message={error} onretry={load} />

{#if issued}
  <SecretPanel clientId={issued.clientId} secret={issued.secret} ondone={() => (issued = null)} />
{/if}

{#if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if clients.length === 0}
  <p class="muted">{t('clients.empty')}</p>
{:else}
  <div class="table-wrap">
    <table>
      <caption class="visually-hidden">{t('clients.title')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('clients.displayName')}</th>
          <th scope="col">{t('clients.clientId')}</th>
          <th scope="col">{t('clients.type')}</th>
          <th scope="col">{t('clients.state')}</th>
          <th scope="col">{t('common.updatedAt')}</th>
          <th scope="col"><span class="visually-hidden">{t('common.actions')}</span></th>
        </tr>
      </thead>
      <tbody>
        {#each clients as client (client.clientId)}
          <tr>
            <td><a href={clientHash(client.clientId)}>{client.displayName}</a></td>
            <td class="mono">{client.clientId}</td>
            <td>{client.clientType === 'confidential' ? t('clients.type.confidential') : t('clients.type.public')}</td>
            <td>
              {#if client.enabled}
                <span class="badge ok">{t('common.enabled')}</span>
              {:else}
                <span class="badge">{t('common.disabled')}</span>
              {/if}
            </td>
            <td>{formatDateTime(client.updatedAt)}</td>
            <td>
              <div class="row-actions">
                <a class="button-link secondary small" href={clientHash(client.clientId)}>{t('common.edit')}</a>
                {#if client.clientType === 'confidential'}
                  <button type="button" class="secondary small" onclick={() => reissue(client)}>
                    {t('clients.reissue')}
                  </button>
                {/if}
                <button type="button" class="secondary danger small" onclick={() => remove(client)}>
                  {t('common.delete')}
                </button>
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}
