# UTG Kit

The toolkit for Universal Terminology Governance — streamlining the process of creating and managing terminology artifacts for [terminology.hl7.org](https://terminology.hl7.org) (THO).

This package ships `utgkit` as a [.NET global tool](https://learn.microsoft.com/dotnet/core/tools/global-tools): a local Blazor web app that simulates the THO site, pointed at a local clone of the [THO repository](https://github.com/HL7/UTG).

The project is best used alongside VsCode or other text editors for general content editing, 
and using this tool to assist in visualising, importing VS/CS content and managing the many resource lists.
Then use normal `git` tools for version control and collaboration.

## Prerequisites

* [.NET 10 SDK or runtime](https://dotnet.microsoft.com/download)
* A local clone of the [THO repository](https://github.com/HL7/UTG)

## Install

```bash
dotnet tool install --global UtgKit
```

To upgrade later:

```bash
dotnet tool update --global UtgKit
```

To uninstall:

```bash
dotnet tool uninstall --global UtgKit
```

## Run

The simplest way is to `cd` into your local THO clone and just launch the tool — the current working directory is used as the THO repo path by default:

```bash
cd C:\path\to\your\UTG\repo\input\sourceOfTruth
utgkit
```

Then open the URL printed in the console (e.g. `http://localhost:5000`).

### Specifying the THO repo path

If you'd rather not `cd`, point the tool at the repo with `-r` / `--repo`:

```bash
utgkit --repo C:\path\to\your\UTG\repo\input\sourceOfTruth
```

The canonical configuration form `--ThoRepo:Path <path>` and the environment variable `ThoRepo__Path` are also honored. Command-line values take precedence.

### Specifying the port / URL

Use the standard ASP.NET Core `--urls` switch:

```bash
utgkit --urls http://localhost:5099
```

You can supply multiple comma-separated URLs, e.g. `--urls "http://localhost:5099;http://0.0.0.0:5099"`.

### All options

```text
Usage:
  utgkit [options]

Options:
  -r, --repo <path>        Path to the local THO repo clone. Defaults to the
                           current working directory.
      --urls <url>         URL(s) the server should listen on
                           (e.g. http://localhost:5099). Comma-separated.
      --ThoRepo:Path <p>   Canonical configuration form of --repo.
  -h, --help               Show help.
```

The site begins indexing your THO repository on first load — a progress bar shows the status. Once indexing completes, you can browse CodeSystems, ValueSets, ConceptMaps, and more.

## What It Does

UTG Kit provides contributors to the UTG process with tools to manage THO terminology artifacts (CodeSystems, ValueSets, ConceptMaps):

* **Site Simulator** — a local web server that simulates the THO site, letting you browse and preview CodeSystems, ValueSets, and ConceptMaps before submitting changes.
* **Import** — import CodeSystems and ValueSets directly from any FHIR publication (including the [FHIR core build](https://build.fhir.org) and HL7 Implementation Guides) into your local THO repository with automatic manifest and provenance tracking.
* **Resource History** — view provenance-based change history for each resource, extracted from history bundles.
* **ValueSet Expansion** — expand ValueSets locally using the Firely SDK and your THO repository as the code system source.
* **Live File Watching** — changes to XML/JSON files in the THO repository are detected automatically and the in-memory index updates in real time (with debouncing for bulk operations).
* **Rendering Manifest Management** — add or remove resources from rendering manifest groups (e.g. "fhir", "v2", "v3").

This does not replace the UTG governance process — it streamlines the creation and management of artifacts. You still submit changes through the standard review and approval workflow.

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

## Source & Issues

Source code, issue tracker and contribution guide: <https://github.com/brianpos/utg-kit>
