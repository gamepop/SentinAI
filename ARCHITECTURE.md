# SentinAI Architecture

This document describes the architecture and design decisions of SentinAI.

## Overview

SentinAI uses a **hybrid AI + heuristics architecture** where:
1. **Heuristics** provide fast, rule-based context
2. **AI (Phi-3 Mini)** makes the final safety decision

This approach combines the speed of rules with the intelligence of AI.

## Component Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    SentinAI.Web                             │
│                  (Blazor Server)                            │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Dashboard  │  │    API      │  │   Brain Services    │  │
│  │  Components │  │ Controllers │  │                     │  │
│  └─────────────┘  └─────────────┘  │  ┌───────────────┐  │  │
│                                     │  │  AgentBrain   │  │  │
│                                     │  │  (Hybrid AI)  │  │  │
│                                     │  └───────────────┘  │  │
│                                     │  ┌───────────────┐  │  │
│                                     │  │ ModelDownload │  │  │
│                                     │  │   Service     │  │  │
│                                     │  └───────────────┘  │  │
│                                     └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                          │
                          │ gRPC (future)
                          ▼
┌─────────────────────────────────────────────────────────────┐
│              SentinAI.SentinelService                       │
│                (.NET 8 Worker)                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │DriveMonitor │  │ USN Journal │  │  CleanupExecutor    │  │
│  │             │  │   Reader    │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Hybrid AI Architecture

### The Problem with Pure Heuristics
- Rigid rules can't handle edge cases
- No context awareness
- High false positive/negative rate

### The Problem with Pure AI
- Slow inference (especially on CPU)
- May hallucinate on unfamiliar paths
- Black box decisions

### Our Solution: Hybrid Approach

```
┌──────────────────────────────────────────────────────────┐
│                    Analysis Request                       │
│                    (folder + files)                       │
└────────────────────────┬─────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────┐
│              STEP 1: Heuristic Analysis                  │
│                    (< 1ms)                               │
│  ┌────────────────────────────────────────────────────┐  │
│  │ • Path-based rules (temp, cache, node_modules)     │  │
│  │ • File extension patterns (*.tmp, *.log)           │  │
│  │ • Winapp2 database matching                        │  │
│  │ • Known safe/unsafe patterns                       │  │
│  └────────────────────────────────────────────────────┘  │
│  OUTPUT: Category, Confidence, Initial Suggestion        │
└────────────────────────┬─────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────┐
│              STEP 2: AI Decision                         │
│                  (5-15s on CPU)                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │ • Receives heuristic context                       │  │
│  │ • Analyzes path semantics                          │  │
│  │ • Reviews file list                                │  │
│  │ • Makes FINAL safe/unsafe decision                 │  │
│  └────────────────────────────────────────────────────┘  │
│  OUTPUT: safe_to_delete, confidence, reason (JSON)       │
└────────────────────────┬─────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────┐
│                   Final Result                           │
│  • AI decision takes precedence                          │
│  • Heuristics can be overridden                          │
│  • Low confidence → User review required                 │
└──────────────────────────────────────────────────────────┘
```

## AI Model: Phi-3 Mini

### Why Phi-3?
- **Small size:** ~2.5GB quantized (vs 8GB+ for larger models)
- **Fast inference:** Works on CPU and GPU
- **MIT License:** Commercial use allowed
- **Microsoft optimized:** ONNX Runtime GenAI support

### Model Variants

| Variant | Provider | Package | Use Case |
|---------|----------|---------|----------|
| CPU INT4 | CPU | `Microsoft.ML.OnnxRuntimeGenAI` | Universal compatibility |
| DirectML INT4 | GPU | `Microsoft.ML.OnnxRuntimeGenAI.DirectML` | Fast inference |

### Configuration

```json
{
  "Brain": {
    "ExecutionProvider": "CPU",      // or "DirectML"
    "MaxSequenceLength": 2048,       // Total context window
    "MaxOutputTokens": 150,          // Response length limit
    "InferenceTimeoutSeconds": 60,
    "Temperature": 0.1               // Low = deterministic
  }
}
```

## Prompt Engineering

### System Prompt (Compact)
```
You are a Windows cleanup safety analyzer.
Rules: Never delete user documents/photos. Temp/cache folders are safe.
Respond ONLY with JSON: {"safe_to_delete":true/false,"confidence":0.0-1.0,"reason":"..."}
```

### User Prompt
```
Path: C:\Users\{user}\AppData\Local\Temp
Files (5): tmp1234.tmp, ~$word.docx, cache.db (+2 more)
Heuristic: Temp, safe=True, conf=0.95
Your JSON decision:
```

### Why This Design?
- **Compact:** Fewer tokens = faster inference
- **Structured:** JSON output is parseable
- **Grounded:** Heuristic context prevents hallucination

## Heuristic Rules Engine

### Rule Categories

| Category | Path Pattern | Default | Confidence |
|----------|-------------|---------|------------|
| `Temp` | `*\Temp`, `*\temp` | Safe | 95% |
| `Cache` | `*\cache*`, browser paths | Safe | 93% |
| `NodeModules` | `*\node_modules` | Safe | 85% |
| `BuildArtifacts` | `*\bin\*`, `*\obj\*` | Safe | 80% |
| `Downloads` | `*\Downloads` | Review | 40% |
| `Unknown` | Everything else | Unsafe | 10% |

### Rule Precedence
1. Explicit exclusions (Windows, Program Files)
2. Winapp2 database matches
3. Path-based patterns
4. File extension patterns
5. Default: Unknown → Unsafe

## Security Model

### Data Privacy
- **100% Local:** No cloud API calls
- **No Telemetry:** File paths never leave the machine
- **Ephemeral:** Analysis results not persisted

### Safe Defaults
- Unknown folders → **Not safe**
- Low confidence → **User approval required**
- System folders → **Always excluded**

### Exclusion List
```csharp
string[] AlwaysExclude = {
    "Windows", "Program Files", "Program Files (x86)",
    "$Recycle.Bin", "System Volume Information"
};
```

## Performance

### Inference Times (Typical)

| Hardware | Time per Folder |
|----------|-----------------|
| Modern CPU (i7/Ryzen 7) | 5-10 seconds |
| GPU (RTX 3060+) | 1-3 seconds |
| NPU (Copilot+ PC) | <1 second |

### Memory Usage

| Component | RAM |
|-----------|-----|
| Web Dashboard | ~150MB |
| AI Model (loaded) | ~2.5GB |
| **Total** | ~2.7GB |

### Optimizations
- **Lazy loading:** Model loads only when needed
- **Token limits:** Short prompts = fast inference
- **Batch analysis:** Multiple folders per session
- **Heuristic bypass:** High-confidence rules skip AI

## Future Enhancements

- [ ] **Streaming inference:** Show AI thinking in real-time
- [ ] **Learning from feedback:** Improve with user corrections
- [ ] **Vector embeddings:** Semantic file similarity
- [ ] **Scheduled scans:** Background cleanup tasks

---

*Last updated: November 2025*
