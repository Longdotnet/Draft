export async function prepareListenerReplacement<T>(
  prepare: () => Promise<T>,
  deactivateCurrent?: () => void,
): Promise<T> {
  const candidate = await prepare();
  deactivateCurrent?.();
  return candidate;
}
