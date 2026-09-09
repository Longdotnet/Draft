import { createHash } from "node:crypto";
import type { ZaloCredentials } from "./contracts.js";
import {
  ZaloRateLimitGuard,
  type ZaloRateLimitEvent,
  type ZaloRateLimitGuardOptions,
} from "./zaloRateLimitGuard.js";

type OperationTelemetry = {
  providerAttempts: number;
  providerSuccesses: number;
  providerFailures: number;
  localCooldownRejects: number;
  pacingWaits: number;
  coalescedReads: number;
  lastProviderAttemptAtUnixMs: number | null;
  lastSuccessAtUnixMs: number | null;
  lastFailureAtUnixMs: number | null;
  lastFailureKind: string | null;
  lastStatus: number | null;
};

type ScopeTelemetry = {
  blockedUntilUnixMs: number;
  lastOperation: string | null;
  operations: Map<string, OperationTelemetry>;
};

export type ZaloProviderOperationDiagnostics = OperationTelemetry & {
  operation: string;
};

export type ZaloProviderAccountDiagnostics = {
  accountId: string;
  coolingDown: boolean;
  retryAfterSeconds: number | null;
  blockedUntilUnixMs: number | null;
  lastOperation: string | null;
  providerAttempts: number;
  providerSuccesses: number;
  providerFailures: number;
  localCooldownRejects: number;
  coalescedReads: number;
  lastProviderAttemptAtUnixMs: number | null;
  lastSuccessAtUnixMs: number | null;
  lastFailureAtUnixMs: number | null;
  lastFailureKind: string | null;
  operations: ZaloProviderOperationDiagnostics[];
};

export type ZaloProviderTrafficDiagnostics = {
  generatedAtUnixMs: number;
  aggregate: {
    trackedScopes: number;
    boundAccounts: number;
    activeCooldowns: number;
    providerAttempts: number;
    providerSuccesses: number;
    providerFailures: number;
    localCooldownRejects: number;
    pacingWaits: number;
    coalescedReads: number;
    lastProviderAttemptAtUnixMs: number | null;
    lastSuccessAtUnixMs: number | null;
    lastFailureAtUnixMs: number | null;
  };
  operations: ZaloProviderOperationDiagnostics[];
  accounts: ZaloProviderAccountDiagnostics[];
};

function emptyOperationTelemetry(): OperationTelemetry {
  return {
    providerAttempts: 0,
    providerSuccesses: 0,
    providerFailures: 0,
    localCooldownRejects: 0,
    pacingWaits: 0,
    coalescedReads: 0,
    lastProviderAttemptAtUnixMs: null,
    lastSuccessAtUnixMs: null,
    lastFailureAtUnixMs: null,
    lastFailureKind: null,
    lastStatus: null,
  };
}

function maxNullable(left: number | null, right: number | null): number | null {
  if (left === null) return right;
  if (right === null) return left;
  return Math.max(left, right);
}

function addTelemetry(target: OperationTelemetry, source: OperationTelemetry): void {
  target.providerAttempts += source.providerAttempts;
  target.providerSuccesses += source.providerSuccesses;
  target.providerFailures += source.providerFailures;
  target.localCooldownRejects += source.localCooldownRejects;
  target.pacingWaits += source.pacingWaits;
  target.coalescedReads += source.coalescedReads;
  target.lastProviderAttemptAtUnixMs = maxNullable(target.lastProviderAttemptAtUnixMs, source.lastProviderAttemptAtUnixMs);
  target.lastSuccessAtUnixMs = maxNullable(target.lastSuccessAtUnixMs, source.lastSuccessAtUnixMs);
  target.lastFailureAtUnixMs = maxNullable(target.lastFailureAtUnixMs, source.lastFailureAtUnixMs);
  if (source.lastFailureAtUnixMs !== null && source.lastFailureAtUnixMs === target.lastFailureAtUnixMs) {
    target.lastFailureKind = source.lastFailureKind;
    target.lastStatus = source.lastStatus;
  }
}

