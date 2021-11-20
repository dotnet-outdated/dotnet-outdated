FROM gitpod/workspace-full-vnc:latest

USER root
# Install .NET runtime dependencies and some dev tools
RUN apt-get update \
    && apt-get install -y \
        libc6 \
        libgcc1 \
        libgssapi-krb5-2 \
        libicu66 \
        libssl1.1 \
        libstdc++6 \
        zlib1g \
        git-gui \
    && rm -rf /var/lib/apt/lists/*

USER gitpod

ENV DOTNET_ROOT="/workspace/.dotnet"
ENV PATH=$PATH:$DOTNET_ROOT
ENV DOTNET_CLI_TELEMETRY_OPTOUT=true
