import { createHash } from "node:crypto";
import type { ZaloCredentials } from "./contracts.js";
import { ZaloRateLimitGuard, type ZaloRateLimitGuardOptions } from "./zaloRateLimitGuard.js";

/**
 * One provider-traffic scope represents one connected credential set. Read endpoints
 * receive credentials while outbound text sends receive only accountId, so the bridge
 * binds accountId -> credential scope when listener setup is requested. This lets the
 * same cooldown/pacing state cover both directions without logging or exposing cookies.
 */
export class ZaloProviderTrafficGovernor {
  private readonly rateGuard: ZaloRateLimitGuard;
  private readonly credentialScopeByAccount = new Map<string, string>();

  constructor(options: ZaloRateLimitGuardOptions = {}) {
    this.rateGuard = new ZaloRateLimitGuard(options);
  }

  bindAccount(accountIdValue: string, credentials: ZaloCredentials): string {
    const accountId = accountIdValue.trim();
    const scope = this.scopeForCredentials(credentials);
    if (accountId) this.credentialScopeByAccount.set(accountId, scope);
    return scope;
  }

  runWithCredentials<T>(credentials: ZaloCredentials, operation: () => Promise<T>): Promise<T> {
    return this.rateGuard.run(this.scopeForCredentials(credentials), operation);
  }

  runWithAccount<T>(accountIdValue: string, operation: () => Promise<T>): Promise<T> {
    const accountId = accountIdValue.trim();
    const scope = this.credentialScopeByAccount.get(accountId) ?? `account:${accountId}`;
    return this.rateGuard.run(scope, operation);
  }

  private scopeForCredentials(credentials: ZaloCredentials): string {
    // Never place raw credentials/cookies in a state key or log field.
    const digest = createHash("sha256")
      .update(JSON.stringify(credentials))
      .digest("hex");
    return `credentials:${digest}`;
  }
}

export const zaloProviderTrafficGovernor = new ZaloProviderTrafficGovernor();
