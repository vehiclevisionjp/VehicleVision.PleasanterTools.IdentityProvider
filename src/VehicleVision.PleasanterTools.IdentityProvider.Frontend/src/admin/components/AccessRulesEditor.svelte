<script lang="ts">
  import { searchDirectory } from '../lib/api';
  import { t } from '../lib/i18n/state.svelte';
  import {
    ACCESS_KINDS,
    type AccessEffect,
    type AccessKind,
    type AccessRule,
    type DirectoryEntry,
  } from '../lib/types';

  interface Props {
    rules: AccessRule[];
    /** 入力の検証エラー（翻訳済み） */
    error?: string | null;
    /** 他の入力欄と区別する接頭辞（同じ画面に 1 つだけなら省いてよい） */
    idPrefix?: string;
  }

  let { rules = $bindable(), error = null, idPrefix = 'rules' }: Props = $props();

  let effect = $state<AccessEffect>('include');
  let kind = $state<AccessKind>('group');
  let query = $state('');
  let candidates = $state<DirectoryEntry[]>([]);
  let searched = $state(false);
  let searching = $state(false);
  let searchError = $state<string | null>(null);
  let manualId = $state('');

  const KIND_KEYS = {
    user: 'accessRules.kind.user',
    dept: 'accessRules.kind.dept',
    group: 'accessRules.kind.group',
  } as const;

  const EFFECT_KEYS = {
    include: 'accessRules.effect.include',
    exclude: 'accessRules.effect.exclude',
  } as const;

  function isAdded(entryKind: AccessKind, id: number): boolean {
    return rules.some((rule) => rule.kind === entryKind && rule.targetId === id && rule.effect === effect);
  }

  function add(entryKind: AccessKind, id: number, label: string | null): void {
    if (isAdded(entryKind, id)) return;
    rules = [...rules, { effect, kind: entryKind, targetId: id, label }];
  }

  function remove(index: number): void {
    rules = rules.filter((_, i) => i !== index);
  }

  async function search(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    searching = true;
    searchError = null;
    const result = await searchDirectory(kind, query.trim());
    if (result.ok) {
      candidates = result.value;
    } else {
      candidates = [];
      searchError = result.message;
    }
    searched = true;
    searching = false;
  }

  function addManual(): void {
    const id = Number(manualId.trim());
    if (!Number.isInteger(id) || id <= 0) return;
    add(kind, id, null);
    manualId = '';
  }
</script>

<fieldset class="field access-rules" aria-describedby={`${idPrefix}-hint`}>
  <legend class="label">{t('accessRules.title')}</legend>
  <span class="hint" id={`${idPrefix}-hint`}>{t('accessRules.hint')}</span>

  {#if rules.length === 0}
    <p class="muted">{t('accessRules.empty')}</p>
  {:else}
    <ul class="rule-list">
      {#each rules as rule, index (`${rule.effect}:${rule.kind}:${rule.targetId}`)}
        <li>
          <span class="badge" class:ok={rule.effect === 'include'} class:error={rule.effect === 'exclude'}>
            {rule.effect === 'include' ? '✓' : '✕'}
            {t(EFFECT_KEYS[rule.effect])}
          </span>
          <span>{t(KIND_KEYS[rule.kind])}</span>
          {#if rule.label}<strong>{rule.label}</strong>{/if}
          <code class="mono">#{rule.targetId}</code>
          <button type="button" class="link" onclick={() => remove(index)}>{t('accessRules.remove')}</button>
        </li>
      {/each}
    </ul>
  {/if}

  <form class="rule-add" onsubmit={search}>
    <label for={`${idPrefix}-effect`} class="visually-hidden">{t('accessRules.effect')}</label>
    <select id={`${idPrefix}-effect`} bind:value={effect}>
      <option value="include">{t('accessRules.effect.include')}</option>
      <option value="exclude">{t('accessRules.effect.exclude')}</option>
    </select>
    <label for={`${idPrefix}-kind`} class="visually-hidden">{t('accessRules.kind')}</label>
    <select id={`${idPrefix}-kind`} bind:value={kind}>
      {#each ACCESS_KINDS as option (option)}
        <option value={option}>{t(KIND_KEYS[option])}</option>
      {/each}
    </select>
    <label for={`${idPrefix}-query`} class="visually-hidden">{t('accessRules.query')}</label>
    <input
      id={`${idPrefix}-query`}
      type="search"
      bind:value={query}
      placeholder={t('accessRules.query')}
      autocomplete="off"
    />
    <button type="submit" disabled={searching}>{t('accessRules.search')}</button>
  </form>

  {#if searchError}<span class="error-text" role="alert">{searchError}</span>{/if}
  {#if searched && !searching && !searchError}
    {#if candidates.length === 0}
      <p class="muted">{t('accessRules.noResults')}</p>
    {:else}
      <ul class="rule-list">
        {#each candidates as entry (entry.id)}
          <li>
            <strong>{entry.name}</strong>
            {#if entry.detail}<code class="mono">{entry.detail}</code>{/if}
            <code class="mono">#{entry.id}</code>
            <button type="button" disabled={isAdded(kind, entry.id)} onclick={() => add(kind, entry.id, entry.name)}>
              {t('accessRules.add')}
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  {/if}

  <div class="rule-manual">
    <label for={`${idPrefix}-manual`} class="hint">{t('accessRules.manual')}</label>
    <input id={`${idPrefix}-manual`} type="text" inputmode="numeric" bind:value={manualId} autocomplete="off" />
    <button type="button" onclick={addManual}>{t('accessRules.add')}</button>
  </div>

  {#if error}<span class="error-text" role="alert">{error}</span>{/if}
</fieldset>
