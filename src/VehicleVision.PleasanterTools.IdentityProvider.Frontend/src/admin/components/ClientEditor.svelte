<script lang="ts">
  import { onMount } from 'svelte';
  import {
    createOidcClient,
    deleteOidcClient,
    getOidcClient,
    reissueOidcClientSecret,
    updateOidcClient,
  } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { t } from '../lib/i18n/state.svelte';
  import { clientHash } from '../lib/route';
  import { OIDC_SCOPES, type OidcClient } from '../lib/types';
  import {
    emptyOidcForm,
    oidcFormFromClient,
    validateOidcForm,
    type FieldError,
    type OidcFormField,
    type OidcFormState,
  } from '../lib/validation';
  import AccessRulesEditor from './AccessRulesEditor.svelte';
  import ErrorBanner from './ErrorBanner.svelte';
  import SecretPanel from './SecretPanel.svelte';

  interface Props {
    /** `null` なら新規作成 */
    clientId: string | null;
  }

  let { clientId }: Props = $props();

  const mode = $derived(clientId === null ? 'create' : 'edit');

  let form = $state<OidcFormState>(emptyOidcForm());
  let loaded = $state<OidcClient>();
  let loading = $state(true);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let saved = $state(false);
  let fieldErrors = $state<Partial<Record<OidcFormField, FieldError>>>({});
  let issued = $state<{ clientId: string; secret: string; next: string } | null>(null);

  async function load(): Promise<void> {
    if (clientId === null) {
      loading = false;
      return;
    }
    loading = true;
    error = null;
    const result = await getOidcClient(clientId);
    if (result.ok) {
      loaded = result.value;
      form = oidcFormFromClient(result.value);
    } else {
      error = result.message;
    }
    loading = false;
  }

  onMount(() => void load());

  function message(field: OidcFormField): string | null {
    const e = fieldErrors[field];
    return e ? t(e.key, e.params) : null;
  }

  function toggleScope(scope: string, checked: boolean): void {
    form.scopes = checked ? [...form.scopes, scope] : form.scopes.filter((s) => s !== scope);
  }

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    error = null;
    saved = false;

    const validated = validateOidcForm(form, mode);
    if (!validated.ok) {
      fieldErrors = validated.errors;
      return;
    }
    fieldErrors = {};

    saving = true;
    if (clientId === null) {
      const result = await createOidcClient(validated.value);
      if (result.ok) {
        const created = result.value.client;
        if (result.value.clientSecret) {
          // **シークレットはこの 1 回だけ。** 控えるまで画面を進めない
          issued = { clientId: created.clientId, secret: result.value.clientSecret, next: clientHash(created.clientId) };
        } else {
          location.hash = clientHash(created.clientId);
        }
      } else error = result.message;
    } else {
      const result = await updateOidcClient(clientId, validated.value);
      if (result.ok) {
        loaded = result.value;
        form = oidcFormFromClient(result.value);
        saved = true;
      } else error = result.message;
    }
    saving = false;
  }

  async function reissue(): Promise<void> {
    if (!loaded) return;
    const ok = await confirmAction({
      title: t('clients.reissue.title'),
      message: t('clients.reissue.message', { name: loaded.displayName }),
      confirmLabel: t('clients.reissue'),
      danger: true,
    });
    if (!ok) return;

    error = null;
    const result = await reissueOidcClientSecret(loaded.clientId);
    if (result.ok) {
      issued = { clientId: loaded.clientId, secret: result.value.clientSecret, next: '' };
    } else error = result.message;
  }

  async function remove(): Promise<void> {
    if (!loaded) return;
    const ok = await confirmAction({
      title: t('clients.delete.title'),
      message: t('clients.delete.message', { name: loaded.displayName, clientId: loaded.clientId }),
      confirmLabel: t('common.delete'),
      danger: true,
    });
    if (!ok) return;

    error = null;
    const result = await deleteOidcClient(loaded.clientId);
    if (result.ok) location.hash = '#/clients';
    else error = result.message;
  }

  function closeSecret(): void {
    const next = issued?.next ?? '';
    issued = null;
    if (next !== '') location.hash = next;
  }
</script>

<h1>{mode === 'create' ? t('clients.create') : t('clients.edit')}</h1>

<p><a href="#/clients">{t('common.backToList')}</a></p>

<ErrorBanner message={error} />

