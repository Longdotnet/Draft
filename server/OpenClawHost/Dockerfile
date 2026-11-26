FROM ghcr.io/openclaw/openclaw:2026.11.10

COPY --chown=node:node render-probe.mjs /app/render-probe.mjs
ENTRYPOINT ["tini", "-s", "--", "node", "/app/render-probe.mjs"]
CMD []