/**
 * One provider-traffic scope represents one connected account lifecycle. Read endpoints
 * receive credentials while outbound text sends receive only accountId, so the bridge
 * binds accountId -> credential scope when listener setup is requested. Once an account
 * is bound, later credential refreshes/rotations are aliased back to that stable scope.
 * This is safety-critical: changing cookies/IMEI/user-agent must not silently erase an
 * account-wide cooldown or pacing queue after an upstream 429.
 *
 * Diagnostics are deliberately process-local and bounded to counters/timestamps. Raw
 * credentials and credential fingerprints are never returned. Public health exposes
 * only aggregate counters; account identifiers are available only behind the bridge's
 * internal-key-protected diagnostics endpoint.
 */
export class ZaloProviderTrafficGovernor {
  private readonly rateGuard: ZaloRateLimitGuard;
  private readonly credentialScopeByAccount = new Map<string, string>();
  private readonly stableScopeByCredential = new Map<string, string>();
  private readonly inFlightReads = new Map<string, Promise<unknown>>();
  private readonly telemetryByScope = new Map<string, ScopeTelemetry>();
  private readonly now: () => number;

  constructor(options: ZaloRateLimitGuardOptions = {}) {
    this.now = options.now ?? Date.now;
    const externalObserver = options.onEvent;
    this.rateGuard = new ZaloRateLimitGuard({
      ...options,
      onEvent: (event) => {
        this.recordRateEvent(event);
        externalObserver?.(event);
      },
    });
  }

  bindAccount(accountIdValue: string, credentials: ZaloCredentials): string {
    const accountId = accountIdValue.trim();
    const rawCredentialScope = this.rawScopeForCredentials(credentials);
    const existingAccountScope = accountId ? this.credentialScopeByAccount.get(accountId) : undefined;
    const existingCredentialScope = this.stableScopeByCredential.get(rawCredentialScope);
    const stableScope = existingAccountScope ?? existingCredentialScope ?? rawCredentialScope;

    // Keep a connected account on one safety scope for its lifetime inside this bridge
    // process. Credential refreshes are transport/auth changes, not permission to bypass
    // an already-open provider cooldown or the per-account pacing queue.
    this.stableScopeByCredential.set(rawCredentialScope, stableScope);
    if (accountId) this.credentialScopeByAccount.set(accountId, stableScope);
    return stableScope;
  }

  runWithCredentials<T>(
    credentials: ZaloCredentials,
    operation: () => Promise<T>,
    operationName = "provider.call",
  ): Promise<T> {
    return this.rateGuard.run(this.scopeForCredentials(credentials), operation, operationName);
  }

  /**
   * Coalesces only requests that are simultaneously in flight and are known to be the
   * same authoritative provider read. Completed values are deliberately not cached:
   * a later caller always reaches Zalo again, so mutation-adjacent validation can never
   * be satisfied from stale process memory.
   */
  runReadWithCredentials<T>(
    credentials: ZaloCredentials,
    readKeyValue: string,
    operation: () => Promise<T>,
    operationName = "provider.read",
  ): Promise<T> {
    const scope = this.scopeForCredentials(credentials);
    const readKey = readKeyValue.trim();
    const normalizedOperation = operationName.trim() || "provider.read";
    if (!readKey) return this.rateGuard.run(scope, operation, normalizedOperation);

    const inFlightKey = `${scope}:${readKey}`;
    const existing = this.inFlightReads.get(inFlightKey);
    if (existing) {
      const telemetry = this.operationTelemetry(scope, normalizedOperation);
      telemetry.coalescedReads += 1;
      return existing as Promise<T>;
    }

    const result = this.rateGuard.run(scope, operation, normalizedOperation);
    this.inFlightReads.set(inFlightKey, result);
    void result.finally(() => {
      if (this.inFlightReads.get(inFlightKey) === result) {
        this.inFlightReads.delete(inFlightKey);
      }
    }).catch(() => undefined);
    return result;
  }

