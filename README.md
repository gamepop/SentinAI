# SentinAI - Autonomous Windows Storage Agent

**SentinAI** is an autonomous agent for Windows that intelligently manages your storage using a **hybrid AI + RAG + heuristics approach**. It combines fast rule-based analysis with Phi-3 Mini AI and Weaviate vector memory for making safe, context-aware cleanup decisions.

## 🎯 Core Philosophy

- **Hybrid Intelligence:** Heuristics provide fast context, RAG retrieves past decisions, AI makes the final call
- **Memory-Augmented:** Weaviate vector database stores and retrieves past cleanup decisions for consistency
- **Safe by Design:** "Propose-Verify-Execute" pattern prevents accidental data loss
- **Local & Private:** All AI inference and vector storage runs locally - your data never leaves your machine
- **User-Friendly:** Simplified Home page for novice users, advanced Dashboard for power users

## 🏗️ Architecture

The application consists of multiple components:

| Component | Tech Stack | Responsibility |
|-----------|-----------|----------------|
| **Web Dashboard** | Blazor Server (.NET 8) | User interface, Brain hosting, API |
| **Sentinel Service** | .NET 8 Worker Service | Monitors USN Journal, executes cleanup |
| **Brain (Hybrid AI)** | ONNX Runtime GenAI + Phi-3 Mini | Analyzes folders, makes safety decisions |
| **RAG Memory** | Weaviate + Ollama (nomic-embed-text) | Vector storage for past decisions |
| **Shared Library** | .NET 8 Class Library | Models, Protos, shared services |

### RAG-Enhanced Analysis Flow

```
1. HEURISTIC ANALYSIS (fast, rule-based)
   ├── Path-based detection (temp folders, caches, node_modules)
   ├── File pattern matching (*.tmp, *.log, *.cache)
   └── Winapp2 rules matching

2. RAG MEMORY RETRIEVAL (Weaviate + Ollama)
   ├── Generate embedding for current folder context
   ├── Query similar past decisions from vector store
   └── Include relevant memories in AI prompt

3. AI DECISION (Phi-3 Mini)
   ├── Receives heuristic context + RAG memories
   ├── Analyzes folder + files with historical context
   └── Makes FINAL safe/unsafe decision

4. MEMORY STORAGE
   ├── Store decision in Weaviate for future reference
   └── Build institutional knowledge over time

5. OUTPUT
   └── JSON response with confidence score
```

### System Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        SentinAI Web UI                          │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │    Home     │  │  Dashboard  │  │   Settings/Scheduler    │  │
│  │ (One-Click) │  │ (Advanced)  │  │   (Auto Cleanup)        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      AgentBrain Service                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │ Heuristics  │  │   Phi-3     │  │    RAG Memory Store     │  │
│  │   Engine    │──│  ONNX AI    │──│  (Weaviate + Ollama)    │  │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Sentinel Service (gRPC)                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │    USN      │  │   Cleanup   │  │   State Machine         │  │
│  │  Journal    │  │  Executor   │  │   Orchestrator          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## 🚀 Features

### 🧠 Brain Analysis Engine
- **Phi-3 Mini 4K Instruct** - Microsoft's compact yet capable LLM (~2.5GB)
- **CPU or DirectML (GPU)** - Configurable execution provider
- **Hybrid approach** - Heuristics validate, RAG retrieves context, AI decides
- **Structured output** - JSON responses for reliable parsing

### 🔍 RAG Memory System (NEW)
- **Weaviate Vector Database** - Local vector storage at `localhost:8080`
- **Ollama Embeddings** - `nomic-embed-text` model at `localhost:11434`
- **Contextual Recall** - Retrieves similar past decisions to inform new ones
- **Learning Over Time** - Builds institutional knowledge from user decisions

### 📋 Heuristic Rules
- Windows/User temp folders → **Safe**
- Browser caches → **Safe**
- node_modules → **Safe** (developer confirmation)
- Build artifacts (bin/obj) → **Safe**
- Downloads with documents → **Review Required**

