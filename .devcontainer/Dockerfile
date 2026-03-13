# Workspace container — .NET 8 SDK + Node 22 + vsdbg
# VS Code Dev Containers connects to this image to code both BE and FE
FROM mcr.microsoft.com/dotnet/sdk:8.0

# Install Node 22
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y nodejs \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Install vsdbg for .NET debugging
RUN apt-get update && apt-get install -y --no-install-recommends unzip curl \
    && curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /vsdbg \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# Install Angular CLI globally
RUN npm install -g @angular/cli@19

WORKDIR /workspace

# Keep container alive
CMD ["sleep", "infinity"]
