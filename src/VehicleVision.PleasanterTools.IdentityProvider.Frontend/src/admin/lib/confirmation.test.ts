import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  acceptConfirmation,
  cancelConfirmation,
  confirmAction,
  currentConfirmation,
  handleConfirmationKeydown,
} from './confirmation.svelte';

const options = {
  title: '終了する',
  message: 'この端末のセッションを終了します。よろしいですか？',
  confirmLabel: '終了する',
  danger: true,
};

afterEach(() => {
  cancelConfirmation();
  vi.unstubAllGlobals();
});

describe('確認ダイアログ', () => {
  it('呼び出すと指定内容で開く', () => {
    void confirmAction(options);

    expect(currentConfirmation()).toMatchObject(options);
  });

  it('取消で false を返して閉じる', async () => {
    const result = confirmAction(options);

    cancelConfirmation();

    await expect(result).resolves.toBe(false);
    expect(currentConfirmation()).toBeNull();
  });

  it('決定で true を返して閉じる', async () => {
    const result = confirmAction(options);

    acceptConfirmation();

    await expect(result).resolves.toBe(true);
    expect(currentConfirmation()).toBeNull();
  });

  it('Esc と同じ取消処理で false を返す', async () => {
    const result = confirmAction(options);
    const preventDefault = vi.fn();

    handleConfirmationKeydown(
      { key: 'Escape', shiftKey: false, preventDefault },
      null,
      null,
      null,
    );

    await expect(result).resolves.toBe(false);
    expect(preventDefault).toHaveBeenCalledOnce();
  });

  it('Tab の移動をダイアログ内のボタンに閉じ込める', () => {
    const cancel = { focus: vi.fn() };
    const confirm = { focus: vi.fn() };
    const preventDefault = vi.fn();

    handleConfirmationKeydown(
      { key: 'Tab', shiftKey: false, preventDefault },
      cancel,
      confirm,
      confirm,
    );

    expect(preventDefault).toHaveBeenCalledOnce();
    expect(cancel.focus).toHaveBeenCalledOnce();
  });

  it('閉じると開く前に焦点があった要素へ戻す', async () => {
    const focus = vi.fn();
    vi.stubGlobal('document', { activeElement: { focus } });
    const result = confirmAction(options);

    cancelConfirmation();
    await result;
    await new Promise<void>((resolve) => queueMicrotask(resolve));

    expect(focus).toHaveBeenCalledOnce();
  });
});
