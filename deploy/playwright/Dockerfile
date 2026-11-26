# The official Playwright image holds the browsers but not the Playwright package, and the browser container has no
# route to the Internet (see compose.yaml: browser-net is internal), so npx could not fetch it at start. The package is
# installed here, at build time. Its version must match the Microsoft.Playwright NuGet package of the brain (major.minor).
# The base image is pinned by digest (manifest list); keep the tag for readers.
FROM mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f
RUN npm install -g --no-audit --no-fund playwright-core@1.64.1 && npm cache clean --force
USER pwuser
EXPOSE 3000
CMD ["playwright-core", "run-server", "--port", "3000", "--host", "0.0.0.0"]
