FROM debian:trixie-slim
RUN apt-get update && apt-get install -y --no-install-recommends curl rclone systemd ca-certificates procps libstdc++6 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
