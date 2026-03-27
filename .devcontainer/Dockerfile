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

# Install network tools + k6 + termshark
RUN apt-get update \
    && apt-get install -y --no-install-recommends iproute2 tshark \
    && apt-get clean && rm -rf /var/lib/apt/lists/*

# k6 — download binary directly (avoids GPG keyserver dependency)
RUN curl -L https://github.com/grafana/k6/releases/download/v0.55.0/k6-v0.55.0-linux-amd64.tar.gz \
    | tar xz --strip-components=1 -C /usr/local/bin k6-v0.55.0-linux-amd64/k6

# termshark — Wireshark-like TUI for terminal
RUN curl -L https://github.com/gcla/termshark/releases/download/v2.4.0/termshark_2.4.0_linux_x64.tar.gz \
    | tar xz --strip-components=1 -C /usr/local/bin termshark_2.4.0_linux_x64/termshark

WORKDIR /workspace

# Keep container alive
CMD ["sleep", "infinity"]