  runWithAccount<T>(
    accountIdValue: string,
    operation: () => Promise<T>,
    operationName = "provider.call",
  ): Promise<T> {
    const accountId = accountIdValue.trim();
    const scope = this.credentialScopeByAccount.get(accountId) ?? `account:${accountId}`;
    return this.rateGuard.run(scope, operation, operationName);
  }

  getHealthSnapshot() {
    const diagnostics = this.getDiagnostics();
    return {
      trackedScopes: diagnostics.aggregate.trackedScopes,
      boundAccounts: diagnostics.aggregate.boundAccounts,
      activeCooldowns: diagnostics.aggregate.activeCooldowns,
      providerAttempts: diagnostics.aggregate.providerAttempts,
      providerFailures: diagnostics.aggregate.providerFailures,
      localCooldownRejects: diagnostics.aggregate.localCooldownRejects,
      coalescedReads: diagnostics.aggregate.coalescedReads,
      lastProviderAttemptAtUnixMs: diagnostics.aggregate.lastProviderAttemptAtUnixMs,
      lastSuccessAtUnixMs: diagnostics.aggregate.lastSuccessAtUnixMs,
      lastFailureAtUnixMs: diagnostics.aggregate.lastFailureAtUnixMs,
    };
  }

  getDiagnostics(): ZaloProviderTrafficDiagnostics {
    const now = this.now();
    const aggregate = emptyOperationTelemetry();
    const operationAggregate = new Map<string, OperationTelemetry>();
    let activeCooldowns = 0;

    for (const scope of this.telemetryByScope.values()) {
      if (scope.blockedUntilUnixMs > now) activeCooldowns += 1;
      for (const [operation, telemetry] of scope.operations) {
        addTelemetry(aggregate, telemetry);
        const target = operationAggregate.get(operation) ?? emptyOperationTelemetry();
        addTelemetry(target, telemetry);
        operationAggregate.set(operation, target);
      }
    }

    const accounts = [...this.credentialScopeByAccount.entries()]
      .map(([accountId, scopeKey]) => this.accountDiagnostics(accountId, scopeKey, now))
      .sort((left, right) => left.accountId.localeCompare(right.accountId));

    return {
      generatedAtUnixMs: now,
      aggregate: {
        trackedScopes: this.telemetryByScope.size,
        boundAccounts: this.credentialScopeByAccount.size,
        activeCooldowns,
        providerAttempts: aggregate.providerAttempts,
        providerSuccesses: aggregate.providerSuccesses,
        providerFailures: aggregate.providerFailures,
        localCooldownRejects: aggregate.localCooldownRejects,
        pacingWaits: aggregate.pacingWaits,
        coalescedReads: aggregate.coalescedReads,
        lastProviderAttemptAtUnixMs: aggregate.lastProviderAttemptAtUnixMs,
        lastSuccessAtUnixMs: aggregate.lastSuccessAtUnixMs,
        lastFailureAtUnixMs: aggregate.lastFailureAtUnixMs,
      },
      operations: [...operationAggregate.entries()]
        .map(([operation, telemetry]) => ({ operation, ...telemetry }))
        .sort((left, right) => left.operation.localeCompare(right.operation)),
      accounts,
    };
  }

  private accountDiagnostics(accountId: string, scopeKey: string, now: number): ZaloProviderAccountDiagnostics {
    const scope = this.telemetryByScope.get(scopeKey);
    const total = emptyOperationTelemetry();
    const operations = scope
      ? [...scope.operations.entries()]
          .map(([operation, telemetry]) => {
            addTelemetry(total, telemetry);
            return { operation, ...telemetry };
          })
          .sort((left, right) => left.operation.localeCompare(right.operation))
      : [];
    const coolingDown = Boolean(scope && scope.blockedUntilUnixMs > now);
    const retryAfterSeconds = coolingDown && scope
      ? Math.max(1, Math.ceil((scope.blockedUntilUnixMs - now) / 1_000))
      : null;

    return {
      accountId,
      coolingDown,
      retryAfterSeconds,
      blockedUntilUnixMs: coolingDown && scope ? scope.blockedUntilUnixMs : null,
      lastOperation: scope?.lastOperation ?? null,
      providerAttempts: total.providerAttempts,
      providerSuccesses: total.providerSuccesses,
      providerFailures: total.providerFailures,
      localCooldownRejects: total.localCooldownRejects,
      coalescedReads: total.coalescedReads,
      lastProviderAttemptAtUnixMs: total.lastProviderAttemptAtUnixMs,
      lastSuccessAtUnixMs: total.lastSuccessAtUnixMs,
      lastFailureAtUnixMs: total.lastFailureAtUnixMs,
      lastFailureKind: total.lastFailureKind,
      operations,
    };
  }

