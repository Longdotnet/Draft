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

type ProviderReadRunner = <T>(operation: string, action: () => Promise<T>) => Promise<T>;

/**
 * Wrap the concrete zca-js API at the provider-call boundary rather than at an HTTP
 * route boundary. Pagination, batch enrichment, and fallback reads can fan one logical
 * bridge request out into many upstream calls; each of those calls must therefore be
 * independently paced/counted and be able to open the shared account cooldown.
 *
 * Non-read members (listener lifecycle, getOwnId, sendMessage, etc.) intentionally pass
 * through untouched because their current outer operations already own side-effect and
 * lifecycle serialization. This keeps the boundary incremental and avoids nested
 * governor deadlocks while moving the high-amplification read paths to actual-call
 * accounting.
 */
export function wrapProviderReadApi<T extends object>(api: T, run: ProviderReadRunner): T {
  const wrappers = new Map<PropertyKey, unknown>();
  return new Proxy(api, {
    get(target, property, receiver) {
      const value = Reflect.get(target, property, receiver);
      if (
        typeof property !== "string" ||
        !GOVERNED_ZALO_READ_METHODS.has(property) ||
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
