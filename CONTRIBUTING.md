# Contributing

Thanks for helping. InfoPanel has two lines of work, on two branches:

- **InfoPanel 1.x**, the Windows app and the current release: branch [`1.4.x`](https://github.com/habibrehmansg/infopanel/tree/1.4.x). Fixes for 1.x, and entries for the [plugin registry](PLUGIN-REGISTRY.md) (`plugins.json`), are pull requests against `1.4.x`.
- **InfoPanel 2.0**, in development: this branch, `2.0.x`. It takes no pull requests until 2.0's first release. Issues are welcome.

## Issues

Open an issue on [GitHub](https://github.com/habibrehmansg/infopanel/issues) for a bug, a sensor that reads wrong or not at all, or a page of the documentation that is wrong. Say which version or branch (and commit) you ran, on which operating system, and how you started it. The engine's log helps: the day's file in the data folder's `logs` folder ([the data folder](docs/2.0/data-folder.md)); it never holds the session token. For a Windows sensor that reads wrong, a recording helps too: start the engine with `--record-sensors <folder> --scrub`, and the recording keeps only what the engine reads, with the computer's and the user's names, network addresses and the serial numbers it recognises replaced. Look through a recording before you attach it: a name or serial in an unusual form can remain.

A vulnerability isn't an issue: see [security](SECURITY.md).

Questions and ideas are welcome on [Discord](https://discord.gg/aNGeJxjE7Q) and [Reddit](https://www.reddit.com/r/InfoPanel/).

## Working on 2.0

You need the .NET 10 SDK (`global.json` pins the version) and, for `spec/`, Node.js 22 or later. The checks, from the repository's root:

```bash
dotnet build InfoPanel.slnx
dotnet test InfoPanel.slnx
```

and in `spec/`:

```bash
npm run check
```

[`CLAUDE.md`](CLAUDE.md) is the full guide to the code: the projects, every command and test filter, the architecture, the invariants and the conventions. In short:

- `spec/` is the contract between the engine and its clients: a change to a format or to the API changes `spec/` (its docs, schemas and `openapi.json`) in the same change as the engine, the tests hold the engine to it, and `npm run gen` regenerates the types.
- Committed outputs (goldens, snapshots, fixtures, the generated types) are regenerated only on purpose, and every change to them is reviewed ([the table](CLAUDE.md#regenerating-committed-outputs-only-on-purpose-review-the-diff)).
- Package versions are central (`Directory.Packages.props`); a new dependency is discussed first.
- Logging follows the [logging guidelines](LoggingGuidelines.md).

The documentation's index is [docs/2.0](docs/2.0/README.md).