  private recordRateEvent(event: ZaloRateLimitEvent): void {
    const scope = this.scopeTelemetry(event.scopeKey);
    scope.lastOperation = event.operation;
    if (event.blockedUntilUnixMs !== undefined) {
      scope.blockedUntilUnixMs = Math.max(scope.blockedUntilUnixMs, event.blockedUntilUnixMs);
    }
    const telemetry = this.operationTelemetry(event.scopeKey, event.operation);

    switch (event.type) {
      case "provider_attempt":
        telemetry.providerAttempts += 1;
        telemetry.lastProviderAttemptAtUnixMs = event.atUnixMs;
        break;
      case "provider_success":
        telemetry.providerSuccesses += 1;
        telemetry.lastSuccessAtUnixMs = event.atUnixMs;
        break;
      case "provider_failure":
        telemetry.providerFailures += 1;
        telemetry.lastFailureAtUnixMs = event.atUnixMs;
        telemetry.lastFailureKind = event.errorKind ?? null;
        telemetry.lastStatus = event.status ?? null;
        this.logTrafficControlEvent(event);
        break;
      case "cooldown_rejected":
        telemetry.localCooldownRejects += 1;
        this.logTrafficControlEvent(event);
        break;
      case "pacing_wait":
        telemetry.pacingWaits += 1;
        break;
    }
  }

  private logTrafficControlEvent(event: ZaloRateLimitEvent): void {
    if (event.type === "provider_failure" && event.errorKind !== "rate_limited") return;
    const accountIds = [...this.credentialScopeByAccount.entries()]
      .filter(([, scope]) => scope === event.scopeKey)
      .map(([accountId]) => accountId);
    console.warn("[Zalo bridge] provider traffic control", {
      event: event.type,
      operation: event.operation,
      providerTouched: event.providerTouched,
      accountIds,
      status: event.status ?? null,
      kind: event.errorKind ?? null,
      blockedUntilUnixMs: event.blockedUntilUnixMs ?? null,
      retryAfterSeconds: event.retryAfterSeconds ?? null,
    });
  }

  private operationTelemetry(scopeKey: string, operation: string): OperationTelemetry {
    const scope = this.scopeTelemetry(scopeKey);
    const existing = scope.operations.get(operation);
    if (existing) return existing;
    const created = emptyOperationTelemetry();
    scope.operations.set(operation, created);
    return created;
  }

  private scopeTelemetry(scopeKey: string): ScopeTelemetry {
    const existing = this.telemetryByScope.get(scopeKey);
    if (existing) return existing;
    const created: ScopeTelemetry = {
      blockedUntilUnixMs: 0,
      lastOperation: null,
      operations: new Map<string, OperationTelemetry>(),
    };
    this.telemetryByScope.set(scopeKey, created);
    return created;
  }

  private scopeForCredentials(credentials: ZaloCredentials): string {
    const rawScope = this.rawScopeForCredentials(credentials);
    return this.stableScopeByCredential.get(rawScope) ?? rawScope;
  }

  private rawScopeForCredentials(credentials: ZaloCredentials): string {
    // Never place raw credentials/cookies in a state key or log field.
    const digest = createHash("sha256")
      .update(JSON.stringify(credentials))
      .digest("hex");
    return `credentials:${digest}`;
  }
}

export const zaloProviderTrafficGovernor = new ZaloProviderTrafficGovernor();
