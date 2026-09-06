export type BridgeErrorSource = "bridge-validation" | "upstream-zalo" | "bridge-internal";

export type BridgeErrorDescriptor = {
  status: number;
  source: BridgeErrorSource;
  kind: string;
  retryable: boolean;
  publicMessage: string;
};

export class BridgeHttpError extends Error {
  constructor(
    public readonly status: number,
    public readonly source: BridgeErrorSource,
    public readonly kind: string,
    public readonly retryable: boolean,
    public readonly publicMessage: string,
  ) {
    super(publicMessage);
    this.name = "BridgeHttpError";
  }
}

function finiteHttpStatus(value: unknown): number | null {
  const status = typeof value === "number" ? value : Number(value);
  return Number.isInteger(status) && status >= 400 && status <= 599 ? status : null;
}

function readObject(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" ? value as Record<string, unknown> : null;
}

function upstreamStatus(error: unknown): number | null {
  const root = readObject(error);
  if (!root) return null;
  const response = readObject(root.response);
  return finiteHttpStatus(response?.status) ?? finiteHttpStatus(root.status) ?? finiteHttpStatus(root.statusCode);
}

function upstreamCode(error: unknown): string | null {
  const root = readObject(error);
  const response = readObject(root?.response);
  const data = readObject(response?.data);
  for (const candidate of [root?.code, data?.code, data?.errorCode]) {
    if (typeof candidate === "string" && /^[A-Za-z0-9_.-]{1,80}$/.test(candidate)) return candidate;
    if (typeof candidate === "number" && Number.isFinite(candidate)) return String(candidate);
  }
  return null;
}

export function classifyBridgeError(error: unknown): BridgeErrorDescriptor {
  if (error instanceof BridgeHttpError) {
    return {
      status: error.status,
      source: error.source,
      kind: error.kind,
      retryable: error.retryable,
      publicMessage: error.publicMessage,
    };
  }

  const status = upstreamStatus(error);
  if (status !== null) {
    if (status === 429) {
      return {
        status,
        source: "upstream-zalo",
        kind: "rate_limited",
        retryable: true,
        publicMessage: "Zalo upstream request was rate-limited.",
      };
    }
    if (status === 401 || status === 403) {
      return {
        status,
        source: "upstream-zalo",
        kind: "authentication",
        retryable: false,
        publicMessage: "Zalo upstream rejected the saved credentials.",
      };
    }
    if (status >= 500) {
      return {
        status: 502,
        source: "upstream-zalo",
        kind: "upstream_failure",
        retryable: true,
        publicMessage: "Zalo upstream request failed.",
      };
    }
    return {
      status,
      source: "upstream-zalo",
      kind: "upstream_rejected",
      retryable: status === 408,
      publicMessage: "Zalo upstream rejected the request.",
    };
  }

  return {
    status: 502,
    source: "bridge-internal",
    kind: "bridge_failure",
    retryable: true,
    publicMessage: "Zalo bridge request failed.",
  };
}

export function bridgeErrorLogFields(error: unknown, descriptor: BridgeErrorDescriptor) {
  return {
    source: descriptor.source,
    kind: descriptor.kind,
    status: descriptor.status,
    retryable: descriptor.retryable,
    upstreamCode: upstreamCode(error),
  };
}
