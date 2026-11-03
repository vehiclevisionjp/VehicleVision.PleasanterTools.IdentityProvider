<script lang="ts">
  import { tick } from 'svelte';
  import {
    acceptConfirmation,
    cancelConfirmation,
    currentConfirmation,
    handleConfirmationKeydown,
  } from '../lib/confirmation.svelte';
  import { t } from '../lib/i18n/state.svelte';

  const titleId = 'confirmation-dialog-title';
  const descriptionId = 'confirmation-dialog-description';
  const request = $derived(currentConfirmation());

  let cancelButton = $state<HTMLButtonElement>();
  let confirmButton = $state<HTMLButtonElement>();

  $effect(() => {
    if (request !== null) {
      void tick().then(() => confirmButton?.focus());
    }
  });

  function handleBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      cancelConfirmation();
    }
  }

  function handleKeydown(event: KeyboardEvent): void {
    handleConfirmationKeydown(
      event,
      cancelButton ?? null,
      confirmButton ?? null,
      document.activeElement,
    );
  }
</script>

{#if request}
  <div
    class="backdrop"
    role="presentation"
    onclick={handleBackdropClick}
    onkeydown={handleKeydown}
  >
    <div
      class="dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
    >
      <h2 id={titleId}>{request.title}</h2>
      <p id={descriptionId}>{request.message}</p>
      <div class="actions">
        <button type="button" class="secondary" bind:this={cancelButton} onclick={cancelConfirmation}>
          {t('common.cancel')}
        </button>
        <button
          type="button"
          class:danger={request.danger}
          bind:this={confirmButton}
          onclick={acceptConfirmation}
        >
          {request.confirmLabel}
        </button>
      </div>
    </div>
  </div>
{/if}

<style lang="scss">
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 1000;
    display: grid;
    place-items: center;
    padding: var(--space-4);
    background: var(--overlay-bg);
  }

  .dialog {
    width: min(28rem, 100%);
    padding: var(--space-6);
    border: 1px solid var(--color-border-strong);
    border-radius: var(--radius-lg);
    background: var(--color-surface);
    box-shadow: var(--shadow-lg);
  }

  h2 {
    margin: 0;
    font-size: var(--fs-lg);
  }

  p {
    margin: 0.75rem 0 0;
    color: var(--color-text);
  }

  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    margin-top: 1.5rem;
  }

</style>
