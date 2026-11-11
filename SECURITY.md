# Security

## Reporting a vulnerability

Report it through GitHub's private vulnerability reporting: the repository's **Security** tab, then **Report a vulnerability**. Please don't open a public issue, a discussion or a pull request for it, and don't post it on Discord or Reddit. Say what you found, how to reproduce it, and which version or branch (and commit) it affects.

## Versions

| Version | Branch | Supported |
|---|---|---|
| InfoPanel 1.x, the current release | `1.4.x` | Yes |
| InfoPanel 2.0, in beta (pre-releases) | `2.0.x` | Yes |

## What matters most

InfoPanel 2.0's engine runs as the user, on the user's computer. Reports about these are the most useful:

- **The local server.** It listens on 127.0.0.1 only and checks the `Host` header and a request's `Origin`. A web page, another site or another origin that reaches the API, the live preview or the tokens, or a request that gets past those checks.
- **Tokens and pairing.** `/api` and `/ws` need a token: the session token (per start, never stored), or an integration token with its scopes. A token or pairing secret that leaks (into a log, an answer, a URL or another process), a scope that lets a token do more than it says, or a pairing that can be taken over.
- **The elevated launches on Windows.** The engine never elevates itself (started from an administrator prompt, it keeps those rights, and so does the sensor helper it starts); it starts only its own sensor helper's set-up (a task that runs the helper as SYSTEM) and the bundled PawnIO installer with administrator rights, each only when the session token asks. A way to make either run something else, or for another user to use the helper's task.
- **What the engine reads.** 1.x profiles and folders, `.infopanel` files, profile archives, images, videos, fonts, SVG, and web images and camera streams fetched by address: a file or stream that crashes the engine, writes outside its data folder, or reaches the network where it shouldn't.
- **The configured service.** Its credential sent anywhere but the service's own address, or written where it shouldn't be.

Not in scope: what a plugin does. Plugins are programs the user chose to run, and they run as the user ([plugins](docs/2.0/plugins.md)).

How the engine guards each of these: the security model in [`CLAUDE.md`](CLAUDE.md#security-model), and the engine API's [authentication](spec/docs/api.md#authentication).
