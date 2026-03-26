# ── Build stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY POC.AURA.SmartHub/POC.AURA.SmartHub.csproj POC.AURA.SmartHub/
RUN dotnet restore POC.AURA.SmartHub/POC.AURA.SmartHub.csproj

COPY POC.AURA.SmartHub/ POC.AURA.SmartHub/
RUN dotnet publish POC.AURA.SmartHub/POC.AURA.SmartHub.csproj \
    -c Release -o /app/publish

# ── Runtime stage ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

ENTRYPOINT ["dotnet", "POC.AURA.SmartHub.dll"]
