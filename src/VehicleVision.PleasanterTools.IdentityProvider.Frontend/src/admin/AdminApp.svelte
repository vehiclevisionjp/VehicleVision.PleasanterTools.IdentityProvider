<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { adminPath, basePath, loginUrl } from './lib/basePath';
  import { getMe, logout } from './lib/api';
  import {
    browserLanguages,
    LANGUAGE_NAMES,
    negotiateLanguage,
    readStoredLanguage,
    SUPPORTED_LANGUAGES,
    type Language,
  } from './lib/i18n/language';
  import type { MessageKey } from './lib/i18n/messages';
  import { initLanguage, language, setLanguage, t } from './lib/i18n/state.svelte';
  import { NAV_PAGES, navHash, navPageOf, parseRoute, type NavPage, type Route } from './lib/route';
  import {
    readThemePreference,
    systemPrefersDark,
    resolveTheme,
    saveThemePreference,
    THEME_PREFERENCES,
    type ThemePreference,
  } from './lib/theme';
  import type { Me } from './lib/types';
  import AuditLogList from './components/AuditLogList.svelte';
  import ClientEditor from './components/ClientEditor.svelte';
  import ClientList from './components/ClientList.svelte';
  import ConfirmationDialog from './components/ConfirmationDialog.svelte';
  import Dashboard from './components/Dashboard.svelte';
  import ErrorBanner from './components/ErrorBanner.svelte';
  import SamlEditor from './components/SamlEditor.svelte';
  import SamlList from './components/SamlList.svelte';
  import SessionList from './components/SessionList.svelte';
  import SigningKeys from './components/SigningKeys.svelte';
  import UserLookup from './components/UserLookup.svelte';

  initLanguage();

  let me = $state<Me>();
  let loading = $state(true);
  let error = $state<string | null>(null);
  let route = $state<Route>(parseRoute(location.hash));
  let themePreference = $state<ThemePreference>(readThemePreference());
  let systemDark = $state(systemPrefersDark());
  let main = $state<HTMLElement>();

  const navLabels: Record<NavPage, MessageKey> = {
    dashboard: 'nav.dashboard',
    clients: 'nav.clients',
    saml: 'nav.saml',
    keys: 'nav.keys',
    sessions: 'nav.sessions',
    audit: 'nav.audit',
    lookup: 'nav.lookup',
  };

  const themeLabels: Record<ThemePreference, MessageKey> = {
    light: 'theme.light',
    dark: 'theme.dark',
    auto: 'theme.system',
  };

  const currentNav = $derived(navPageOf(route));

  // **配色は `<html data-theme>` へ当てる。** OS の設定を見るのは「OS に合わせる」を選んだときだけ
  $effect(() => {
    document.documentElement.dataset.theme = resolveTheme(themePreference, systemDark);
  });

  // **タブの題名も言語に合わせる**
  $effect(() => {
    document.title = t('app.title');
  });

  async function loadMe(): Promise<void> {
    loading = true;
    error = null;
    const result = await getMe();
    if (result.ok) {
      me = result.value;
      // **画面で選んだ言語が無ければ、ログイン中の人の言語設定を使う**
      if (readStoredLanguage() === null) {
        setLanguage(negotiateLanguage(result.value.language, browserLanguages()), false);
      }
    } else if (result.status !== 401) {
      // 401 は api.ts がログイン画面へ遷移させる
      error = result.message;
    }
    loading = false;
  }

  onMount(() => {
    const query = window.matchMedia('(prefers-color-scheme: dark)');
    const onScheme = (event: MediaQueryListEvent) => (systemDark = event.matches);
    query.addEventListener('change', onScheme);

    // **ページを移したら本文へフォーカスを移す。** 読み上げ・キーボード利用者が迷わないため
    const onHash = () => {
      route = parseRoute(location.hash);
      void tick().then(() => main?.focus());
    };
    window.addEventListener('hashchange', onHash);

    void loadMe();

    return () => {
      query.removeEventListener('change', onScheme);
      window.removeEventListener('hashchange', onHash);
    };
  });

  function changeTheme(value: string): void {
    themePreference = value as ThemePreference;
    saveThemePreference(themePreference);
  }

  async function signOut(): Promise<void> {
    await logout();
    window.location.assign(loginUrl(adminPath, basePath));
  }
</script>

<a class="skip-link" href="#admin-main" onclick={(event) => { event.preventDefault(); main?.focus(); }}>
  {t('app.skipToContent')}
</a>

