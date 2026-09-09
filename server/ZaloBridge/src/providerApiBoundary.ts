export const GOVERNED_ZALO_READ_METHODS = new Set([
  "fetchAccountInfo",
  "getAllGroups",
  "getGroupInfo",
  "getGroupLinkDetail",
  "getGroupLinkInfo",
  "getListBoard",
  "getPollDetail",
  "getGroupMembersInfo",
  "getGroupChatHistory",
  "getAvatarUrlProfile",
  "getFullAvatar",
]);

export const GOVERNED_ZALO_STICKER_METHODS = new Set([
  "getStickers",
  "getStickersDetail",
  "sendSticker",
]);

type ProviderRunner = <T>(operation: string, action: () => Promise<T>) => Promise<T>;

/**
 * Wrap selected concrete zca-js methods at the provider-call boundary. Every selected
 * invocation acquires the account traffic queue independently, so pagination, batch
 * enrichment, semantic fallback, and the final sticker side effect cannot collapse into
 * one misleading logical attempt.
 *
 * Callers deliberately choose the method set. Listener lifecycle and ordinary text send
 * remain outside the read wrapper because their existing outer operation is already the
 * exact side-effect/lifecycle boundary; nesting the same account queue would deadlock.
 */
export function wrapProviderMethods<T extends object>(
  api: T,
  methods: ReadonlySet<string>,
  run: ProviderRunner,
): T {
  const wrappers = new Map<PropertyKey, unknown>();
  return new Proxy(api, {
    get(target, property, receiver) {
      const value = Reflect.get(target, property, receiver);
      if (
        typeof property !== "string" ||
        !methods.has(property) ||
        typeof value !== "function"
      ) {
        return value;
      }

      const existing = wrappers.get(property);
      if (existing) return existing;

      const wrapped = (...args: unknown[]) => run(
        `sdk.${property}`,
        () => Promise.resolve(Reflect.apply(value, target, args)),
      );
      wrappers.set(property, wrapped);
      return wrapped;
    },
  });
}

export function wrapProviderReadApi<T extends object>(api: T, run: ProviderRunner): T {
  return wrapProviderMethods(api, GOVERNED_ZALO_READ_METHODS, run);
}

export function wrapProviderStickerApi<T extends object>(api: T, run: ProviderRunner): T {
  return wrapProviderMethods(api, GOVERNED_ZALO_STICKER_METHODS, run);
}
