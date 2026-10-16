// **書体とアイコンはアプリに同梱している。** 端末の書体に依存させない。
import '@fontsource-variable/noto-sans-jp';
import '@fontsource-variable/m-plus-1-code';
import '@fontsource/material-icons';
import './styles.scss';
import { mount } from 'svelte';
import AdminApp from './AdminApp.svelte';

const target = document.getElementById('admin');
if (target === null) {
  throw new Error('#admin が見つかりません');
}

export default mount(AdminApp, { target });
