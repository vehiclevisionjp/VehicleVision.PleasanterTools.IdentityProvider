<script lang="ts">
  import { onMount } from 'svelte';
  import {
    createSamlProvider,
    deleteSamlProvider,
    getSamlProvider,
    importSamlMetadata,
    updateSamlProvider,
  } from '../lib/api';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { t } from '../lib/i18n/state.svelte';
  import type { MessageKey } from '../lib/i18n/messages';
  import { samlProviderHash } from '../lib/route';
  import {
    ATTRIBUTE_SOURCES,
    NAME_ID_FORMATS,
    NAME_ID_SOURCES,
    type SamlProvider,
  } from '../lib/types';
  import {
    applyImportedMetadata,
    emptySamlForm,
    samlFormFromProvider,
    validateSamlForm,
    type FieldError,
    type SamlFormField,
    type SamlFormState,
  } from '../lib/validation';
  import AccessRulesEditor from './AccessRulesEditor.svelte';
  import ErrorBanner from './ErrorBanner.svelte';

  interface Props {
    /** `null` なら新規作成 */
    providerId: string | null;
  }

  let { providerId }: Props = $props();

  const mode = $derived(providerId === null ? 'create' : 'edit');

  let form = $state<SamlFormState>(emptySamlForm());
  let loaded = $state<SamlProvider>();
  let loading = $state(true);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let saved = $state(false);
  let fieldErrors = $state<Partial<Record<SamlFormField, FieldError>>>({});

  let metadataXml = $state('');
  let importing = $state(false);
  let importError = $state<string | null>(null);
  let imported = $state(false);

  async function load(): Promise<void> {
    if (providerId === null) {
      loading = false;
      return;
    }
    loading = true;
    error = null;
    const result = await getSamlProvider(providerId);
    if (result.ok) {
      loaded = result.value;
      form = samlFormFromProvider(result.value);
    } else {
      error = result.message;
    }
    loading = false;
  }

  onMount(() => void load());

  function message(field: SamlFormField): string | null {
    const e = fieldErrors[field];
    return e ? t(e.key, e.params) : null;
  }

  const nameIdFormatKeys: Record<(typeof NAME_ID_FORMATS)[number], MessageKey> = {
    emailAddress: 'saml.nameIdFormat.emailAddress',
    persistent: 'saml.nameIdFormat.persistent',
    unspecified: 'saml.nameIdFormat.unspecified',
    transient: 'saml.nameIdFormat.transient',
  };

  const nameIdSourceKeys: Record<(typeof NAME_ID_SOURCES)[number], MessageKey> = {
    loginId: 'source.loginId',
    email: 'source.email',
    userId: 'source.userId',
    globalId: 'source.globalId',
  };

  const attributeSourceKeys: Record<(typeof ATTRIBUTE_SOURCES)[number], MessageKey> = {
    loginId: 'source.loginId',
    email: 'source.email',
    name: 'source.name',
    userId: 'source.userId',
    userCode: 'source.userCode',
    dept: 'source.dept',
    groups: 'source.groups',
    tenantId: 'source.tenantId',
    language: 'source.language',
  };

  function addMapping(): void {
    form.attributeMappings = [...form.attributeMappings, { attributeName: '', source: 'email' }];
  }

  function removeMapping(index: number): void {
    form.attributeMappings = form.attributeMappings.filter((_, i) => i !== index);
  }

  async function runImport(): Promise<void> {
    importError = null;
    imported = false;
    if (metadataXml.trim() === '') {
      importError = t('saml.import.empty');
      return;
    }
    importing = true;
    const result = await importSamlMetadata(metadataXml);
    if (result.ok) {
      // **保存はしない。** フォームへ埋めるだけで、保存は利用者が確かめてから
      form = applyImportedMetadata(form, result.value);
      imported = true;
    } else {importError = result.message;}
    importing = false;
  }

  async function submit(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    error = null;
    saved = false;

    const validated = validateSamlForm(form);
    if (!validated.ok) {
      fieldErrors = validated.errors;
      return;
    }
    fieldErrors = {};

    saving = true;
    if (providerId === null) {
      const result = await createSamlProvider(validated.value);
      if (result.ok) {location.hash = samlProviderHash(result.value.providerId);}
      else {error = result.message;}
    } else {
      const result = await updateSamlProvider(providerId, validated.value);
      if (result.ok) {
        loaded = result.value;
        form = samlFormFromProvider(result.value);
        saved = true;
      } else {error = result.message;}
    }
    saving = false;
  }

  async function remove(): Promise<void> {
    if (!loaded) {return;}
    const ok = await confirmAction({
      title: t('saml.delete.title'),
      message: t('saml.delete.message', { name: loaded.displayName, entityId: loaded.entityId }),
      confirmLabel: t('common.delete'),
      danger: true,
    });
    if (!ok) {return;}

    error = null;
    const result = await deleteSamlProvider(loaded.providerId);
    if (result.ok) {location.hash = '#/saml';}
    else {error = result.message;}
  }
