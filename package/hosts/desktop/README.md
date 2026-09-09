# Agience Desktop Relay Host

Status: **Draft**

This package implements the desktop relay host runtime.

Purpose:

- run a thin local Agience host, standalone from the full Origin / Mantle backend
- expose the first-party MCP persona servers from the source tree
- reserve a controller-facing runtime role (`authority` mode) for the full platform control plane

Mode naming:

- `host`: the desktop relay companion runtime. It provisions no database — no Postgres, no
  ArangoDB.
- `authority`: the controller/runtime mode for the full control plane and infrastructure; not
  implemented

The `host`/`authority` split is about operational control, not tenant or namespace boundaries.

Implemented:

- runtime configuration and mode parsing
- a source-tree MCP host app that mounts the persona servers directly from `agience-chorus/src/<persona>/`
- a desktop relay host entrypoint for local execution
- a `desktop-host` MCP surface with read-only filesystem tools, restricted to the roots in `allowed_roots`
- a preapproved local server supervisor for manual lifecycle management
- an optional outbound relay client loop for authority connection

Not implemented:

- browser relay parity
- authority runtime bootstrapping
- durable local MCP process pooling

## Test status

What is validated in-repo:

- desktop host config and relay runtime tests pass (`pytest tests/`)

`/relay/v1/connect` is implemented by `crystal.host`, the module Chorus mounts to serve its
personas (port 8082) — Mantle, at port 8081, does not implement it, and no service implements
`/relay/sessions/me`. The desktop host and its MCP surface run standalone; the end-to-end
authority connection walked through below is unvalidated.

Limits:

- desktop authentication bootstrap is manual
- local MCP proxying is request-by-request

## Usage

From this directory:

```bash
python -m agience_relay_host.main --mode host --bind-host 127.0.0.1 --bind-port 8082
```

Optional config file:

```bash
python -m agience_relay_host.main --config ./config.example.json
```

Relevant environment variables:

- `AGIENCE_RELAY_MODE=host|authority`
- `AGIENCE_AUTHORITY_URL=https://agience.example.com`
- `AGIENCE_RELAY_BIND_HOST=127.0.0.1`
- `AGIENCE_RELAY_BIND_PORT=8082`
- `AGIENCE_RELAY_ENABLED_PERSONAS=aria,sage,iris,astra,lumen,seraph,ophan`
- `AGIENCE_RELAY_DISPLAY_NAME=My Desktop Host`
- `AGIENCE_RELAY_DEVICE_ID=device-123`
- `AGIENCE_RELAY_ALLOWED_ROOTS=C:/work,C:/Users/<you>/Documents`
- `AGIENCE_RELAY_SERVICE_DEFINITIONS_DIR=./service-definitions`
- `AGIENCE_RELAY_ACCESS_TOKEN=<agience access token>`
- `AGIENCE_RELAY_CLIENT_VERSION=0.1.0`

Persona servers do not read the desktop host's `AGIENCE_RELAY_*` variables above. They depend on
their normal environment: **`MANTLE_URI`** (all seven, defaulting to `localhost:8081` if unset),
**`ORIGIN_URI`** (lumen, ophan, seraph), plus `MCP_HOST`/`MCP_PORT`/`MCP_TRANSPORT` and `LOG_LEVEL`. Chorus
servers do not read `PLATFORM_INTERNAL_SECRET` — persona-to-Origin auth is a JWT exchange. Each
persona's `.well-known/mcp.json` is generated from its `server.py` and is the authority.

If `authority_url` and `access_token` are configured, the desktop host will also attempt to maintain an outbound relay connection to `/relay/v1/connect`.

## Local install

From the repository root:

```powershell
Set-Location package/hosts/desktop
python -m pip install -e .
```

This installs the desktop relay package in editable mode so local code changes are picked up immediately.

## End-to-end local connection

### 1. Start the backend authority

Mantle listens on `localhost:8081`, the default `MANTLE_URI` these persona servers read.

From the `agience-mantle` checkout:

```powershell
Set-Location agience-mantle/src/mantle
python main.py
```

Expected result:

- mantle listens on `http://localhost:8081`
- startup completes without relay-related errors

### 2. Obtain a bearer token

Use a real Agience access token if you already have one from local sign-in.

For local development, you can mint a JWT directly:

```powershell
Set-Location agience-mantle/src/mantle
python -c "from services.auth_service import create_jwt_token; print(create_jwt_token({'sub':'YOUR_USER_ID','client_id':'desktop-host'}))"
```

Notes:

- replace `YOUR_USER_ID` with the Agience user id you want the desktop host session to represent
- `client_id` should stay `desktop-host`
- this token is sufficient for relay session setup and backend-side testing

### 3. Configure the desktop host

Edit `config.example.json` or copy it to a local config file and set:

- `mode`: `host`
- `authority_url`: `http://localhost:8081`
- `access_token`: the token from the previous step
- `allowed_roots`: the local directories you want desktop filesystem tools to access
- `enabled_personas`: whichever persona servers you want exposed locally

Minimal example:

```json
{
	"mode": "host",
	"authority_url": "http://localhost:8081",
	"access_token": "<paste token here>",
	"bind_host": "127.0.0.1",
	"bind_port": 8082,
	"allowed_roots": [
		"C:/Users/<you>/Documents"
	],
	"service_definitions_dir": "./service-definitions",
	"enabled_personas": ["aria", "iris"],
	"log_level": "INFO"
}
```

### 4. Start the desktop host

From `package/hosts/desktop`:

```powershell
python -m agience_relay_host.main --config .\config.example.json
```

Expected result:

- desktop host starts on `http://127.0.0.1:8082`
- if `authority_url` and `access_token` are valid, it attempts relay connection automatically

### 5. Verify desktop-side status

In a separate terminal:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:8082/relay/status"
```

Expected result after successful handshake:

- `configured: true`
- `connected: true`
- `session_id` populated

### 6. Verify authority-side session registration

In PowerShell:

```powershell
$headers = @{ Authorization = "Bearer <paste token here>" }
Invoke-RestMethod -Uri "http://localhost:8081/relay/sessions/me" -Headers $headers
```

Expected result:

- one active session for the current user
- `display_name`, `device_id`, and `capabilities_manifest` present after `client_hello`

### 6a. Optional one-command smoke test

Once the backend and desktop host are both running, you can verify both sides with a single command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\smoke-test.ps1 -Token "<paste token here>"
```

Optional parameters:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\smoke-test.ps1 `
	-Token "<paste token here>" `
	-AuthorityUrl "http://localhost:8081" `
	-DesktopHostUrl "http://127.0.0.1:8082" `
	-TimeoutSeconds 30
```

The script checks:

- desktop `/relay/status`
- authority `/relay/sessions/me`
- required built-in desktop-host tools
- absence of invalid `local-mcp:local-mcp:*` ids

### 7. Optional focused regression checks

Desktop host tests:

```powershell
Set-Location package/hosts/desktop
pytest tests/
```

## Optional local MCP server definitions

If you want the desktop host to advertise additional local MCP servers, add JSON definitions under `service-definitions/`.

Example:

```json
{
	"server_id": "sample",
	"label": "Sample Local Server",
	"command": ["python", "server.py"],
	"cwd": "C:/path/to/server"
}
```

Behavior:

- the desktop host advertises this server to the authority as `local-mcp:sample`
- backend routing understands the `local-mcp:` namespace
- each call is proxied as a one-shot subprocess request

## Built-in desktop-host tools

The host runtime mounts a dedicated MCP server at `/desktop-host/mcp` with:

- `host_status`
- `fs_list_dir`
- `fs_read_text`
- `mcp_servers_list_local`
- `mcp_servers_start_local`
- `mcp_servers_stop_local`

Filesystem tools are restricted to configured allowlisted roots.

Local server lifecycle operations only work for server definitions present in `service-definitions/`.

## Relay status

The host app exposes relay status at `/relay/status`.

## Troubleshooting

`/relay/status` shows `configured: false`

- `authority_url` or `access_token` is missing from config

`/relay/status` shows `configured: true` but `connected: false`

- backend is not running on the configured authority URL
- bearer token is invalid or does not verify against backend keys
- desktop host cannot reach the backend over HTTP/WebSocket

`/relay/sessions/me` returns an empty list

- the desktop host never completed `client_hello`
- you are querying the authority with a token for a different user than the desktop host session

the smoke-test script fails with no authority sessions

- the token passed to `smoke-test.ps1` does not belong to the same user represented by the desktop host session
- the desktop host failed before `client_hello` completed

backend starts but local MCP servers do not appear

- confirm JSON files exist under `service-definitions/`
- confirm each definition has a valid `server_id` and `command`
- local MCP servers are advertised as `local-mcp:<server_id>`

persona server mount fails on startup

- confirm the selected persona exists under `agience-chorus/src/<persona>/`
- confirm `MANTLE_URI` is set (and `ORIGIN_URI` for lumen, ophan, and seraph) — see the environment section above