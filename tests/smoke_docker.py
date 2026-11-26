"""Smoke-test a running all-in-one MCP container, with an optional real LLM and Tor."""
import argparse
import json
import time
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint", default="http://127.0.0.1:8765/mcp")
    parser.add_argument("--research", action="store_true")
    parser.add_argument("--onion-url", help="A known public .onion page to read via the bundled Tor")
    opt = parser.parse_args()
    session = None
    protocol = "2025-11-25"
    message_id = 0

    def rpc(method, params, notification=False):
        nonlocal session, message_id
        message_id += 1
        body = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            body["id"] = message_id
        headers = {
            "Accept": "application/json, text/event-stream",
            "Content-Type": "application/json",
            "MCP-Protocol-Version": protocol,
        }
        if session:
            headers["Mcp-Session-Id"] = session
        with urlopen(Request(opt.endpoint, data=json.dumps(body).encode(), headers=headers),
                     timeout=720 if opt.research else 60) as reply:
            session = reply.headers.get("Mcp-Session-Id", session)
            if notification:
                return None
            if reply.headers.get_content_type() == "text/event-stream":
                for line in reply:
                    if line.startswith(b"data:"):
                        event = json.loads(line[5:])
                        if event.get("id") == message_id:
                            return event["result"]
                raise AssertionError("The event stream ended without the requested response")
            return json.load(reply)["result"]

    def call(tool, **arguments):
        return rpc("tools/call", {"name": tool, "arguments": arguments})

    def success(tool, **arguments):
        result = call(tool, **arguments)
        assert not result.get("isError"), result.get("content")
        return result["structuredContent"]

    for attempt in range(45):
        try:
            initialized = rpc("initialize", {"protocolVersion": protocol, "capabilities": {},
                                             "clientInfo": {"name": "docker-smoke", "version": "1.0"}})
            protocol = initialized["protocolVersion"]
            break
        except OSError:
            if attempt == 44:
                raise
            time.sleep(2)
    rpc("notifications/initialized", {}, notification=True)
    assert {tool["name"] for tool in rpc("tools/list", {})["tools"]} == {"web_search", "fetch_page", "research"}
    print("MCP HTTP ready", flush=True)

    search = success("web_search", query="IANA reserved domains", engines=["bing", "duckduckgo"])
    assert search["results"], search
    print(f"web_search: {len(search['results'])} results", flush=True)
    yacy = success("web_search", query="example", engines=["yacy"])
    assert not yacy["unresponsiveEngines"], yacy["unresponsiveEngines"]
    print(f"YaCy responded: {len(yacy['results'])} results", flush=True)
    page = success("fetch_page", url="https://www.iana.org/domains/reserved", render=True, format="text")
    assert page["rendered"] and "example" in page["markdown"].lower(), page
    assert call("fetch_page", url="http://127.0.0.1/").get("isError"), "Loopback must be blocked"
    print("Playwright rendered a public page; the page fetcher blocked loopback", flush=True)

    pdf = success("fetch_page", url="https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf")
    assert pdf["contentType"] == "application/pdf" and "Dummy PDF file" in pdf["markdown"], pdf
    print("fetch_page extracted text from a public PDF", flush=True)

    if opt.onion_url:
        onion = success("fetch_page", url=opt.onion_url)
        assert ".onion" in onion["finalUrl"] and onion["markdown"], onion
        print("fetch_page read a public .onion page through Tor", flush=True)
        ahmia = success("web_search", query="tor project", categories=["onion"], engines=["ahmia"])
        assert not ahmia["unresponsiveEngines"] and ahmia["results"], ahmia["unresponsiveEngines"]
        print(f"Ahmia via Tor: {len(ahmia['results'])} results", flush=True)

    if opt.research:
        report = success("research", question="What are IANA's reserved example domains used for?", mode="balanced")
        assert report["sources"] and any(source["cited"] for source in report["sources"]), report
        print(f"research: real LLM, {len(report['sources'])} sources, {len(report['answer'])} characters", flush=True)
        timings = report["timings"]
        assert "refine" in timings
        assert sum(value for step, value in timings.items() if step != "total") <= timings["total"], timings
        print(f"Research timings (ms): {timings}", flush=True)


if __name__ == "__main__":
    main()