### 🏠 User-Friendly Home Page (NEW)
- **One-Click Quick Scan** - Simple scanning for novice users
- **Individual Item Approval** - Approve or skip each item separately
- **Bulk Actions** - Approve All / Dismiss All for multiple items
- **Real-time Progress** - Visual feedback during scanning
- **Smart Status** - Shows pending items on page load

### 📊 Advanced Dashboard
- **Detailed Analysis** - Full breakdown of all suggestions
- **AI Reasoning** - See why each item was flagged
- **Category Filtering** - Filter by safe/review status
- **Execution History** - Track past cleanup operations

### ⏰ Auto Cleanup Scheduler (NEW)
- **Quick Toggle Cards** - Daily, Weekly, Monthly presets
- **Friendly Time Picker** - No cron expressions needed
- **Safe Items Only** - Auto-cleanup only affects pre-approved categories
- **Notification Options** - Get notified before/after cleanup

### 🔗 Winapp2 Integration
- Community-maintained cleanup rules
- Auto-download from official source
- Grounds AI decisions in proven patterns

## 📦 Project Structure

```
SentinAI/
├── src/
│   ├── SentinAI.Web/                  # Blazor Server Dashboard
│   │   ├── Components/
│   │   │   └── Pages/
│   │   │       ├── Home.razor         # User-friendly scan page
│   │   │       ├── Dashboard.razor    # Advanced analysis view
│   │   │       ├── Scheduler.razor    # Auto cleanup scheduling
│   │   │       └── Settings.razor     # Configuration page
│   │   ├── Controllers/               # API controllers
│   │   ├── Services/
│   │   │   ├── AgentBrain.cs          # Hybrid AI + RAG engine
│   │   │   ├── RagMemoryStore.cs      # Weaviate integration
│   │   │   ├── BrainConfiguration.cs  # CPU/DirectML config
│   │   │   ├── ModelDownloadService.cs
│   │   │   └── BrainInitializationService.cs
│   │   └── appsettings.json           # Configuration
│   │
│   ├── SentinAI.SentinelService/      # Background service
│   │   ├── Services/
│   │   │   ├── DriveMonitor.cs        # USN Journal monitoring
│   │   │   ├── UsnJournalReader.cs    # P/Invoke USN reader
│   │   │   ├── CleanupExecutor.cs     # Safe file deletion
│   │   │   └── StateMachineOrchestrator.cs
│   │   └── Program.cs
│   │
│   ├── SentinAI.Shared/               # Shared models
│   │   ├── Models/
│   │   └── Protos/agent.proto         # gRPC definitions
│   │
│   └── SentinAI.Brain/                # WinUI 3 app (optional)
│
├── download-models.ps1                # Manual model download script
├── build.ps1                          # Build script
└── install-service.ps1                # Service installer
```

## 🛠️ Quick Start

### Prerequisites

- **Windows 10/11** (22H2 or later)
- **.NET 8 SDK** or later - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **~3GB disk space** for AI model
- **Docker** (optional, for Weaviate RAG)
- **Ollama** (optional, for embeddings)

### 1. Clone & Build

```powershell
git clone https://github.com/gamepop/SentinAI.git
cd SentinAI
dotnet build SentinAI.sln
```

### 2. Download AI Model

The Phi-3 model (~2.5GB) downloads automatically on first run, or manually:

```powershell
# Download CPU model (recommended)
.\download-models.ps1 -Provider CPU

# Or download DirectML (GPU) model
.\download-models.ps1 -Provider DirectML

# Or download both
.\download-models.ps1 -Provider All
```

### 3. Setup RAG Memory (Optional but Recommended)

```powershell
# Start Weaviate vector database
docker run -d --name weaviate -p 8080:8080 -p 50051:50051 `
  -e AUTHENTICATION_ANONYMOUS_ACCESS_ENABLED=true `
  -e PERSISTENCE_DATA_PATH=/var/lib/weaviate `
  -e DEFAULT_VECTORIZER_MODULE=none `
  -e CLUSTER_HOSTNAME=node1 `
  cr.weaviate.io/semitechnologies/weaviate:1.28.4