</script>

<h1>{mode === 'create' ? t('saml.create') : t('saml.edit')}</h1>

<p><a href="#/saml">{t('common.backToList')}</a></p>

<ErrorBanner message={error} />

{#if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else if mode === 'edit' && !loaded}
  <!-- 読み込みに失敗した（エラーは上に出ている） -->
{:else}
  <section class="card" aria-labelledby="import-title">
    <h2 id="import-title">{t('saml.import.title')}</h2>
    <div class="field">
      <label class="label" for="metadata-xml">{t('saml.import.label')}</label>
      <textarea id="metadata-xml" bind:value={metadataXml} spellcheck="false" aria-describedby="import-hint"></textarea>
      <span class="hint" id="import-hint">{t('saml.import.hint')}</span>
    </div>
    <button type="button" class="secondary" onclick={runImport} disabled={importing}>
      {importing ? t('common.loading') : t('saml.import.run')}
    </button>
    <ErrorBanner message={importError} />
    {#if imported}
      <div class="banner ok" role="status">
        <span class="material-icons" aria-hidden="true">check_circle</span>
        {t('saml.import.done')}
      </div>
    {/if}
  </section>

  <form onsubmit={submit} novalidate>
    {#if saved}
      <div class="banner ok" role="status">
        <span class="material-icons" aria-hidden="true">check_circle</span>
        {t('common.saved')}
      </div>
    {/if}

    <div class="field">
      <label class="label" for="saml-name">{t('saml.displayName')}</label>
      <input id="saml-name" type="text" bind:value={form.displayName} required aria-invalid={fieldErrors.displayName ? 'true' : undefined} />
      {#if message('displayName')}<span class="error-text" role="alert">{message('displayName')}</span>{/if}
    </div>

    <div class="field">
      <label class="label" for="saml-entity">{t('saml.entityId')}</label>
      <input id="saml-entity" type="text" bind:value={form.entityId} required autocomplete="off" aria-invalid={fieldErrors.entityId ? 'true' : undefined} />
      {#if message('entityId')}<span class="error-text" role="alert">{message('entityId')}</span>{/if}
    </div>

    <div class="field">
      <label class="label" for="saml-acs">{t('saml.acsUrl')}</label>
      <input id="saml-acs" type="url" bind:value={form.acsUrl} required autocomplete="off" aria-invalid={fieldErrors.acsUrl ? 'true' : undefined} />
      {#if message('acsUrl')}<span class="error-text" role="alert">{message('acsUrl')}</span>{/if}
    </div>

    <div class="field">
      <label class="label" for="saml-slo">{t('saml.sloUrl')}</label>
      <input id="saml-slo" type="url" bind:value={form.sloUrl} autocomplete="off" aria-invalid={fieldErrors.sloUrl ? 'true' : undefined} />
      {#if message('sloUrl')}<span class="error-text" role="alert">{message('sloUrl')}</span>{/if}
    </div>

    <div class="field">
      <label class="label" for="saml-cert">{t('saml.signingCertificate')}</label>
      <textarea
        id="saml-cert"
        bind:value={form.signingCertificate}
        spellcheck="false"
        aria-invalid={fieldErrors.signingCertificate ? 'true' : undefined}
        aria-describedby="saml-cert-hint"
      ></textarea>
      <span class="hint" id="saml-cert-hint">{t('saml.signingCertificate.hint')}</span>
      {#if message('signingCertificate')}
        <span class="error-text" role="alert">{message('signingCertificate')}</span>
      {/if}
    </div>

    <div class="field">
      <label class="label" for="saml-nameid-format">{t('saml.nameIdFormat')}</label>
      <select id="saml-nameid-format" bind:value={form.nameIdFormat}>
        {#each NAME_ID_FORMATS as option (option)}
          <option value={option}>{t(nameIdFormatKeys[option])}</option>
        {/each}
      </select>
    </div>

    <div class="field">
      <label class="label" for="saml-nameid-source">{t('saml.nameIdSource')}</label>
      <select id="saml-nameid-source" bind:value={form.nameIdSource}>
        {#each NAME_ID_SOURCES as option (option)}
          <option value={option}>{t(nameIdSourceKeys[option])}</option>
        {/each}
      </select>
    </div>

    <fieldset aria-describedby={fieldErrors.attributeMappings ? 'mapping-error' : undefined}>
      <legend>{t('saml.attributeMappings')}</legend>
      {#if form.attributeMappings.length === 0}
        <p class="muted">{t('saml.attributeMappings.empty')}</p>
      {:else}
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t('saml.attributeName')}</th>
                <th scope="col">{t('saml.attributeSource')}</th>
                <th scope="col"><span class="visually-hidden">{t('common.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {#each form.attributeMappings as mapping, index (index)}
                <tr>
                  <td>
                    <input
                      type="text"
                      bind:value={mapping.attributeName}
                      aria-label={t('saml.attributeNameRow', { row: index + 1 })}
                      autocomplete="off"
                    />
                  </td>
                  <td>
                    <select bind:value={mapping.source} aria-label={t('saml.attributeSourceRow', { row: index + 1 })}>
                      {#each ATTRIBUTE_SOURCES as option (option)}
                        <option value={option}>{t(attributeSourceKeys[option])}</option>
                      {/each}
                    </select>
                  </td>
                  <td>
                    <button
                      type="button"
                      class="secondary danger small"
                      onclick={() => removeMapping(index)}
                      aria-label={t('saml.attributeRemoveRow', { row: index + 1 })}
                    >
                      {t('common.remove')}
                    </button>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}
      <p>
        <button type="button" class="secondary small" onclick={addMapping}>
          <span class="material-icons" aria-hidden="true">add</span>
          {t('saml.attributeAdd')}
        </button>
      </p>
      {#if message('attributeMappings')}
        <span class="error-text" id="mapping-error" role="alert">{message('attributeMappings')}</span>
      {/if}
    </fieldset>

    <fieldset>
      <legend>{t('saml.signing')}</legend>
      <div class="check">
        <input id="saml-sign-assertion" type="checkbox" bind:checked={form.signAssertion} />
        <label for="saml-sign-assertion">{t('saml.signAssertion')}</label>
      </div>
      <div class="check">
        <input id="saml-sign-response" type="checkbox" bind:checked={form.signResponse} />
        <label for="saml-sign-response">{t('saml.signResponse')}</label>
      </div>
      <div class="check">
        <input id="saml-require-signed" type="checkbox" bind:checked={form.requireSignedRequests} />
        <label for="saml-require-signed">{t('saml.requireSignedRequests')}</label>
      </div>
    </fieldset>

    <div class="field">
      <label class="label" for="saml-tenants">{t('common.tenantIds')}</label>
      <input
        id="saml-tenants"
        type="text"
        bind:value={form.tenantIds}
        autocomplete="off"
        inputmode="numeric"
        aria-invalid={fieldErrors.tenantIds ? 'true' : undefined}
        aria-describedby="saml-tenants-hint"
      />
      <span class="hint" id="saml-tenants-hint">{t('common.tenantIds.hint')}</span>
      {#if message('tenantIds')}<span class="error-text" role="alert">{message('tenantIds')}</span>{/if}
    </div>

    <AccessRulesEditor bind:rules={form.accessRules} error={message('accessRules')} idPrefix="saml-rules" />

    <div class="check">
      <input id="saml-enabled" type="checkbox" bind:checked={form.enabled} />
      <label for="saml-enabled">{t('common.enabled')}</label>
    </div>

    <div class="actions">
      <button type="submit" disabled={saving}>{saving ? t('common.saving') : t('common.save')}</button>
      <a class="button-link secondary" href="#/saml">{t('common.cancel')}</a>
      {#if mode === 'edit' && loaded}
        <button type="button" class="secondary danger" onclick={remove}>{t('common.delete')}</button>
      {/if}
    </div>
  </form>
{/if}
