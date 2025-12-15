# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```powershell
# Build
dotnet build SentinAI.sln

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/SentinAI.Web.Tests/SentinAI.Web.Tests.csproj

# Run tests with filter (e.g., only DeepScan tests)
dotnet test --filter "FullyQualifiedName~DeepScan"

# Run single test by name
dotnet test --filter "FullyQualifiedName~DeepScanExecutionServiceTests.ExecuteCleanupAsync_DeletesApprovedFiles"

# Run Web dashboard
cd src/SentinAI.Web && dotnet run

# Download AI model (required before first run)
.\download-models.ps1 -Provider CPU

# Build for release
.\build.ps1 -Configuration Release
```

## Architecture Overview

SentinAI is a Windows storage management agent using a **hybrid AI + RAG + heuristics** approach:

```
Analysis Flow:
1. Heuristics (<1ms) → Fast rule-based categorization (temp, cache, node_modules)
2. RAG Memory → Query Weaviate for similar past decisions
3. Phi-4 Mini AI (3-10s) → Final safety decision with JSON output
4. Memory Storage → Store decision in Weaviate for future reference
```

### Project Structure

| Project | Purpose |
|---------|---------|
| `src/SentinAI.Web` | Blazor Server dashboard, API controllers, AgentBrain service |
| `src/SentinAI.Shared` | Models, Protobuf definitions, shared services |
| `src/SentinAI.SentinelService` | Windows background service for USN Journal monitoring |
| `tests/SentinAI.Web.Tests` | Unit tests for web services (xUnit + Moq) |
| `tests/SentinAI.Shared.Tests` | Unit tests for shared models |

### Key Services

**AgentBrain** (`src/SentinAI.Web/Services/AgentBrain.cs`)
- Core hybrid AI engine implementing `IAgentBrain`
- Methods: `AnalyzeFolderAsync`, `AnalyzeFilesAsync`, `AnalyzeAppRemovalAsync`, `AnalyzeRelocationAsync`
- Uses ONNX Runtime GenAI with Phi-4 Mini model
- Falls back to heuristics when model unavailable

**Deep Scan Services** (`src/SentinAI.Web/Services/DeepScan/`)
- `DeepScanExecutionService` - Executes cleanup/relocation operations
- `DeepScanLearningService` - Records decisions in RAG for learning
- `SpaceAnalysisService` - Drive space analysis by category
- `AppDiscoveryService` - Windows app detection (Windows-only)
- `WeaviateDeepScanRagStore` - Vector memory for Deep Scan decisions

**RAG Memory** (`src/SentinAI.Web/Services/Rag/`)
- `WeaviateRagStore` - Weaviate vector database integration
- `NoopRagStore` - In-memory fallback when Weaviate unavailable
- Uses Ollama `nomic-embed-text` for embeddings

### Configuration

Primary config in `src/SentinAI.Web/appsettings.json`:
- `Brain` section - AI model settings (ExecutionProvider, MaxSequenceLength, Temperature)
- `RagStore` section - Weaviate connection for brain memory
- `DeepScanRagStore` section - Weaviate connection for deep scan learning

### gRPC Communication

Protocol definitions in `src/SentinAI.Shared/Protos/agent.proto`. The Web dashboard communicates with SentinelService over Named Pipes using gRPC.

## Testing Patterns

Tests use xUnit with Moq for mocking. Key test files:
- `AgentBrainHeuristicTests.cs` - Heuristic rule engine tests
- `DeepScanExecutionServiceTests.cs` - Cleanup/relocation execution tests
- `DeepScanLearningServiceTests.cs` - RAG learning tests

Note: Deep Scan services are Windows-only (`[SupportedOSPlatform("windows")]`), tests will show CA1416 warnings on non-Windows platforms.

## Development Notes

- .NET 8.0 for Web/Shared, .NET 9.0 for SentinelService (global.json allows rollForward)
- Windows 10/11 required for full functionality (USN Journal, App Discovery)
- Phi-4 Mini model (~2.5GB) stored in `%LocalAppData%\SentinAI\Models\`
- RAG requires Docker (Weaviate) and Ollama running locally
