// **配色を先に決める**（ちらつき防止）。書体は同梱しない（端末の書体 → OS の書体。DesignReference の決まり）。
// アイコン（Material Icons）はアプリに同梱している
import './lib/themeInit';
import '@fontsource/material-icons';
import './design/colors-explicit.css';
import './design/app-base.css';
import './styles.scss';
import { mount } from 'svelte';
import AdminApp from './AdminApp.svelte';

const target = document.getElementById('admin');
if (target === null) {
  throw new Error('#admin が見つかりません');
}

export default mount(AdminApp, { target });
