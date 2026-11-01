<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';
  import CopyButton from './CopyButton.svelte';

  interface Props {
    clientId: string;
    secret: string;
    /** 控えたことを確認して閉じる */
    ondone: () => void;
  }

  let { clientId, secret, ondone }: Props = $props();
</script>

<!-- **シークレットはこの 1 回だけ。** 閉じたら二度と見られないことを必ず伝える -->
<section class="banner warn secret" role="alert" aria-labelledby="secret-title">
  <span class="material-icons" aria-hidden="true">key</span>
  <div class="body">
    <strong id="secret-title">{t('secret.title', { clientId })}</strong>
    <p>{t('secret.warning')}</p>
    <p class="value">
      <code class="mono" data-testid="client-secret">{secret}</code>
      <CopyButton text={secret} subject={t('secret.subject')} />
    </p>
    <button type="button" onclick={ondone}>{t('secret.done')}</button>
  </div>
</section>

<style lang="scss">
  .body {
    flex: 1;
    min-width: 0;
  }

  p {
    margin: 0.4rem 0;
  }

  .value {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
  }

  code {
    padding: 0.2rem 0.5rem;
    border: 1px solid var(--color-border-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    color: var(--color-text);
    font-family: var(--font-code);
    user-select: all;
  }
</style>
