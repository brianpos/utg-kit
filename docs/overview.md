# Server startup
When the server starts up it will immediately index the THO repo. This is a synchronous operation that must complete before the server can handle any requests. The indexing process reads all FHIR resources from the repo and builds in-memory data structures for fast access. This allows the web app to serve pages and MCP requests with low latency, since all data is already loaded in memory.
It will have a file system monitor that tracks changes to the files and updates the in-memory index accordingly. This ensures that any edits made through the web app or externally are reflected in real-time without needing to restart the server.
While it is starting the UI will show a loading screen with progress updates. Once indexing is complete, the main UI will be displayed and the user can begin interacting with their terminology artifacts.