<div class="shell">
  {#if loading}
    <p class="status" role="status">{t('common.loading')}</p>
  {:else if !me}
    <div class="status">
      <ErrorBanner message={error ?? t('app.loadFailed')} onretry={loadMe} />
    </div>
  {:else}
    <header class="top">
      <span class="brand">{t('app.title')}</span>

      <div class="controls">
        <span class="who" title={me.loginId}>
          <span class="material-icons" aria-hidden="true">account_circle</span>
          {me.name}
        </span>

        <label class="select">
          <span class="visually-hidden">{t('app.language')}</span>
          <select value={language()} onchange={(event) => setLanguage(event.currentTarget.value as Language)}>
            {#each SUPPORTED_LANGUAGES as option (option)}
              <option value={option}>{LANGUAGE_NAMES[option]}</option>
            {/each}
          </select>
        </label>

        <label class="select">
          <span class="visually-hidden">{t('app.theme')}</span>
          <select value={themePreference} onchange={(event) => changeTheme(event.currentTarget.value)}>
            {#each THEME_PREFERENCES as option (option)}
              <option value={option}>{t(themeLabels[option])}</option>
            {/each}
          </select>
        </label>

        <button type="button" class="secondary small" onclick={signOut}>{t('app.signOut')}</button>
      </div>
    </header>

    <nav class="nav" aria-label={t('app.navigation')}>
      <ul>
        {#each NAV_PAGES as page (page)}
          <li>
            <a href={navHash(page)} aria-current={currentNav === page ? 'page' : undefined}>
              {t(navLabels[page])}
            </a>
          </li>
        {/each}
      </ul>
    </nav>

    <main id="admin-main" tabindex="-1" bind:this={main}>
      <!-- **編集画面は対象が変わったら作り直す。** 前の入力を引きずらない -->
      {#if route.page === 'dashboard'}
        <Dashboard />
      {:else if route.page === 'clients'}
        <ClientList />
      {:else if route.page === 'client'}
        {#key route.clientId}
          <ClientEditor clientId={route.clientId} />
        {/key}
      {:else if route.page === 'saml'}
        <SamlList />
      {:else if route.page === 'samlProvider'}
        {#key route.providerId}
          <SamlEditor providerId={route.providerId} />
        {/key}
      {:else if route.page === 'keys'}
        <SigningKeys />
      {:else if route.page === 'sessions'}
        <SessionList />
      {:else if route.page === 'audit'}
        <AuditLogList />
      {:else if route.page === 'lookup'}
        <UserLookup />
      {/if}
    </main>
  {/if}
</div>
<ConfirmationDialog />

<style lang="scss">
  // `.skip-link`・`.visually-hidden` は app-base.css
  .shell {
    min-height: 100vh;
  }

  // ヘッダーは鉄紺・白文字（ライト・ダークとも）
  .top {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--space-4);
    min-height: 56px;
    padding: var(--space-2) var(--space-6);
    border-bottom: 1px solid var(--color-primary-edge);
    background: var(--color-primary);
    color: var(--color-on-fill);

    // 鉄紺の上は白いリング
    :global(:focus-visible) {
      outline-color: var(--color-on-fill);
    }
  }

  .brand {
    font-family: var(--font-heading);
    font-weight: 700;
  }

  .controls {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--space-3);
    margin-left: auto;
  }

  .who {
    font-size: var(--fs-sm);
  }

  .select select {
    width: auto;
    min-height: 32px;
    padding: var(--space-1) var(--space-2);
    font-size: var(--fs-xs);
  }

  .nav {
    background: var(--color-surface);
    border-bottom: 1px solid var(--color-border);

    ul {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      margin: 0;
      padding: 0 var(--space-4);
      list-style: none;
    }

    a {
      display: block;
      margin-bottom: -1px;
      padding: var(--space-3) var(--space-4);
      border-bottom: 3px solid transparent;
      color: var(--color-text-muted);
      font-size: var(--fs-sm);
      text-decoration: none;

      &:hover {
        color: var(--color-text);
      }

      // 色だけに頼らず、下線の太さと文字の太さでも現在地を示す
      &[aria-current='page'] {
        border-bottom-color: var(--color-primary-text);
        color: var(--color-primary-text);
        font-weight: 700;
      }
    }
  }

  main {
    max-width: 80rem;
    margin: 0 auto;
    padding: var(--space-6) var(--space-6) var(--space-12);

    &:focus {
      outline: none;
    }
  }

  .status {
    max-width: 40rem;
    margin: var(--space-12) auto;
    padding: 0 var(--space-4);
    color: var(--color-text-muted);
    text-align: center;
  }
</style>
