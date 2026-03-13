# ---- Build Stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY POC.AURA.Api/POC.AURA.Api.csproj POC.AURA.Api/
RUN dotnet restore POC.AURA.Api/POC.AURA.Api.csproj

COPY POC.AURA.Api/ POC.AURA.Api/
RUN dotnet publish POC.AURA.Api/POC.AURA.Api.csproj -c Release -o /app/publish

# ---- Runtime Stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "POC.AURA.Api.dll"]