{#if issued}
  <SecretPanel clientId={issued.clientId} secret={issued.secret} ondone={closeSecret} />
{:else if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if mode === 'edit' && !loaded}
  <!-- 読み込みに失敗した（エラーは上に出ている） -->
{:else}
  <form onsubmit={submit} novalidate>
    {#if saved}
      <div class="banner ok" role="status">
        <span class="material-icons" aria-hidden="true">check_circle</span>
        {t('common.saved')}
      </div>
    {/if}

    <div class="field">
      <label class="label" for="client-id">{t('clients.clientId')}</label>
      <input
        id="client-id"
        type="text"
        bind:value={form.clientId}
        readonly={mode === 'edit'}
        autocomplete="off"
        aria-invalid={fieldErrors.clientId ? 'true' : undefined}
        aria-describedby="client-id-hint"
      />
      <span class="hint" id="client-id-hint">
        {mode === 'create' ? t('clients.clientId.hintCreate') : t('clients.clientId.hintEdit')}
      </span>
      {#if message('clientId')}<span class="error-text" role="alert">{message('clientId')}</span>{/if}
    </div>

    <div class="field">
      <label class="label" for="client-name">{t('clients.displayName')}</label>
      <input
        id="client-name"
        type="text"
        bind:value={form.displayName}
        required
        aria-invalid={fieldErrors.displayName ? 'true' : undefined}
      />
      {#if message('displayName')}<span class="error-text" role="alert">{message('displayName')}</span>{/if}
    </div>

    <fieldset>
      <legend>{t('clients.type')}</legend>
      <div class="check">
        <input
          id="type-confidential"
          type="radio"
          name="client-type"
          value="confidential"
          bind:group={form.clientType}
          disabled={mode === 'edit'}
        />
        <label for="type-confidential">{t('clients.type.confidential')}</label>
      </div>
      <div class="check">
        <input
          id="type-public"
          type="radio"
          name="client-type"
          value="public"
          bind:group={form.clientType}
          disabled={mode === 'edit'}
        />
        <label for="type-public">{t('clients.type.public')}</label>
      </div>
      <span class="hint muted">
        {form.clientType === 'public' ? t('clients.type.publicHint') : t('clients.type.confidentialHint')}
        {#if mode === 'edit'}{t('clients.type.fixed')}{/if}
      </span>
    </fieldset>

    <div class="field">
      <label class="label" for="redirect-uris">{t('clients.redirectUris')}</label>
      <textarea
        id="redirect-uris"
        bind:value={form.redirectUris}
        spellcheck="false"
        aria-invalid={fieldErrors.redirectUris ? 'true' : undefined}
        aria-describedby="redirect-uris-hint"
      ></textarea>
      <span class="hint" id="redirect-uris-hint">{t('clients.redirectUris.hint')}</span>
      {#if message('redirectUris')}<span class="error-text" role="alert">{message('redirectUris')}</span>{/if}
    </div>

    <div class="field">
      <label class="label" for="post-logout-uris">{t('clients.postLogoutRedirectUris')}</label>
      <textarea
        id="post-logout-uris"
        bind:value={form.postLogoutRedirectUris}
        spellcheck="false"
        aria-invalid={fieldErrors.postLogoutRedirectUris ? 'true' : undefined}
      ></textarea>
      {#if message('postLogoutRedirectUris')}
        <span class="error-text" role="alert">{message('postLogoutRedirectUris')}</span>
      {/if}
    </div>

    <fieldset aria-describedby={fieldErrors.scopes ? 'scopes-error' : undefined}>
      <legend>{t('clients.scopes')}</legend>
      {#each OIDC_SCOPES as scope (scope)}
        <div class="check">
          <input
            id={`scope-${scope}`}
            type="checkbox"
            checked={form.scopes.includes(scope)}
            onchange={(event) => toggleScope(scope, event.currentTarget.checked)}
          />
          <label for={`scope-${scope}`}><code class="mono">{scope}</code></label>
        </div>
      {/each}
      {#if message('scopes')}<span class="error-text" id="scopes-error" role="alert">{message('scopes')}</span>{/if}
    </fieldset>

    <div class="field">
      <label class="label" for="tenant-ids">{t('common.tenantIds')}</label>
      <input
        id="tenant-ids"
        type="text"
        bind:value={form.tenantIds}
        autocomplete="off"
        inputmode="numeric"
        aria-invalid={fieldErrors.tenantIds ? 'true' : undefined}
        aria-describedby="tenant-ids-hint"
      />
      <span class="hint" id="tenant-ids-hint">{t('common.tenantIds.hint')}</span>
      {#if message('tenantIds')}<span class="error-text" role="alert">{message('tenantIds')}</span>{/if}
    </div>

    <AccessRulesEditor bind:rules={form.accessRules} error={message('accessRules')} idPrefix="oidc-rules" />

    <div class="field">
      <label class="label" for="access-lifetime">{t('clients.accessTokenLifetime')}</label>
      <input
        id="access-lifetime"
        type="text"
        inputmode="numeric"
        bind:value={form.accessTokenLifetimeSeconds}
        aria-invalid={fieldErrors.accessTokenLifetimeSeconds ? 'true' : undefined}
        aria-describedby="lifetime-hint"
      />
      {#if message('accessTokenLifetimeSeconds')}
        <span class="error-text" role="alert">{message('accessTokenLifetimeSeconds')}</span>
      {/if}
    </div>

    <div class="field">
      <label class="label" for="refresh-lifetime">{t('clients.refreshTokenLifetime')}</label>
      <input
        id="refresh-lifetime"
        type="text"
        inputmode="numeric"
        bind:value={form.refreshTokenLifetimeSeconds}
        aria-invalid={fieldErrors.refreshTokenLifetimeSeconds ? 'true' : undefined}
        aria-describedby="lifetime-hint"
      />
      <span class="hint" id="lifetime-hint">{t('clients.lifetime.hint')}</span>
      {#if message('refreshTokenLifetimeSeconds')}
        <span class="error-text" role="alert">{message('refreshTokenLifetimeSeconds')}</span>
      {/if}
    </div>

    <div class="check">
      <input id="client-enabled" type="checkbox" bind:checked={form.enabled} />
      <label for="client-enabled">{t('common.enabled')}</label>
    </div>

    {#if loaded}
      <p class="muted">
        {t('clients.secretState')}:
        {loaded.hasSecret ? t('clients.secret.has') : t('clients.secret.none')}
      </p>
    {/if}

    <div class="actions">
      <button type="submit" disabled={saving}>{saving ? t('common.saving') : t('common.save')}</button>
      <a class="button-link secondary" href="#/clients">{t('common.cancel')}</a>
      {#if mode === 'edit' && loaded}
        {#if loaded.clientType === 'confidential'}
          <button type="button" class="secondary" onclick={reissue}>{t('clients.reissue')}</button>
        {/if}
        <button type="button" class="secondary danger" onclick={remove}>{t('common.delete')}</button>
      {/if}
    </div>
  </form>
{/if}
