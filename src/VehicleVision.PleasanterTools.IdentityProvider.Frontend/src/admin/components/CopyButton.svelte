<script lang="ts">
  import { copyText } from '../lib/clipboard';
  import { t } from '../lib/i18n/state.svelte';

  interface Props {
    /** コピーする文字列 */
    text: string;
    /** 何をコピーするか（読み上げ用。「○○をコピー」） */
    subject: string;
  }

  let { text, subject }: Props = $props();

  let state = $state<'idle' | 'copied' | 'failed'>('idle');
  let timer: ReturnType<typeof setTimeout> | undefined;

  async function copy(): Promise<void> {
    state = (await copyText(text)) ? 'copied' : 'failed';
    clearTimeout(timer);
    timer = setTimeout(() => (state = 'idle'), 3000);
  }
</script>

<button type="button" class="secondary small" onclick={copy} aria-label={t('common.copySubject', { subject })}>
  <span class="material-icons" aria-hidden="true">content_copy</span>
  {t('common.copy')}
</button>
<!-- **結果は読み上げにも伝える。** -->
<span class="status" role="status">
  {#if state === 'copied'}{t('common.copied')}{:else if state === 'failed'}{t('common.copyFailed')}{/if}
</span>

<style lang="scss">
  .status {
    margin-left: 0.4rem;
    color: var(--muted);
    font-size: 0.85rem;
  }
</style>
