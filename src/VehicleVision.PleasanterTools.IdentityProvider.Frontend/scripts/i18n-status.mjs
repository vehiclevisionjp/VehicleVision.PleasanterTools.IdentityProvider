import { en } from '../src/admin/lib/i18n/messages/en.ts';
import { ja } from '../src/admin/lib/i18n/messages/ja.ts';

const catalogs = { ja, en };
const total = Object.keys(ja).length;

console.log('Administration screen');
for (const [language, catalog] of Object.entries(catalogs)) {
  console.log(`  ${language}: ${Object.keys(catalog).length} / ${total}`);
}
