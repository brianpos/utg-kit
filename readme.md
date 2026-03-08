# UTG Kit

The toolkit for Universal Terminology Governance — streamlining the process of creating and managing terminology artifacts for [terminology.hl7.org](https://terminology.hl7.org) (THO).

Replicating a simulator of https://terminology.hl7.org/index.html to help with local development of that site.

## What It Does

UTG Kit provides contributors to the UTG process with tools to manage THO terminology artifacts (CodeSystems, ValueSets, NamingSystems, ConceptMaps):

* **Site Simulator** — a local web server that simulates the THO site, letting you preview changes before submitting them.
* **Validation** — validate artifacts against the FHIR specification and THO-specific requirements.
* **Authoring** — create and edit artifacts with metadata editing, concept editing, and version diffs.
* **CLI Tools** — command-line utilities for validation, diffing, and bulk operations.
* **MCP Server** — an embedded Model Context Protocol endpoint so AI coding agents can search, validate, and edit terminology artifacts directly.

This does not replace the UTG governance process — it streamlines the creation and management of artifacts. You still submit changes through the standard review and approval workflow.

## Why Now?

A large volume of content is being migrated from the HL7 core specification into THO. This process is time-consuming and error-prone. UTG Kit makes it easier for contributors to manage their artifacts at scale.

## Supported Artifact Types

* CodeSystems in XML format
* ValueSets in XML format
* NamingSystems in XML format
* ImplementationGuides in XML format (the core IG only)
* Bundles in JSON format (containing Provenance resources)
* List in XML format

## Key Edit Features

* Metadata editing
* Concept editing (add, modify, hierarchy management)
* Diffs with previous versions

## Technical Architecture

* **Blazor Server** (.NET 10) — single-process web app with interactive server-side rendering
* **Firely .NET SDK** (`Hl7.Fhir.R4`) — FHIR resource parsing, serialization, and validation
* **MCP endpoint** — embedded in the web app via Streamable HTTP transport (`/mcp`)
* **File system storage** — reads/writes directly from the local THO repo clone (source of truth)
* **No separate frontend build** — one language (C#), one build system (`dotnet`)

## Project Structure

```
src/
├── UtgKit.Web/            # Blazor Server app + MCP endpoint
│   ├── Components/        # Razor components (Pages, Layout, shared)
│   └── Mcp/               # MCP tool & resource definitions
├── UtgKit.Core/           # Shared domain logic (FHIR parsing, validation, authoring)
├── UtgKit.Cli/            # CLI tools (validate, diff, export)
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

Configure the path to your local THO repo in `src/UtgKit.Web/appsettings.Development.json`:

```json
{
  "ThoRepo": {
    "Path": "C:/path/to/your/UTG/repo"
  }
}
```

### CLI Usage

```bash
dotnet run --project src/UtgKit.Cli -- validate ./CodeSystem-v3-ActCode.xml
dotnet run --project src/UtgKit.Cli -- diff ./CodeSystem-v3-ActCode.xml --previous v2.1.0
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
