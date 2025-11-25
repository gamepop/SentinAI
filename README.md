# SentinAI - Autonomous Windows Storage Agent

**SentinAI** is an autonomous agent for Windows that intelligently manages your storage using a **hybrid AI + heuristics approach**. It combines fast rule-based analysis with Phi-3 Mini AI for making safe cleanup decisions.

## 🎯 Core Philosophy

- **Hybrid Intelligence:** Heuristics provide fast context, AI makes the final decision
- **Safe by Design:** "Propose-Verify-Execute" pattern prevents accidental data loss
- **Local & Private:** All AI inference runs locally - your data never leaves your machine
- **Configurable:** Switch between CPU and GPU (DirectML) execution providers

## 🏗️ Architecture

The application consists of multiple components:

| Component | Tech Stack | Responsibility |
|-----------|-----------|----------------|
| **Web Dashboard** | Blazor Server (.NET 8) | User interface, Brain hosting, API |
| **Sentinel Service** | .NET 8 Worker Service | Monitors USN Journal, executes cleanup |
| **Brain (Hybrid AI)** | ONNX Runtime GenAI + Phi-3 Mini | Analyzes folders, makes safety decisions |
| **Shared Library** | .NET 8 Class Library | Models, Protos, shared services |

### Hybrid Analysis Flow

```
1. HEURISTIC ANALYSIS (fast, rule-based)
   ├── Path-based detection (temp folders, caches, node_modules)
   ├── File pattern matching (*.tmp, *.log, *.cache)
   └── Winapp2 rules matching

2. AI DECISION (Phi-3 Mini)
   ├── Receives heuristic context
   ├── Analyzes folder + files
   └── Makes FINAL safe/unsafe decision

3. OUTPUT
   └── JSON response with confidence score
```

## 🚀 Features

### Brain Analysis Engine
- **Phi-3 Mini 4K Instruct** - Microsoft's compact yet capable LLM
- **CPU or DirectML (GPU)** - Configurable execution provider
- **Hybrid approach** - Heuristics validate, AI decides
- **Structured output** - JSON responses for reliable parsing

### Heuristic Rules
- Windows/User temp folders → **Safe**
- Browser caches → **Safe**
- node_modules → **Safe** (developer confirmation)
- Build artifacts (bin/obj) → **Safe**
- Downloads with documents → **Review Required**

### Winapp2 Integration
- Community-maintained cleanup rules
- Auto-download from official source
- Grounds AI decisions in proven patterns

## 📦 Project Structure

```
SentinAI/
├── src/
│   ├── SentinAI.Web/                  # Blazor Server Dashboard
│   │   ├── Components/                # Razor components
│   │   ├── Controllers/               # API controllers
│   │   ├── Services/
│   │   │   ├── AgentBrain.cs          # Hybrid AI + Heuristics engine
│   │   │   ├── BrainConfiguration.cs  # CPU/DirectML config
│   │   │   ├── ModelDownloadService.cs # Phi-3 model downloader
│   │   │   └── BrainInitializationService.cs
│   │   └── appsettings.json           # Configuration
│   │
│   ├── SentinAI.SentinelService/      # Background service
│   │   ├── Services/
│   │   │   ├── DriveMonitor.cs        # USN Journal monitoring
│   │   │   ├── UsnJournalReader.cs    # P/Invoke USN reader
│   │   │   └── CleanupExecutor.cs     # Safe file deletion
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

### 3. Run the Web Dashboard

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
    "ExecutionProvider": "CPU",        // "CPU" or "DirectML"
    "ModelPath": "",                    // Custom path or empty for default
    "ForceModelRedownload": false,
    "InferenceTimeoutSeconds": 60,
    "MaxSequenceLength": 2048,          // Total context window
    "MaxOutputTokens": 150,             // Max response tokens
    "Temperature": 0.1                  // Lower = more deterministic
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

## 📊 API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/brain/status` | GET | Brain status and statistics |
| `/api/brain/analyze` | POST | Analyze a folder path |
| `/api/cleanup/scan` | POST | Full system scan |
| `/api/cleanup/execute` | POST | Execute cleanup |

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
      "SentinAI.Web.Services.AgentBrain": "Debug"
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
- [ ] Real-time USN Journal monitoring
- [ ] Duplicate file detection
- [ ] Scheduled cleanup tasks
- [ ] Windows Store submission

## 📜 License

MIT License - See [LICENSE](LICENSE) for details.

**Dependencies:**
- [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai) - MIT
- [Phi-3 Mini](https://huggingface.co/microsoft/Phi-3-mini-4k-instruct) - MIT
- [Winapp2](https://github.com/MoscaDotTo/Winapp2) - CC BY-NC-SA

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## 📞 Support

- **Issues:** [GitHub Issues](https://github.com/gamepop/SentinAI/issues)
- **Discussions:** [GitHub Discussions](https://github.com/gamepop/SentinAI/discussions)

---

**Built with** .NET 8, Blazor, ONNX Runtime, and Phi-3 AI
