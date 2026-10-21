/** クリップボードへ書く。**できなければ `false`**（非 HTTPS・権限なしなど）。 */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/** 文字列をファイルとして保存させる（証明書 PEM など）。 */
export function downloadText(fileName: string, text: string, type = 'application/x-pem-file'): void {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
