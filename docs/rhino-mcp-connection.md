# Connecting an AI client to the open Rhino 8 session

Investigation date: 2026-10-30. **No end-to-end connection has been verified.**

## Decision for Codex Web

The documented McNeel integration is for **Codex running locally**, not a
configuration for `chatgpt.com/codex`. There is no verified, officially documented
Codex Web → Rhino connection in the sources inspected for this investigation.
Do not treat the local-client example below as a working Web integration.

Codex Web runs tasks on a remote machine. `localhost` there means that remote
machine, not your Windows computer. A GitHub repository connection synchronizes
code; it does not connect the agent to your open Rhino document. A Windows
`.codex/config.toml` file cannot register a server with the Web service.

An authenticated remote MCP endpoint would need **both** a supported registration
method in the specific Codex Web product/account and a secure connection back to
Rhino. Neither side of that end-to-end route has been established here. Transport
support in Codex CLI's configuration schema does not prove Web support. ChatGPT
custom apps/developer mode and Codex Web are distinct integration surfaces; an
app working in an ordinary ChatGPT conversation is not proof it works in a Codex
Web task.

OpenAI's current hosted MCP/cloud documentation could not be retrieved in this
environment (HTTP 403). Consequently this investigation cannot certify a
universal claim that every current Web plan lacks remote MCP support. The
actionable conclusion is narrower: **there is no supported Web-only solution
verified for your setup, and no Web integration has been implemented.** If your
workspace offers a custom remote-MCP registration feature specifically for Codex
Web, verify its official documentation and administrator policy before designing
a bridge.

## What Rhino-MCP-Platform actually configures

McNeel's `mcneel/RhinoAI` repository identifies the package as
`Rhino-MCP-Platform`. The inspected `main` revision was
`8f625a2fd062ac18df5579195b2062e56bc7f7ef`. The default GitHub branch was
`rhino-9.x`; use the documentation and package appropriate to Rhino 8. The exact
package version on your Windows computer has not been inspected.

The inspected implementation has this local architecture:

```text
Local AI client on Windows
  → MCP over stdio → bundled rhino-mcp-router.exe
  → discovered local HTTP endpoint → Rhino plugin → Rhino document
```

- `MCPConnect`'s **Installed** state checks for a Rhino entry in the client config
  file. For Codex this is `%USERPROFILE%\.codex\config.toml`. It does not check
  whether `codex.exe` exists, authenticate Codex, or perform an MCP handshake.
- The router is bundled with the plugin. Its executable path depends on package
  version and platform; use the actual path provided by `MCPConnect`.
- The router exposes MCP over **stdio**, discovers Rhino's on-disk listener
  announcements, and routes to sessions/slots. Do not hard-code a session port.
- The plugin binds its HTTP server to **localhost**. The inspected implementation
  serves JSON-RPC POST requests at `/` and a filtered endpoint at `/agent`.
  Its source explicitly says it does not implement the Streamable-HTTP SSE
  channel. Do not assume a general remote client is compatible.
- The inspected listener setup has no remote OAuth/authentication gateway.
  Loopback binding is not a substitute for authentication if a relay is added.

| Connection method | Finding |
| --- | --- |
| Local Codex + bundled stdio router | Documented by McNeel; suitable alternative; not tested on your PC |
| Local clients such as Claude Desktop or an already approved MCP-aware editor | Documented alternatives; use their matching McNeel guides |
| Direct Codex Web → Windows localhost | Not a reachable route from a remote task |
| Codex Web → authenticated remote MCP → Rhino | No supported end-to-end route verified; no relay deployed |
| Copying a config into GitHub | Does not register or connect a Web MCP client |

## Simplest documented alternative

If keeping Codex is the priority, use **Codex CLI on the same Windows computer
as Rhino**, subject to IT approval. This adds one local AI client and uses the
router already supplied by the Rhino package. It needs no public Rhino endpoint,
custom bridge, Python-script transfer, or separately installed MCP server.

If installing any local client is prohibited and no compatible client is already
available, the requested workflow has no established supported route under the
current constraints. Do not install a tunnel to work around that restriction.

### Local connection instructions (not executed on your computer)

