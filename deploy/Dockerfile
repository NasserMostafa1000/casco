# Build context: repository root (Casco/).  docker build -f deploy/Dockerfile .

FROM node:24-alpine AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/src/Casco.Api/Casco.Api.csproj backend/src/Casco.Api/
RUN dotnet restore backend/src/Casco.Api/Casco.Api.csproj
COPY backend/src/Casco.Api/ backend/src/Casco.Api/
COPY --from=web /src/frontend/dist/ backend/src/Casco.Api/wwwroot/
RUN dotnet publish backend/src/Casco.Api/Casco.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    App__DataPath=/app/data
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Casco.Api.dll"]
