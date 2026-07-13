export type HealthPingResult = {
  url: string;
  ok: boolean;
  status: number | null;
  error: string | null;
};

type FetchLike = (input: string, init?: RequestInit) => Promise<Pick<Response, "ok" | "status">>;

export type ApiKeepAliveConfiguration = {
  enabled: boolean;
  intervalMinutes: number;
};

export function getApiKeepAliveConfiguration(
  environment: NodeJS.ProcessEnv = process.env,
): ApiKeepAliveConfiguration {
  const configuredEnabled = environment.ZALO_BRIDGE_API_KEEP_ALIVE;
  const enabled = configuredEnabled == null
    ? environment.NODE_ENV === "production"
    : configuredEnabled.toLowerCase() === "true";
  const configuredMinutes = Number(environment.ZALO_BRIDGE_API_KEEP_ALIVE_MINUTES ?? "8");
  const intervalMinutes = Number.isFinite(configuredMinutes)
    ? Math.min(14, Math.max(5, configuredMinutes))
    : 8;
  return { enabled, intervalMinutes };
}

export function apiHealthUrlsFromWebhooks(webhookUrls: Iterable<string>): string[] {
  const healthUrls = new Set<string>();

  for (const webhookUrl of webhookUrls) {
    try {
      const url = new URL(webhookUrl);
      if (url.protocol !== "https:" && url.protocol !== "http:") continue;
      url.pathname = "/health";
      url.search = "";
      url.hash = "";
      healthUrls.add(url.toString());
    } catch {
      // Ignore one invalid callback without stopping checks for other listeners.
    }
  }

  return [...healthUrls];
}

export async function pingApiHealthUrls(
  webhookUrls: Iterable<string>,
  fetchImpl: FetchLike = fetch,
): Promise<HealthPingResult[]> {
  return Promise.all(
    apiHealthUrlsFromWebhooks(webhookUrls).map(async (url): Promise<HealthPingResult> => {
      try {
        const response = await fetchImpl(url, {
          method: "GET",
          redirect: "follow",
          signal: AbortSignal.timeout(25_000),
        });
        return {
          url,
          ok: response.ok,
          status: response.status,
          error: response.ok ? null : `HTTP ${response.status}`,
        };
      } catch (error) {
        return {
          url,
          ok: false,
          status: null,
          error: error instanceof Error ? error.message : "Health request failed",
        };
      }
    }),
  );
}

export function startApiKeepAlive(
  getWebhookUrls: () => Iterable<string>,
  configuration = getApiKeepAliveConfiguration(),
): () => void {
  const { enabled, intervalMinutes } = configuration;
  if (!enabled) return () => undefined;
  let running = false;

  const run = async () => {
    if (running) return;
    running = true;
    try {
      const results = await pingApiHealthUrls(getWebhookUrls());
      for (const result of results) {
        if (result.ok) {
          console.log(`[Zalo bridge keep-alive] API healthy: ${result.url}`);
        } else {
          console.warn(`[Zalo bridge keep-alive] API unavailable: ${result.url} (${result.error})`);
        }
      }
    } finally {
      running = false;
    }
  };

  const timer = setInterval(() => void run(), intervalMinutes * 60_000);
  console.log(`[Zalo bridge keep-alive] Enabled every ${intervalMinutes} minute(s) for active listener APIs.`);
  return () => clearInterval(timer);
}
