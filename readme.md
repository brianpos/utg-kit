# UTG Kit

The toolkit for Universal Terminology Governance — streamlining the process of creating and managing terminology artifacts for [terminology.hl7.org](https://terminology.hl7.org) (THO).

Replicating a simulator of https://terminology.hl7.org/index.html to help with local development of that site.

## Quick Start

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and clone the [THO repository](https://github.com/HL7/UTG).

2. Set the path to your local THO repo's `input/sourceOfTruth` directory in `src/UtgKit.Web/appsettings.json`:

   ```json
   {
     "ThoRepo": {
       "Path": "C:/path/to/your/UTG/repo/input/sourceOfTruth"
     }
   }
   ```

   You can also override this in `src/UtgKit.Web/appsettings.Development.json` for local development.

3. Run the web app:

   ```bash
   cd src
   dotnet run --project UtgKit.Web
   ```

The site will start indexing your THO repository on first load — a progress bar shows the status. Once indexing completes, you can browse CodeSystems, ValueSets, ConceptMaps, and more.

## What It Does

UTG Kit provides contributors to the UTG process with tools to manage THO terminology artifacts (CodeSystems, ValueSets, ConceptMaps):

* **Site Simulator** — a local web server that simulates the THO site, letting you browse and preview CodeSystems, ValueSets, and ConceptMaps before submitting changes.
* **Import** — import CodeSystems and ValueSets directly from any FHIR publication (including the [FHIR core build](https://build.fhir.org) and HL7 Implementation Guides) into your local THO repository with automatic manifest and provenance tracking.
* **Resource History** — view provenance-based change history for each resource, extracted from history bundles.
* **ValueSet Expansion** — expand ValueSets locally using the Firely SDK and your THO repository as the code system source.
* **Live File Watching** — changes to XML/JSON files in the THO repository are detected automatically and the in-memory index updates in real time (with debouncing for bulk operations).
* **Rendering Manifest Management** — add or remove resources from rendering manifest groups (e.g. "fhir", "v2", "v3").
* **MCP Server** — an embedded Model Context Protocol endpoint so AI coding agents can interact with terminology artifacts directly.
* **CLI Tools** — command-line utilities for validation, diffing, and bulk operations (in progress).

This does not replace the UTG governance process — it streamlines the creation and management of artifacts. You still submit changes through the standard review and approval workflow.

## Why Now?

A large volume of content is being migrated from the HL7 core specification into THO. This process is time-consuming and error-prone. UTG Kit makes it easier for contributors to manage their artifacts at scale.

## Supported Artifact Types

* CodeSystems in XML format
* ValueSets in XML format
* ConceptMaps in XML format
* Bundles in JSON format (containing Provenance resources for change history)
* Lists in XML format (including rendering manifests)

## Site Simulator Pages

* **Home** — index status, quick links to import actions
* **Code Systems** — searchable list of all CodeSystems with detail view (metadata, concepts, XML/JSON tabs, resource history)
* **Value Sets** — searchable list of all ValueSets with detail view (metadata, compose/expansion, XML/JSON tabs, resource history)
* **Concept Maps** — searchable list of all ConceptMaps with source/target scope display
* **Import CodeSystem** — download a CodeSystem from a FHIR publication URL, save to THO repo, update manifest, append provenance
* **Import ValueSet** — download a ValueSet (and optionally its referenced CodeSystems) from a FHIR publication URL

## Core Services

* **`ThoFileService`** — reads/writes FHIR resources from the local THO repo clone; maintains an in-memory index of all resource metadata for fast lookups; supports incremental re-indexing and file-watcher suppression during imports.
* **`ThoFileWatcher`** — background service watching the THO directory for `.xml`/`.json` changes with debounced incremental index updates.
* **`ThoResourceResolver`** — implements Firely's `IAsyncResourceResolver` to resolve CodeSystems and ValueSets by canonical URL from the local repo.
* **`ValueSetExpansionService`** — expands ValueSets using the Firely SDK's `ValueSetExpander` with the local THO repository as the source.
* **`ImportCodeSystemService`** — downloads a CodeSystem from a FHIR publication, saves it as XML, updates the rendering manifest, and appends a provenance entry.
* **`ImportValueSetService`** — downloads a ValueSet (and optionally referenced CodeSystems), saves to the repo, and tracks provenance.

## Technical Architecture

* **Blazor Server** (.NET 10) — single-process web app with interactive server-side rendering
* **Firely .NET SDK** (`Hl7.Fhir.R5` v5.13.2) — FHIR resource parsing, serialization, and validation
* **MCP endpoint** — embedded in the web app via Streamable HTTP transport (`/mcp`)
* **File system storage** — reads/writes directly from the local THO repo clone (source of truth)
* **File watcher** — `ThoFileWatcher` background service detects changes and incrementally updates the in-memory index
* **No separate frontend build** — one language (C#), one build system (`dotnet`)

## Project Structure

```
src/
├── UtgKit.Web/            # Blazor Server app + MCP endpoint
│   ├── Components/        # Razor components (Pages, Layout, shared)
│   └── Mcp/               # MCP tool & resource definitions
├── UtgKit.Core/           # Shared domain logic (FHIR parsing, indexing, import, history)
│   └── Services/          # ThoFileService, ImportCodeSystemService, ImportValueSetService, etc.
├── UtgKit.Cli/            # CLI tools (in progress)
├── UtgKit.Core.Tests/     # xUnit tests for domain logic
├── UtgKit.Web.Tests/      # bUnit component tests
└── UtgKit.sln
docs/                      # Project documentation
.github/                   # CI/CD, copilot instructions
```

## Getting Started

### Prerequisites

* [.NET 10 SDK](https://dotnet.microsoft.com/download)
* A local clone of the [THO repository](https://github.com/HL7/UTG)

### Run the Web App

```bash
cd src
dotnet run --project UtgKit.Web
```

Configure the path to your local THO repo's `input/sourceOfTruth` directory in `src/UtgKit.Web/appsettings.json` (or `appsettings.Development.json`):

```json
{
  "ThoRepo": {
    "Path": "C:/path/to/your/UTG/repo/input/sourceOfTruth"
  }
}
```

### MCP Configuration

Add to your VS Code / AI client MCP settings:

```json
{
  "mcpServers": {
    "utg-kit": {
      "type": "http",
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

### Run Tests

```bash
cd src
dotnet test
```
