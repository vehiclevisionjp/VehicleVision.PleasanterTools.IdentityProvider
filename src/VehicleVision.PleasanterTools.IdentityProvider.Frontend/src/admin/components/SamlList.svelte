<script lang="ts">
  import { onMount } from 'svelte';
  import { deleteSamlProvider, listSamlProviders } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import { samlProviderHash } from '../lib/route';
  import type { SamlProvider } from '../lib/types';
  import ErrorBanner from './ErrorBanner.svelte';

  let providers = $state<SamlProvider[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function load(): Promise<void> {
    loading = true;
    error = null;
    const result = await listSamlProviders();
    if (result.ok) providers = result.value;
    else error = result.message;
    loading = false;
  }

  onMount(() => void load());

  async function remove(provider: SamlProvider): Promise<void> {
    const ok = await confirmAction({
      title: t('saml.delete.title'),
      message: t('saml.delete.message', { name: provider.displayName, entityId: provider.entityId }),
      confirmLabel: t('common.delete'),
      danger: true,
    });
    if (!ok) return;

    error = null;
    const result = await deleteSamlProvider(provider.providerId);
    if (result.ok) providers = providers.filter((item) => item.providerId !== provider.providerId);
    else error = result.message;
  }
</script>

<h1>{t('saml.title')}</h1>

<div class="toolbar">
  <a class="button-link" href={samlProviderHash(null)}>
    <span class="material-icons" aria-hidden="true">add</span>
    {t('saml.create')}
  </a>
</div>

<ErrorBanner message={error} onretry={load} />

{#if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if providers.length === 0}
  <p class="muted">{t('saml.empty')}</p>
{:else}
  <div class="table-wrap">
    <table>
      <caption class="visually-hidden">{t('saml.title')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('saml.displayName')}</th>
          <th scope="col">{t('saml.entityId')}</th>
          <th scope="col">{t('saml.acsUrl')}</th>
          <th scope="col">{t('clients.state')}</th>
          <th scope="col">{t('common.updatedAt')}</th>
          <th scope="col"><span class="visually-hidden">{t('common.actions')}</span></th>
        </tr>
      </thead>
      <tbody>
        {#each providers as provider (provider.providerId)}
          <tr>
            <td><a href={samlProviderHash(provider.providerId)}>{provider.displayName}</a></td>
            <td class="mono">{provider.entityId}</td>
            <td class="mono">{provider.acsUrl}</td>
            <td>
              {#if provider.enabled}
                <span class="badge ok">{t('common.enabled')}</span>
              {:else}
                <span class="badge">{t('common.disabled')}</span>
              {/if}
            </td>
            <td>{formatDateTime(provider.updatedAt)}</td>
            <td>
              <div class="row-actions">
                <a class="button-link secondary small" href={samlProviderHash(provider.providerId)}>
                  {t('common.edit')}
                </a>
                <button type="button" class="secondary danger small" onclick={() => remove(provider)}>
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
