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
