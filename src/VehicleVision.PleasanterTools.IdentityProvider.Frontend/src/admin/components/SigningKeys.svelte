<script lang="ts">
  import { onMount } from 'svelte';
  import { deleteSigningKey, getSigningKeyCertificate, listSigningKeys, rotateSigningKey } from '../lib/api';
  import { downloadText } from '../lib/clipboard';
  import { confirmAction } from '../lib/confirmation.svelte';
  import { formatDateTime, t } from '../lib/i18n/state.svelte';
  import type { KeyPurpose, SigningKey } from '../lib/types';
  import CopyButton from './CopyButton.svelte';
  import ErrorBanner from './ErrorBanner.svelte';

  let keys = $state<SigningKey[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let notice = $state<string | null>(null);
  let rotating = $state<KeyPurpose | null>(null);
  let certificate = $state<{ keyId: string; pem: string } | null>(null);

  const purposes: KeyPurpose[] = ['oidc', 'saml'];

  async function load(): Promise<void> {
    loading = true;
    error = null;
    const result = await listSigningKeys();
    if (result.ok) {keys = result.value;}
    else {error = result.message;}
    loading = false;
  }

  onMount(() => void load());

  const keysOf = (purpose: KeyPurpose) => keys.filter((key) => key.purpose === purpose);

  async function rotate(purpose: KeyPurpose): Promise<void> {
    const ok = await confirmAction({
      title: t('keys.rotate.title'),
      message: t(purpose === 'oidc' ? 'keys.rotate.message.oidc' : 'keys.rotate.message.saml'),
      confirmLabel: t('keys.rotate'),
      danger: true,
    });
    if (!ok) {return;}

    error = null;
    notice = null;
    rotating = purpose;
    const result = await rotateSigningKey(purpose);
    rotating = null;
    if (result.ok) {
      notice = t('keys.rotated', { keyId: result.value.keyId });
      await load();
    } else {error = result.message;}
  }

  async function remove(key: SigningKey): Promise<void> {
    const ok = await confirmAction({
      title: t('keys.delete.title'),
      message: t('keys.delete.message', { keyId: key.keyId }),
      confirmLabel: t('common.delete'),
      danger: true,
    });
    if (!ok) {return;}

    error = null;
    notice = null;
    const result = await deleteSigningKey(key.keyId);
    if (result.ok) {
      if (certificate?.keyId === key.keyId) {certificate = null;}
      keys = keys.filter((item) => item.keyId !== key.keyId);
    } else {error = result.message;}
  }

  async function showCertificate(key: SigningKey): Promise<void> {
    error = null;
    const result = await getSigningKeyCertificate(key.keyId);
    if (result.ok) {certificate = { keyId: key.keyId, pem: result.value };}
    else {error = result.message;}
  }
</script>

<h1>{t('keys.title')}</h1>

<ErrorBanner message={error} onretry={load} />
{#if notice}
  <div class="banner ok" role="status">
    <span class="material-icons" aria-hidden="true">check_circle</span>
    {notice}
  </div>
{/if}

{#if loading}
  <p class="muted" role="status">{t('common.loading')}</p>
{:else}
  {#each purposes as purpose (purpose)}
    <section class="card" aria-labelledby={`keys-${purpose}`}>
      <div class="toolbar">
        <h2 id={`keys-${purpose}`}>{t(purpose === 'oidc' ? 'keys.purpose.oidc' : 'keys.purpose.saml')}</h2>
        <span class="spacer"></span>
        <button type="button" class="secondary" onclick={() => rotate(purpose)} disabled={rotating !== null}>
          <span class="material-icons" aria-hidden="true">autorenew</span>
          {rotating === purpose ? t('common.loading') : t('keys.rotate')}
        </button>
      </div>

      {#if keysOf(purpose).length === 0}
        <p class="muted">{t('keys.empty')}</p>
      {:else}
        <div class="table-wrap">
          <table>
            <caption class="visually-hidden">
              {t(purpose === 'oidc' ? 'keys.purpose.oidc' : 'keys.purpose.saml')}
            </caption>
            <thead>
              <tr>
                <th scope="col">{t('keys.keyId')}</th>
                <th scope="col">{t('keys.status')}</th>
                <th scope="col">{t('keys.algorithm')}</th>
                <th scope="col">{t('keys.thumbprint')}</th>
                <th scope="col">{t('keys.validity')}</th>
                <th scope="col"><span class="visually-hidden">{t('common.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {#each keysOf(purpose) as key (key.keyId)}
                <tr>
                  <td class="mono">{key.keyId}</td>
                  <td>
                    {#if key.status === 'active'}
                      <span class="badge ok">{t('keys.status.active')}</span>
                    {:else}
                      <span class="badge">{t('keys.status.retired')}</span>
                    {/if}
                  </td>
                  <td>{key.algorithm}</td>
                  <td class="mono">{key.thumbprint}</td>
                  <td>{formatDateTime(key.notBefore)} – {formatDateTime(key.notAfter)}</td>
                  <td>
                    <div class="row-actions">
                      {#if key.purpose === 'saml'}
                        <button type="button" class="secondary small" onclick={() => showCertificate(key)}>
                          {t('keys.certificate')}
                        </button>
                      {/if}
                      {#if key.status === 'retired'}
                        <button type="button" class="secondary danger small" onclick={() => remove(key)}>
                          {t('common.delete')}
                        </button>
                      {/if}
                    </div>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      {/if}
    </section>
  {/each}

  {#if certificate}
    <section class="card" aria-labelledby="certificate-title">
      <h2 id="certificate-title">{t('keys.certificate.title', { keyId: certificate.keyId })}</h2>
      <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
      <!-- 横に溢れる長い行をキーボードでも読めるよう、フォーカスできるようにしてある -->
      <pre class="code" tabindex="0" aria-label={t('keys.certificate')}>{certificate.pem}</pre>
      <div class="actions">
        <CopyButton text={certificate.pem} subject={t('keys.certificate')} />
        <button
          type="button"
          class="secondary small"
          onclick={() => downloadText(`${certificate!.keyId}.pem`, certificate!.pem)}
        >
          <span class="material-icons" aria-hidden="true">download</span>
          {t('keys.certificate.download')}
        </button>
        <button type="button" class="secondary small" onclick={() => (certificate = null)}>
          {t('common.close')}
        </button>
      </div>
    </section>
  {/if}
{/if}
