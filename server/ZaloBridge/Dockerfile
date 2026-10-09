FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY tsconfig.json ./
COPY src ./src
COPY scripts ./scripts
RUN npm run build
# Explicit opt-in for read-only NLU model packaging; default build never downloads weights.
ARG ZALO_BRIDGE_NLU_PREPARE=false
RUN if [ "$ZALO_BRIDGE_NLU_PREPARE" = "true" ]; then npm run nlu:prepare; else mkdir -p dist/nlu-model; fi

FROM node:22-bookworm-slim AS runtime
ENV NODE_ENV=production
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev && npm cache clean --force
COPY --from=build /app/dist/src ./dist
COPY --from=build /app/dist/nlu-model ./dist/nlu-model
USER node
EXPOSE 3000
CMD ["node", "dist/server.js"]
