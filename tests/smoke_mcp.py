"""Exercise the actual stdio server using an OpenAI-compatible fixture.

Build first, then run python tests/smoke_mcp.py. Set SEARXNG_SMOKE_URL to
also exercise a running SearXNG instance. This test reads a public IANA page.
"""
import http.server
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
from urllib.parse import parse_qs, urlsplit
from urllib.request import urlopen


URL = "https://www.iana.org/domains/reserved"


class Fixture(http.server.BaseHTTPRequestHandler):
    live_url = None
    calls = []

    def reply(self, value):
        body = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass

    def do_GET(self):
        url = urlsplit(self.path)
        assert url.path == "/search", self.path
        query = parse_qs(url.query)
        self.calls.append(query)
        if self.live_url and query["q"] == ["IANA reserved domains"]:
            with urlopen(self.live_url.rstrip("/") + self.path, timeout=20) as response:
                self.reply(json.load(response))
            return
        self.reply({
            "results": [
                {"url": URL, "title": "IANA Reserved Domains", "content": "Example domains", "engine": "bing"},
                {"url": "http://127.0.0.1/private", "title": "Private fixture", "engine": "bing"},
            ],
            "unresponsive_engines": [["offline", "timeout"]],
            "number_of_results": 2,
        })

    def do_POST(self):
        assert self.path == "/v1/chat/completions", self.path
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.calls.append(request)
        assert self.headers["Authorization"] == "Bearer fixture-only"
        assert request["max_completion_tokens"] > 0
        prompt = request["messages"][0]["content"]
        if "plan a web search" in prompt:
            answer = '{"language":"en","queries":["IANA example domains"]}'
        elif "refine a web search" in prompt:
            answer = '{"queries":[]}'
        else:
            assert prompt.startswith("You answer"), prompt
            assert 'url="' + URL + '"' in request["messages"][-1]["content"]
            answer = "IANA maintains example domains for documentation [1, 99]."
        self.reply({"choices": [{"message": {"content": answer}}]})


def main():
    root = Path(__file__).resolve().parents[1]
    assembly = root / "bin/Debug/net10.0/SearxngResearchMcp.dll"
    with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Fixture) as fixture, tempfile.TemporaryDirectory() as tmp:
        threading.Thread(target=fixture.serve_forever, daemon=True).start()
        local_url = f"http://127.0.0.1:{fixture.server_port}"
        live_url = Fixture.live_url = os.environ.get("SEARXNG_SMOKE_URL")
        config = Path(tmp) / "config.json"
        config.write_text(json.dumps({
            "Search": {"SearxngUrl": local_url},
            "OpenAI": {
                "BaseUrl": local_url + "/v1",
                "AnalysisModel": "planner-fixture",
                "AnswerModel": "writer-fixture",
                "ApiKeyEnv": "MCP_SMOKE_LLM_KEY",
                "TokenLimitField": "max_completion_tokens",
                "UseJsonResponseFormat": True,
            },
        }), encoding="utf-8")
        env = {**os.environ, "MCP_SMOKE_LLM_KEY": "fixture-only"}
        with subprocess.Popen(["dotnet", str(assembly), "--config", str(config)],
                              cwd=tmp, env=env, stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, text=True, encoding="utf-8") as server:
            lines = queue.Queue()

            def read_stdout():
                for line in server.stdout:
                    lines.put(line)
                lines.put(None)

            threading.Thread(target=read_stdout, daemon=True).start()

            def send(method, params=None, request_id=None):
                message = {"jsonrpc": "2.0", "method": method}
                if params is not None:
                    message["params"] = params
                if request_id is not None:
                    message["id"] = request_id
                server.stdin.write(json.dumps(message) + "\n")
                server.stdin.flush()
                if request_id is None:
                    return None
                while True:
                    line = lines.get(timeout=90)
                    assert line is not None, "MCP exited before replying"
                    received = json.loads(line)
                    if received.get("id") == request_id:
                        assert "result" in received, received
                        return received["result"]

            try:
                send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                    "clientInfo": {"name": "smoke", "version": "1"}}, 1)
                send("notifications/initialized")
                tools = send("tools/list", {}, 2)["tools"]
                assert {tool["name"] for tool in tools} == {"web_search", "fetch_page", "research"}, tools
                web = send("tools/call", {"name": "web_search", "arguments": {
                    "query": "IANA reserved domains", "language": "en", "categories": ["web"], "engines": ["bing", "duckduckgo"],
                }}, 3)
                assert not web.get("isError"), web
                result = web["structuredContent"]
                assert result["results"], result
                if not live_url:
                    assert result["unresponsiveEngines"] == ["offline: timeout"], result
                    assert Fixture.calls[0]["engines"] == ["bing,duckduckgo"], Fixture.calls
                    assert Fixture.calls[0]["categories"] == ["general"], Fixture.calls

                research = send("tools/call", {"name": "research", "arguments": {
                    "question": "What domains does IANA reserve for documentation?", "mode": "balanced", "sources": ["web"],
                }}, 4)
                assert not research.get("isError"), research
                result = research["structuredContent"]
                assert "[1]" in result["answer"] and "99" not in result["answer"], result
                assert result["sources"][0]["cited"], result
                calls = [call for call in Fixture.calls if "model" in call]
                assert calls[0]["model"] == "planner-fixture", calls
                assert any("refine a web search" in call["messages"][0]["content"] for call in calls), calls
                assert calls[-1]["model"] == "writer-fixture", calls
                assert calls[0]["response_format"] == {"type": "json_object"}
                assert "response_format" not in calls[-1]
                if not live_url:
                    assert any("127.0.0.1" in warning for warning in result["warnings"]), result
                bad = send("tools/call", {"name": "web_search", "arguments": {
                    "query": "test", "categories": ["unknown"],
                }}, 5)
                assert bad.get("isError"), bad
                page = send("tools/call", {"name": "fetch_page", "arguments": {
                    "url": URL, "format": "text", "max_chars": 1000, "render": False,
                }}, 6)
                assert not page.get("isError"), page
                assert "example" in page["structuredContent"]["markdown"].lower(), page
                private = send("tools/call", {"name": "fetch_page", "arguments": {
                    "url": "http://127.0.0.1/",
                }}, 7)
                assert private.get("isError"), private
                print("MCP stdio, web_search, research, OpenAI stages and citation check passed.")
            finally:
                server.stdin.close()
                try:
                    server.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    server.kill()
        fixture.shutdown()


if __name__ == "__main__":
    main()