# Install and start Ollama for embeddings
winget install Ollama.Ollama
ollama pull nomic-embed-text
```

### 4. Run the Web Dashboard

```powershell
cd src/SentinAI.Web
dotnet run
```

Open http://localhost:5203 in your browser.

## ⚙️ Configuration

Edit `src/SentinAI.Web/appsettings.json`:

```json
{
  "Brain": {
    "ExecutionProvider": "CPU",
    "ModelPath": "",
    "ForceModelRedownload": false,
    "InferenceTimeoutSeconds": 60,
    "MaxSequenceLength": 2048,
    "MaxOutputTokens": 150,
    "Temperature": 0.1
  },
  "Rag": {
    "Enabled": true,
    "WeaviateUrl": "http://localhost:8080",
    "OllamaUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "MaxMemories": 5
  }
}
```

### Execution Providers

| Provider | Pros | Cons |
|----------|------|------|
| **CPU** | Works everywhere, stable | Slower inference (~5-15s) |
| **DirectML** | GPU accelerated, fast | Requires DirectX 12 GPU |

### Model Locations

Models are stored in:
- **CPU:** `%LocalAppData%\SentinAI\Models\Phi3-Mini-CPU\`
- **DirectML:** `%LocalAppData%\SentinAI\Models\Phi3-Mini-DirectML\`

## 🔐 Security

- **Local Processing:** All AI runs locally, no cloud API calls
- **No Data Collection:** Files are analyzed but never uploaded
- **Safe Defaults:** Unknown folders default to "not safe to delete"
- **User Confirmation:** Ambiguous items require manual approval
- **Individual Control:** Approve/reject each cleanup suggestion separately

## 📊 API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/brain/status` | GET | Brain status and statistics |
| `/api/brain/analyze` | POST | Analyze a folder path |
| `/api/agent/suggestions` | GET | Get pending cleanup suggestions |
| `/api/agent/approve/{id}` | POST | Approve and execute cleanup |
| `/api/agent/reject/{id}` | POST | Reject/dismiss suggestions |
| `/api/agent/clean-path` | POST | Clean a specific path |
| `/api/agent/analyze` | POST | Trigger full system scan |
| `/api/scheduler/status` | GET | Get scheduler configuration |
| `/api/scheduler/configure` | POST | Update scheduler settings |

## 🧪 Development

### Running Tests

```powershell
dotnet test
```

### Debug Logging

Enable detailed AI logs in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "SentinAI.Web.Services.AgentBrain": "Debug",
      "SentinAI.Web.Services.RagMemoryStore": "Debug"
    }
  }
}
```

### Building for Release

```powershell
.\build.ps1 -Configuration Release
```

## 🗺️ Roadmap

- [x] Hybrid AI + Heuristics engine
- [x] CPU and DirectML support
- [x] Phi-3 Mini integration
- [x] Winapp2 rules parser
- [x] Web dashboard
- [x] RAG memory system (Weaviate + Ollama)
- [x] User-friendly Home page with one-click scan
- [x] Individual item approval/rejection
- [x] Auto cleanup scheduler
- [x] Settings page
- [ ] Real-time USN Journal monitoring
- [ ] Duplicate file detection
- [ ] Windows Store submission

## 📜 License

MIT License - See [LICENSE](LICENSE) for details.

**Dependencies:**
- [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai) - MIT
- [Phi-3 Mini](https://huggingface.co/microsoft/Phi-3-mini-4k-instruct) - MIT
- [Winapp2](https://github.com/MoscaDotTo/Winapp2) - CC BY-NC-SA
- [Weaviate](https://weaviate.io/) - BSD-3-Clause
- [Ollama](https://ollama.ai/) - MIT

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## 📞 Support

- **Issues:** [GitHub Issues](https://github.com/gamepop/SentinAI/issues)
- **Discussions:** [GitHub Discussions](https://github.com/gamepop/SentinAI/discussions)

---

**Built with** .NET 8, Blazor, ONNX Runtime, Phi-3 AI, Weaviate, and Ollama
