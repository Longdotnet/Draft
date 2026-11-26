FROM ghcr.io/openclaw/openclaw:2026.10.34-slim@sha256:6217432664538c5abf22144681659b5e328d5bdc2749814bc6767701304a8ecc

COPY --chown=node:node render-probe.mjs owner-ui.mjs draft-text.mjs /app/
ENTRYPOINT ["tini", "-s", "--", "node", "/app/render-probe.mjs"]
CMD []
