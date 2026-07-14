FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY VolleyDraft.Api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-noto-core \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} dotnet VolleyDraft.Api.dll"]
