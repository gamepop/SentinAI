# Contributing to SentinAI

Thank you for your interest in contributing to SentinAI!

## Getting Started

### Prerequisites
- Windows 10/11
- .NET 8 SDK or later
- Git

### Setup
```powershell
git clone https://github.com/gamepop/SentinAI.git
cd SentinAI
dotnet build
```

### Download AI Model
```powershell
.\download-models.ps1 -Provider CPU
```

## Development Workflow

1. **Fork** the repository
2. **Create a branch** for your feature: `git checkout -b feature/my-feature`
3. **Make changes** and test locally
4. **Commit** with clear messages
5. **Push** and create a Pull Request

## Code Style

- Follow [C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use meaningful variable names
- Add XML comments to public methods
- Keep methods focused and small

## Testing

```powershell
dotnet test
```

Run the Web dashboard to test changes:
```powershell
cd src/SentinAI.Web
dotnet run
```

## Reporting Issues

Include:
- Windows version
- .NET version (`dotnet --version`)
- Steps to reproduce
- Expected vs actual behavior
- Console logs if applicable

## Feature Requests

1. Check existing issues first
2. Describe the problem it solves
3. Propose a solution
4. Consider alternatives

## Areas for Contribution

- 🐛 Bug fixes
- 📖 Documentation improvements
- 🧪 Test coverage
- ⚡ Performance optimizations
- 🌐 Localization

## Code of Conduct

- Be respectful and inclusive
- Provide constructive feedback
- Focus on what's best for the project

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
