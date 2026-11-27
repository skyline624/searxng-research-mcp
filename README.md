# SearXNG Research MCP

A self-hosted **Model Context Protocol (MCP) server** for web search, page and PDF extraction, and multi-step research with numbered citations. A single Docker container runs the .NET 10 server, SearXNG, YaCy, Tor, and Playwright/Chromium. The `research` tool uses an OpenAI-compatible API, so you can connect a local LLM.

| MCP tool | What it does |
| --- | --- |
| `web_search` | Returns links, snippets, and unavailable search engines. Filter by language, category, engine, date range, or result page. The `onion` category searches Ahmia over Tor. |
| `fetch_page` | Extracts text or Markdown from public HTTP(S) pages, PDFs, and `.onion` pages. Playwright renders JavaScript pages on request or when the initial page contains too little text. Tor handles `.onion` requests through its local SOCKS5 proxy. |
| `research` | Plans search queries, reads sources, refines the search, ranks passages, and writes an answer with numbered citations. |

## Quick start

```sh
git clone https://github.com/skyline624/searxng-research-mcp.git
cd searxng-research-mcp
docker compose up -d --build
```

Add the server to your MCP client at **`http://127.0.0.1:8765/mcp`** using **Streamable HTTP**. Here is an example for clients that use `mcpServers` in their JSON configuration.

```json
{
  "mcpServers": {
    "searxng-research": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp"
    }
  }
}
```

Use `docker compose logs -f mcp` to follow startup. Docker publishes the MCP port on the loopback interface. Set `MCP_PORT` in `.env` to use a different host port. A Docker volume stores the YaCy index, Tor state, and the SearXNG secret generated on the first startup.

### Example tool calls

These are the arguments to pass from your MCP client.

| Tool | Example arguments |
| --- | --- |
| `web_search` | `{"query":"IANA reserved domains","max_results":5}` |
| `fetch_page` | `{"url":"https://www.iana.org/domains/reserved","format":"markdown"}` |
| `research` | `{"question":"Why does IANA reserve example domains?","mode":"balanced"}` |

## Configure the LLM for research

Configure an OpenAI-compatible `/v1/chat/completions` API to use `research`. You can use `web_search` and `fetch_page` as soon as the server starts.

Create a local `.env.llm` file, then run `docker compose up -d`. This example points to a model server running on the Docker Desktop host.

```dotenv
RESEARCH_MCP_OpenAI__BaseUrl=http://host.docker.internal:11434/v1
RESEARCH_MCP_OpenAI__Model=your-model-id
```

- Set `OPENAI_API_KEY` in `.env.llm` if your API requires a key.
- Set `RESEARCH_MCP_OpenAI__AnalysisModel` to choose a separate model for planning, ranking candidates, and refining queries. Set `RESEARCH_MCP_OpenAI__AnswerModel` to choose a separate model for writing the answer.
- Use `RESEARCH_MCP_OpenAI__TimeoutSeconds` to set the timeout per LLM call. `RESEARCH_MCP_OpenAI__TokenLimitField` accepts `max_tokens` or `max_completion_tokens`. Set `RESEARCH_MCP_OpenAI__UseJsonResponseFormat=true` to request structured JSON from compatible APIs.

For a privately signed or self-signed HTTPS endpoint, place its public PEM certificate in `certificates/` with a `.crt` extension and recreate the container. The certificate must cover the hostname or IP address in `BaseUrl`. The server sends excerpts from fetched pages and any supplied conversation history to the configured LLM.

The `speed`, `balanced`, and `quality` modes run up to one, two, and three research rounds, respectively. Each tool call waits for its result. For follow-up questions, pass previous turns in `history` with `question`, `answer`, and `sources`. Each previous source includes an `index`, `title`, and `url`.

## Search engines and Tor

The [bundled SearXNG configuration](deploy/searxng/settings.yml) covers general search, news, academic research, code, and discussions. It also enables YaCy and Ahmia. Pass engine names in the `engines` argument to focus `web_search`. YaCy uses its local index and peer-to-peer network by default. Ahmia runs over Tor, so its availability and response time depend on the Tor network and the remote service.

Docker Compose grants `NET_ADMIN` to install the firewall inside the container. Playwright runs under a dedicated user and makes outbound connections through the MCP server's filtering proxy, which checks for public addresses. The other services also run under dedicated users. The server loads Ahmia's blocklist before returning or fetching `.onion` URLs.

## Development over stdio

The .NET application also supports stdio. Copy `appsettings.example.json` to `appsettings.local.json`, configure the URLs of the services you want to use, and run the following command.

```sh
dotnet run -- --config ./appsettings.local.json
```

## Tests

```sh
dotnet test tests/SearxngResearchMcp.Tests.csproj
dotnet build
python tests/smoke_mcp.py
python tests/smoke_docker.py
```

Run the Docker smoke test against a running container. Add `--research` to test the configured LLM or `--onion-url` followed by a known public `.onion` address to test both Tor page fetching and Ahmia search. Allow Tor to establish its first circuit before running the Tor test.

[MIT License](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
