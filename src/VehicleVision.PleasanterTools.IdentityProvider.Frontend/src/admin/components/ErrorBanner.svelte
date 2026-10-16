<script lang="ts">
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    /** 出す文言。無ければ何も出さない */
    message: string | null;
    /** あれば「再読み込み」を出す */
    onretry?: () => void;
  }

  let { message, onretry }: Props = $props();
</script>

{#if message}
  <div class="banner error" role="alert">
    <span class="material-icons" aria-hidden="true">error</span>
    <span>{message}</span>
    {#if onretry}
      <button type="button" class="link" onclick={onretry}>{t('common.retry')}</button>
    {/if}
  </div>
{/if}
