// **配色を先に決める**（ちらつき防止）。書体（Noto Sans CJK JP・Myrica M）は admin.html の `/css/app-fonts.css` で同梱している
// （DesignReference 第 2.2 版。複写は scripts/fonts/sync-app-fonts.mjs）。アイコン（Material Icons）もアプリに同梱している
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
