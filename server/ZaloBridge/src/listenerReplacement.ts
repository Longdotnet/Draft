type RetryControllableListener = {
  start(options?: { retryOnClose?: boolean }): void;
};

type RetryControllableCandidate = {
  api?: {
    listener?: RetryControllableListener;
  } | null;
};

const reconnectControlledListeners = new WeakSet<object>();

/**
 * zca-js can silently own websocket reconnects when retryOnClose is enabled. Those
 * reconnects bypass Draft's account-scoped lifecycle/recovery control plane and its
 * observability. Every listener prepared through this replacement boundary therefore
 * forces provider-managed retry off, even when an older caller still asks for it.
 *
 * Reconnect/recovery remains owned by the API-side ZaloListenerCoordinator: its sparse
 * listener reconcile creates a new listener generation and queues bounded missed-event
 * recovery, so a sleeping/restarted free-tier process can recover without a hidden
 * websocket reconnect storm.
 */
export function enforceBridgeOwnedListenerReconnect(candidate: unknown): void {
  if (!candidate || typeof candidate !== "object") return;
  const listener = (candidate as RetryControllableCandidate).api?.listener;
  if (!listener || typeof listener.start !== "function") return;
  if (reconnectControlledListeners.has(listener as object)) return;

  const providerStart = listener.start.bind(listener);
  listener.start = (options) => providerStart({
    ...options,
    retryOnClose: false,
  });
  reconnectControlledListeners.add(listener as object);
}

export async function prepareListenerReplacement<T>(
  prepare: () => Promise<T>,
  deactivateCurrent?: () => void,
): Promise<T> {
  const candidate = await prepare();
  deactivateCurrent?.();
  return candidate;
}

export async function activateListenerReplacement<T>(options: {
  prepare: () => Promise<T>;
  deactivateCurrent?: () => void | Promise<void>;
  activateCandidate: (candidate: T) => void | Promise<void>;
  reactivateCurrent?: () => void | Promise<void>;
}): Promise<T> {
  const candidate = await options.prepare();
  enforceBridgeOwnedListenerReconnect(candidate);
  await options.deactivateCurrent?.();

  try {
    await options.activateCandidate(candidate);
    return candidate;
  } catch (activationError) {
    try {
      await options.reactivateCurrent?.();
    } catch (rollbackError) {
      throw new AggregateError(
        [activationError, rollbackError],
        "Replacement listener activation failed and the previous listener could not be restored",
      );
    }
    throw activationError;
  }
}

export type ManualCloseFence = {
  pendingManualCloseEvents: number;
};

export function beginIntentionalManualClose(fence: ManualCloseFence): void {
  fence.pendingManualCloseEvents += 1;
}

export function cancelIntentionalManualClose(fence: ManualCloseFence): void {
  fence.pendingManualCloseEvents = Math.max(0, fence.pendingManualCloseEvents - 1);
}

export function consumeIntentionalManualClose(
  fence: ManualCloseFence,
  closeCode: number,
): boolean {
  if (closeCode !== 1000 || fence.pendingManualCloseEvents <= 0) return false;
  fence.pendingManualCloseEvents -= 1;
  return true;
}
