# Copilot Instructions for UTG Kit

## Project Overview

UTG Kit is a toolkit for Universal Terminology Governance (UTG) contributors working with [terminology.hl7.org](https://terminology.hl7.org) (THO). It provides a site simulator, validation, authoring tools, CLI utilities, and an MCP server for AI-assisted terminology editing.

**Target users:** HL7 UTG contributors managing CodeSystems, ValueSets, and NamingSystems.

## Architecture

- **Single-stack .NET 10** — Blazor Server frontend + ASP.NET backend in one process
- **Firely .NET SDK** (`Hl7.Fhir.R4`) for all FHIR resource parsing, serialization, and validation
- **File system storage** — reads/writes directly from a local THO repo clone (no database)
- **MCP endpoint** embedded in the web app at `/mcp` (Streamable HTTP transport)

### Project Layout

```
src/
├── UtgKit.Web/            # Blazor Server app + MCP endpoint
│   ├── Components/        # Razor components (Pages/, Layout/, shared)
│   └── Mcp/               # MCP tool & resource definitions
├── UtgKit.Core/           # Domain logic — FHIR parsing, validation, authoring
├── UtgKit.Cli/            # CLI tools (validate, diff, export)
├── UtgKit.Core.Tests/     # xUnit tests for Core
├── UtgKit.Web.Tests/      # bUnit component tests
└── UtgKit.sln
docs/                      # Project documentation
.github/                   # CI/CD, copilot instructions
```

### Key Architecture Rule

All domain logic lives in `UtgKit.Core`. The Web and Cli projects are thin shells:
- `UtgKit.Web` — Blazor UI + MCP surface, injects Core services
- `UtgKit.Cli` — command-line surface, calls Core directly
- `UtgKit.Core` — all real work (parsing, validation, diffing, authoring)

## FHIR Artifacts

THO artifacts are FHIR R4 XML-format resources. Use Firely SDK types directly:
- `Hl7.Fhir.Model.CodeSystem` — parsed via `FhirXmlParser`
- `Hl7.Fhir.Model.ValueSet`
- `Hl7.Fhir.Model.NamingSystem`
- `Hl7.Fhir.Model.ImplementationGuide` (core IG only)
- `Hl7.Fhir.Model.Bundle` (JSON, containing Provenance resources)

In Blazor components, use Firely models directly — no DTOs needed. The server-side rendering means no serialization boundary between UI and domain logic.

## Build & Run

```bash
cd src
dotnet build                              # Build all projects
dotnet run --project UtgKit.Web           # Run the web app
dotnet run --project UtgKit.Cli -- <cmd>  # Run CLI commands
dotnet test                               # Run all tests
```

Configure THO repo path in `src/UtgKit.Web/appsettings.Development.json`:
```json
{ "ThoRepo": { "Path": "C:/path/to/UTG/repo" } }
```

## Testing

- **UtgKit.Core.Tests** — xUnit. Test validation rules, FHIR parsing logic, diff engine.
- **UtgKit.Web.Tests** — bUnit. Test Razor components in isolation (concept editor, diff viewer, validation display).
- **E2E** — Playwright for full site simulator and editor workflows.

## Conventions

- Place documentation in `docs/`.
- Use `UtgKit.*` namespace prefix for all projects.
- Blazor components go in `Components/Pages/` (route components) or `Components/` (shared).
- MCP tools go in `Mcp/Tools/`, MCP resources in `Mcp/Resources/`.
- CLI commands go in `Commands/` — one class per command.
- Register services in Core via extension methods (`services.AddUtgKitCore()`).
