FROM node:22-alpine AS frontend-build
WORKDIR /frontend

COPY package.json package-lock.json ./
RUN npm ci

COPY index.html vite.config.ts tsconfig.json tsconfig.app.json tsconfig.node.json postcss.config.js tailwind.config.js ./
COPY public ./public
COPY src ./src
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS api-build
WORKDIR /src/server/VolleyDraft.Api

COPY server/VolleyDraft.Api/VolleyDraft.Api.csproj ./
RUN dotnet restore

COPY server/VolleyDraft.Api/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

COPY --from=frontend-build /frontend/dist /app/publish/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends fontconfig fonts-noto-core fonts-dejavu-core \
    && fc-cache -f \
    && fc-match "Noto Sans" | grep -qi "NotoSans" \
    && rm -rf /var/lib/apt/lists/*

COPY --from=api-build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} dotnet VolleyDraft.Api.dll"]