1. Save your current Rhino work. Keep the desired Rhino 8 document open.
2. If permitted, install the official Windows Codex CLI using the
   [OpenAI installation instructions](https://github.com/openai/codex#quickstart)
   or an IT-managed package. A Windows release binary avoids installing Node.js
   solely for this task. Respect corporate execution policies.
3. In Windows PowerShell, run `codex --version`, then `codex` and sign in using
   its supported ChatGPT sign-in flow. Do not share tokens or API keys in chat.
4. In Rhino, run `MCPConnect`. Preserve the existing Codex entry if it is already
   installed. Inspect its command path locally and confirm it points to the
   bundled Windows router. Do not post the entire config, which may contain
   unrelated sensitive settings.
5. Only if the entry is missing, use MCPConnect's installer or merge
   [`config/codex-rhino.example.toml`](../config/codex-rhino.example.toml) into
   `%USERPROFILE%\.codex\config.toml`. Replace its placeholder path with the
   MCPConnect-provided command. Do not overwrite the whole existing file or add
   a duplicate `[mcp_servers.rhino]` table. Follow your installed package's
   generated settings if they differ from this example.
6. Restart the local Codex session. Run `codex mcp list` to inspect the registered
   server. In an interactive session, `/mcp` can show MCP status and tools.
   A config listing alone is not evidence that Rhino is connected.
7. Perform the test below. If several Rhino sessions exist, identify the intended
   session/document before authorizing any modification. Avoid router operations
   that open a new slot when the goal is your already open session.

The repository's earlier `scripts/twisted_tower.py` uses interactive Rhino prompts
and was written for manual ScriptEditor execution. It is not an MCP bridge and
should not be used as an unattended MCP smoke test. A connected agent can instead
call Rhino tools to generate geometry directly.

## Connection and geometry test

First enter this natural-language prompt in the **local connected client**:

> Use the Rhino MCP tools to list the available Rhino sessions/documents. Identify
> my already open Rhino 8 document and report its name, units, and object count.
> Do not create, close, or modify documents. If the target is ambiguous, ask me.

Success requires a real MCP tool response matching the document on your screen.
If there is no tool response, stop: do not substitute generated Python text.

Then, in a saved test document, use this geometry test:

> In the identified open Rhino 8 document, create a 10 by 10 by 10 document-unit
> box at the world origin on a new uniquely named MCP_Connection_Test layer.
> Preserve existing objects. Query the resulting object and report its GUID,
> layer, bounding box, and whether it is a closed solid. Do not open a new document.

Verify the box appears in that exact Rhino window, the returned GUID identifies
it, the bounds are (0, 0, 0) to (10, 10, 10), and the query reports a closed solid.
Those observations, not an assistant's prose or a green config label, establish
an actual round trip. You can then ask it to remove only that returned test GUID.

Once this succeeds, a useful first tower prompt is:

> In this same Rhino document, create a 30-storey tower centered at the world
> origin, with floor height 3.6, width 24, depth 18, and rotation 3 degrees per
> storey, all lengths in document units. Make 30 occupied floor surfaces and a
> separate roof at height 108, rotated 90 degrees. Organize outlines, floors,
> roof, and facade panels into separate sublayers under a new parent layer.
> Preserve existing geometry. Use Rhino MCP tools to create and query the
> geometry, report counts and height, and tell me if any tool fails.

This creates a parameterized result; live Grasshopper sliders or persistent
editable parameters require an additional Grasshopper definition, not just baked
surfaces. Specify that separately if required.

## Remote-access boundary

No firewall changes, public listeners, tunnels, credentials, or scheduled code
execution were added. A future remote design would require an IT-approved
authenticated TLS MCP gateway, user/session authorization, constrained tools,
audit logging, and verified Codex Web registration and transport compatibility.
It must not expose an unauthenticated Rhino execution endpoint. There is no need
to take on those components for the documented same-computer workflow.

## Evidence and limits

Primary sources inspected:

- [McNeel Codex guide](https://github.com/mcneel/RhinoAI/blob/8f625a2fd062ac18df5579195b2062e56bc7f7ef/docs/content/docs/getting-started/codex.md): explicitly describes the terminal client and stdio router config.
- [Client installation state](https://github.com/mcneel/RhinoAI/blob/8f625a2fd062ac18df5579195b2062e56bc7f7ef/rhino/plugin/McpClientConfigInstaller.cs): checks the config entry, not live connectivity.
- [Router config generation](https://github.com/mcneel/RhinoAI/blob/8f625a2fd062ac18df5579195b2062e56bc7f7ef/rhino/plugin/RouterMcpConfig.cs) and [stdio server](https://github.com/mcneel/RhinoAI/blob/8f625a2fd062ac18df5579195b2062e56bc7f7ef/rhino/router/Program.cs).
- [Loopback listener](https://github.com/mcneel/RhinoAI/blob/8f625a2fd062ac18df5579195b2062e56bc7f7ef/rhino/plugin/McpServer.cs) and [HTTP implementation](https://github.com/mcneel/RhinoAI/blob/8f625a2fd062ac18df5579195b2062e56bc7f7ef/rhino/plugin/Server/McpEndpoint.cs).
- [OpenAI Codex README](https://github.com/openai/codex#readme): distinguishes local CLI from Codex Web.
- [OpenAI Codex configuration schema](https://github.com/openai/codex/blob/main/codex-rs/core/config.schema.json): includes local command and HTTP URL/auth fields; this is not a Web registration contract.

Verified here: primary-source inspection and TOML example parsing. Not verified:
your installed plugin version, Windows client installation, local authentication,
MCP handshake, session selection, or geometry creation. The example intentionally
contains a path placeholder and cannot connect until configured on Windows.
