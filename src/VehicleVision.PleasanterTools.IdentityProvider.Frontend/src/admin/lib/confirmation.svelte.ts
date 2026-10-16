export interface ConfirmationOptions {
  title: string;
  message: string;
  confirmLabel: string;
  danger?: boolean;
}

export interface ConfirmationRequest extends ConfirmationOptions {
  id: number;
}

interface FocusTarget {
  focus(): void;
}

interface ConfirmationKeyEvent {
  key: string;
  shiftKey: boolean;
  preventDefault(): void;
}

interface PendingConfirmation extends ConfirmationRequest {
  resolve(confirmed: boolean): void;
  returnFocusTo: FocusTarget | null;
}

let nextId = 0;
let pending = $state<PendingConfirmation | null>(null);

export function currentConfirmation(): ConfirmationRequest | null {
  return pending;
}

export function confirmAction(options: ConfirmationOptions): Promise<boolean> {
  if (pending !== null) {
    throw new Error('確認ダイアログは既に開いています。');
  }

  const returnFocusTo = activeFocusTarget();
  return new Promise<boolean>((resolve) => {
    pending = {
      ...options,
      danger: options.danger ?? false,
      id: ++nextId,
      resolve,
      returnFocusTo,
    };
  });
}

export function acceptConfirmation(): void {
  settle(true);
}

export function cancelConfirmation(): void {
  settle(false);
}

export function handleConfirmationKeydown(
  event: ConfirmationKeyEvent,
  cancelButton: FocusTarget | null,
  confirmButton: FocusTarget | null,
  activeElement: unknown,
): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    cancelConfirmation();
    return;
  }

  if (event.key !== 'Tab' || cancelButton === null || confirmButton === null) {
    return;
  }

  if (event.shiftKey && activeElement === cancelButton) {
    event.preventDefault();
    confirmButton.focus();
  } else if (!event.shiftKey && activeElement === confirmButton) {
    event.preventDefault();
    cancelButton.focus();
  }
}

function settle(confirmed: boolean): void {
  const request = pending;
  if (request === null) {
    return;
  }

  pending = null;
  queueMicrotask(() => request.returnFocusTo?.focus());
  request.resolve(confirmed);
}

function activeFocusTarget(): FocusTarget | null {
  if (typeof document === 'undefined') {
    return null;
  }

  const target = document.activeElement;
  if (target === null) {
    return null;
  }

  const focus = Reflect.get(target, 'focus');
  return typeof focus === 'function' ? { focus: () => focus.call(target) } : null;
}
