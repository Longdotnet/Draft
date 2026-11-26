FROM ghcr.io/openclaw/openclaw:2026.11.10

COPY --chown=node:node render-probe.mjs owner-ui.mjs /app/
ENTRYPOINT ["tini", "-s", "--", "node", "/app/render-probe.mjs"]
CMD []
