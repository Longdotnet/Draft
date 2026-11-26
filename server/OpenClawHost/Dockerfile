FROM node:22-bookworm-slim
RUN npm install -g --no-audit --no-fund @openai/codex@0.172.0 && npm cache clean --force
WORKDIR /app
COPY --chown=node:node codex-probe.mjs /app/
USER node
CMD ["node", "/app/codex-probe.mjs"]
