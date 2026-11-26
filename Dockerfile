FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SearxngResearchMcp.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release --no-restore -o /out && rm -rf /out/.playwright/node

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS dotnet
FROM searxng/searxng:2026.11.23-a7163d091@sha256:a7fc607aa6723f5a8982105bcc5ffba4d1b9fc7c6922a31a88d6e4cba49ef12a AS searxng
FROM yacy/yacy_search_server@sha256:4077e9d85ea13e3d2a29b63add60b109ccced191bebfc169d422dd50fca44c41 AS yacy

FROM mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f AS chromium
RUN npm install -g --no-audit --no-fund playwright-core@1.64.1 \
    && mkdir -p /chromium-bundle \
    && cp -a /ms-playwright/chromium_headless_shell-* /ms-playwright/ffmpeg-* /chromium-bundle/

FROM dotnet
COPY --from=chromium /usr/bin/node /usr/bin/node
COPY --from=chromium /usr/lib/node_modules/playwright-core /usr/lib/node_modules/playwright-core
COPY --from=chromium /chromium-bundle /ms-playwright
RUN apt-get update && apt-get install -y --no-install-recommends curl iptables libatomic1 tor \
    && node /usr/lib/node_modules/playwright-core/cli.js install-deps chromium \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -u 1001 -m -s /usr/sbin/nologin pwuser \
    && useradd -M -d /var/lib/research-mcp/yacy -s /usr/sbin/nologin yacy

COPY --from=searxng / /opt/searxng-root/
COPY --from=yacy /opt/yacy_search_server /opt/yacy_search_server
COPY --from=yacy /opt/java/openjdk /opt/java/openjdk
COPY deploy/searxng/settings.yml /opt/searxng-root/etc/searxng/settings.yml
COPY --chmod=0755 deploy/start.sh /usr/local/bin/start-research-mcp
WORKDIR /app
COPY --from=build /out/ .
ENV PATH="/usr/share/dotnet:/opt/java/openjdk/bin:${PATH}" \
    JAVA_HOME=/opt/java/openjdk \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PLAYWRIGHT_NODEJS_PATH=/usr/bin/node \
    RESEARCH_MCP_Transport=http \
    RESEARCH_MCP_HttpUrl=http://0.0.0.0:8765 \
    RESEARCH_MCP_Search__SearxngUrl=http://127.0.0.1:8080 \
    RESEARCH_MCP_Search__BrowserUrl=ws://127.0.0.1:3000/ \
    RESEARCH_MCP_Search__TorProxyUrl=socks5h://127.0.0.1:9050 \
    RESEARCH_MCP_OpenAI__BaseUrl=http://host.docker.internal:11434/v1
EXPOSE 8765
ENTRYPOINT ["/usr/local/bin/start-research-mcp"]
