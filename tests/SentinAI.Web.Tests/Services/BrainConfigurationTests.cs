using SentinAI.Web.Services;

namespace SentinAI.Web.Tests.Services;

public class BrainConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrectlySet()
    {
        // Arrange & Act
        var config = new BrainConfiguration();

        // Assert
        Assert.Equal("CPU", config.ExecutionProvider);
        Assert.Equal(string.Empty, config.ModelPath);
        Assert.False(config.ForceModelRedownload);
        Assert.Equal(30, config.InferenceTimeoutSeconds);
        Assert.Equal(4096, config.MaxSequenceLength);
        Assert.Equal(150, config.MaxOutputTokens);
        Assert.Equal(0.1, config.Temperature);
    }

    [Fact]
    public void UseCpu_ReturnsTrueForCpuProvider()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "CPU" };

        // Assert
        Assert.True(config.UseCpu);
        Assert.False(config.UseDirectML);
    }

    [Fact]
    public void UseCpu_IsCaseInsensitive()
    {
        // Arrange & Act & Assert
        Assert.True(new BrainConfiguration { ExecutionProvider = "cpu" }.UseCpu);
        Assert.True(new BrainConfiguration { ExecutionProvider = "CPU" }.UseCpu);
        Assert.True(new BrainConfiguration { ExecutionProvider = "Cpu" }.UseCpu);
    }

    [Fact]
    public void UseDirectML_ReturnsTrueForDirectMLProvider()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "DirectML" };

        // Assert
        Assert.True(config.UseDirectML);
        Assert.False(config.UseCpu);
    }

    [Fact]
    public void UseDirectML_ReturnsTrueForGPUProvider()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "GPU" };

        // Assert
        Assert.True(config.UseDirectML);
        Assert.False(config.UseCpu);
    }

    [Fact]
    public void UseDirectML_IsCaseInsensitive()
    {
        // Arrange & Act & Assert
        Assert.True(new BrainConfiguration { ExecutionProvider = "directml" }.UseDirectML);
        Assert.True(new BrainConfiguration { ExecutionProvider = "DirectML" }.UseDirectML);
        Assert.True(new BrainConfiguration { ExecutionProvider = "gpu" }.UseDirectML);
        Assert.True(new BrainConfiguration { ExecutionProvider = "GPU" }.UseDirectML);
    }

    [Fact]
    public void GetModelSubdirectory_ReturnsCorrectPathForCpu()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "CPU" };

        // Act
        var subdir = config.GetModelSubdirectory();

        // Assert
        Assert.Equal("cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4", subdir);
    }

    [Fact]
    public void GetModelSubdirectory_ReturnsCorrectPathForDirectML()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "DirectML" };

        // Act
        var subdir = config.GetModelSubdirectory();

        // Assert
        Assert.Equal("gpu/gpu-int4-rtn-block-32", subdir);
    }

    [Fact]
    public void GetModelFileNames_ReturnsCorrectNamesForCpu()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "CPU" };

        // Act
        var (onnxFile, onnxDataFile) = config.GetModelFileNames();

        // Assert
        Assert.Equal("model.onnx", onnxFile);
        Assert.Equal("model.onnx.data", onnxDataFile);
    }

    [Fact]
    public void GetModelFileNames_ReturnsCorrectNamesForDirectML()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "DirectML" };

        // Act
        var (onnxFile, onnxDataFile) = config.GetModelFileNames();

        // Assert
        Assert.Equal("model.onnx", onnxFile);
        Assert.Equal("model.onnx.data", onnxDataFile);
    }

    [Fact]
    public void GetProviderDisplayName_ReturnsCorrectNameForCpu()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "CPU" };

        // Act
        var displayName = config.GetProviderDisplayName();

        // Assert
        Assert.Equal("CPU", displayName);
    }

    [Fact]
    public void GetProviderDisplayName_ReturnsCorrectNameForDirectML()
    {
        // Arrange
        var config = new BrainConfiguration { ExecutionProvider = "DirectML" };

        // Act
        var displayName = config.GetProviderDisplayName();

        // Assert
        Assert.Equal("DirectML (GPU)", displayName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void InferenceTimeoutSeconds_AcceptsValidValues(int timeout)
    {
        // Arrange
        var config = new BrainConfiguration { InferenceTimeoutSeconds = timeout };

        // Assert
        Assert.Equal(timeout, config.InferenceTimeoutSeconds);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void MaxSequenceLength_AcceptsValidValues(int length)
    {
        // Arrange
        var config = new BrainConfiguration { MaxSequenceLength = length };

        // Assert
        Assert.Equal(length, config.MaxSequenceLength);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Temperature_AcceptsValidRange(double temp)
    {
        // Arrange
        var config = new BrainConfiguration { Temperature = temp };

        // Assert
        Assert.Equal(temp, config.Temperature);
    }
}
