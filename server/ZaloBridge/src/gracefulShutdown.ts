export type ShutdownListener = {
  accountId: string;
};

export type ListenerStopFailure = {
  accountId: string;
  error: unknown;
};

export type GracefulShutdownResult = {
  drained: boolean;
  listenerStopFailures: ListenerStopFailure[];
  quiesceError: unknown | null;
  drainError: unknown | null;
};

type GracefulShutdownOptions = {
  listeners: readonly ShutdownListener[];
  runListenerLifecycle: (accountId: string, action: () => Promise<unknown>) => Promise<unknown>;
  stopListener: (accountId: string) => unknown | Promise<unknown>;
  quiesce: () => Promise<void>;
  drainWebhookDeliveries: (maxWaitMs: number) => Promise<boolean>;
  drainBudgetMs: number;
};

/**
 * Quiesces every listener independently and always attempts the Bridge→API webhook drain.
 *
 * Shutdown cleanup is deliberately best-effort across accounts: one broken listener must
 * not prevent other listeners from stopping or skip delivery of already accepted inbound
 * events. Failures are returned to the host so it can still exit non-zero after the drain
 * has had its chance to protect accepted work.
 */
export async function quiesceListenersAndDrain(
  options: GracefulShutdownOptions,
): Promise<GracefulShutdownResult> {
  const listenerStopFailures: ListenerStopFailure[] = [];

  for (const listener of options.listeners) {
    try {
      await options.runListenerLifecycle(
        listener.accountId,
        async () => options.stopListener(listener.accountId),
      );
    } catch (error) {
      listenerStopFailures.push({ accountId: listener.accountId, error });
    }
  }

  let quiesceError: unknown | null = null;
  if (options.listeners.length > 0) {
    try {
      await options.quiesce();
    } catch (error) {
      quiesceError = error;
    }
  }

  let drained = false;
  let drainError: unknown | null = null;
  try {
    drained = await options.drainWebhookDeliveries(options.drainBudgetMs);
  } catch (error) {
    drainError = error;
  }

  return {
    drained,
    listenerStopFailures,
    quiesceError,
    drainError,
  };
}
