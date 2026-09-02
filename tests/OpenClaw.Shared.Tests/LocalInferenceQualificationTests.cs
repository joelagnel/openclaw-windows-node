using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using System.Reflection;
using System.Runtime.InteropServices;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests;

public class LocalInferenceQualificationTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void CudaVisibleDevicesSelector_FormatsDriverUuidLikeNvml()
    {
        byte[] cudaUuid = Convert.FromHexString("CC66BCA6B5FFDD70995CD81A07ADD980");

        Assert.Equal(
            "GPU-cc66bca6-b5ff-dd70-995c-d81a07add980",
            CudaHostHardwareProbe.ToCudaVisibleDevicesSelector(cudaUuid));
    }

    [Fact]
    public void CudaProbe_LoadsNativeDriverOnlyFromSystem32()
    {
        MethodInfo[] imports = typeof(CudaHostHardwareProbe)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
            .ToArray();

        Assert.NotEmpty(imports);
        Assert.All(imports, method =>
        {
            DefaultDllImportSearchPathsAttribute attribute = Assert.IsType<DefaultDllImportSearchPathsAttribute>(
                method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>());
            Assert.Equal(DllImportSearchPath.System32, attribute.Paths);
        });
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "NVIDIA RTX Spark N1X", LlamaRuntimeCatalog.X64RuntimeId)]
    [InlineData(RuntimeArchitecture.Arm64, "NVIDIA GeForce RTX 5090", LlamaRuntimeCatalog.Arm64RuntimeId)]
    public void Evaluate_RoutesRuntimeByArchitectureWithoutGpuSkuPairing(
        RuntimeArchitecture architecture,
        string gpuName,
        string expectedRuntimeId)
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(architecture, Gpu(gpuName, "GPU-generic", totalGiB: 32, freeGiB: 32)));

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal(expectedRuntimeId, result.Plan?.Runtime.Id);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(LocalModelCatalog.IntermediateContextTokens, result.Plan?.Profile.ContextTokens);
        Assert.Equal(KvCachePrecision.Q8_0, result.Plan?.Profile.KeyCachePrecision);
    }

    [Fact]
    public void Evaluate_UnsetModelChoosesHighestPriorityModelThatFitsTotalCapacity()
    {
        var cases = new[]
        {
            (TotalBytes: 34_190_458_880L, FreeBytes: 32_432_455_680L,
                ModelId: LocalModelCatalog.Qwen38_27BModelId,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0,
                RequiredBytes: 31_253_556_128L),
            (TotalBytes: 24 * GiB, FreeBytes: 24 * GiB,
                ModelId: LocalModelCatalog.Qwen38_27BModelId,
                ContextTokens: LocalModelCatalog.MinimumContextTokens,
                Precision: KvCachePrecision.F16,
                RequiredBytes: 25_322_810_272L),
        };
        foreach (var testCase in cases)
        {
            GpuInfo gpu = Gpu("NVIDIA arbitrary adapter", "GPU-capacity", 1, 1) with
            {
                GpuVisibleMemoryBytes = testCase.TotalBytes,
                FreeGpuVisibleMemoryBytes = testCase.FreeBytes,
            };
            LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
                Hardware(RuntimeArchitecture.X64, gpu));

            Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
            Assert.Equal(testCase.ModelId, result.Plan?.Model.Id);
            Assert.Equal(testCase.ContextTokens, result.Plan?.Profile.ContextTokens);
            Assert.Equal(testCase.Precision, result.Plan?.Profile.KeyCachePrecision);
            Assert.Equal(testCase.RequiredBytes, result.RequiredTotalMemoryBytes);
            Assert.True(result.Plan?.Profile.ContextTokens >= LocalModelCatalog.MinimumContextTokens);
            Assert.Equal(LocalInferenceModelSelectionOrigin.Default, result.Plan?.ModelSelectionOrigin);
        }
    }

    [Fact]
    public void Evaluate_UnsetModelRejects16GiBCapacity()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-16", 16, 16)));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.DoesNotContain(LocalModelCatalog.Models, model => model.Id == "qwen3.5-9b-mtp-q4-k-m");
    }

    [Fact]
    public void Evaluate_Removed16GiBModelIdIsUnknown()
    {
        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, Gpu("NVIDIA arbitrary adapter", "GPU-32", 32, 32)),
            "qwen3.5-9b-mtp-q4-k-m");

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.CatalogSelectionFailed, result.FailureCode);
        Assert.Equal(LocalInferenceSelectionFailureCode.UnknownModel, result.SelectionFailureCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Evaluate_ExplicitModelNeverDowngradesAndReportsExactCapacity()
    {
        var cases = new[]
        {
            (ModelId: LocalModelCatalog.Qwen38_27BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 31_253_556_128L),
            (ModelId: LocalModelCatalog.Qwen35BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 32_532_584_736L),
            (ModelId: LocalModelCatalog.Qwen27BModelId, TotalGiB: 32,
                Status: LocalInferenceEligibilityStatus.Eligible,
                ContextTokens: LocalModelCatalog.IntermediateContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 31_895_889_024L),
            (ModelId: LocalModelCatalog.Qwen35BModelId, TotalGiB: 16,
                Status: LocalInferenceEligibilityStatus.Unsupported,
                ContextTokens: LocalModelCatalog.MinimumContextTokens,
                Precision: KvCachePrecision.Q8_0, RequiredBytes: 27_742_689_568L),
        };
        foreach (var testCase in cases)
        {
            LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
                Hardware(RuntimeArchitecture.X64, Gpu(
                    "NVIDIA arbitrary adapter", "GPU-explicit", testCase.TotalGiB, testCase.TotalGiB)),
                testCase.ModelId);

            Assert.Equal(testCase.Status, result.Status);
            Assert.Equal(testCase.ModelId, result.Plan?.Model.Id);
            Assert.Equal(testCase.ContextTokens, result.Plan?.Profile.ContextTokens);
            Assert.Equal(testCase.Precision, result.Plan?.Profile.KeyCachePrecision);
            Assert.Equal(testCase.RequiredBytes, result.RequiredTotalMemoryBytes);
            Assert.Equal(testCase.TotalGiB * GiB, result.DetectedTotalMemoryBytes);
            if (testCase.Status == LocalInferenceEligibilityStatus.Unsupported)
                Assert.Equal(LocalInferenceEligibilityFailureCode.InsufficientGpuMemory, result.FailureCode);
        }
    }

    [Theory]
    [InlineData(LocalModelCatalog.Qwen35BModelId, 5_120, 512, 2_720, 272, 8)]
    [InlineData(LocalModelCatalog.Qwen38_27BModelId, 16_384, 1_024, 8_704, 544, 8)]
    [InlineData(LocalModelCatalog.Qwen27BModelId, 16_384, 1_024, 8_704, 544, 8)]
    public void GetRequiredMemoryBytes_IncludesRecipeKvCacheAndWorkspace(
        string modelId,
        long expectedF16CacheMiB,
        long expectedF16DraftCacheMiB,
        long expectedQ8CacheMiB,
        long expectedQ8DraftCacheMiB,
        long expectedQ8WorkspaceGiB)
    {
        LocalModelInfo model = LocalModelCatalog.Find(modelId)!;
        LocalInferenceRunProfile f16Profile = LocalModelCatalog.GetProfiles(model)[0];
        LocalInferenceRunProfile q8Profile = LocalModelCatalog.GetProfiles(model)[1];

        long f16Required = LocalInferenceEligibility.GetRequiredMemoryBytes(model, f16Profile);
        long q8Required = LocalInferenceEligibility.GetRequiredMemoryBytes(model, q8Profile);

        Assert.Equal(
            model.Weights.SizeBytes +
            (expectedF16CacheMiB + expectedF16DraftCacheMiB) * 1024 * 1024 +
            LocalModelCatalog.RuntimeWorkspaceReserveBytes,
            f16Required);
        Assert.Equal(
            model.Weights.SizeBytes +
            (expectedQ8CacheMiB + expectedQ8DraftCacheMiB) * 1024 * 1024 +
            expectedQ8WorkspaceGiB * GiB,
            q8Required);
    }

    [Fact]
    public void Evaluate_RanksEligibleBeforeBusyAndUnsupportedAdapters()
    {
        GpuInfo unsupported = Gpu("NVIDIA incompatible", "GPU-old", 48, 48) with { CudaMajorVersion = 12 };
        GpuInfo busy = Gpu("NVIDIA busy", "GPU-busy", 32, 1);
        GpuInfo eligible = Gpu("NVIDIA ready", "GPU-ready", 32, 32);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, unsupported, busy, eligible),
            LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal(LocalInferenceEligibilityStatus.Eligible, result.Status);
        Assert.Equal("GPU-ready", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_IgnoresLegacySharedMemoryFields()
    {
        GpuInfo gpu = Gpu("NVIDIA generic unified memory", "GPU-shared", 8, 8) with
        {
            SharedGpuMemoryBytes = 16 * GiB,
            FreeSharedGpuMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.Arm64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, result.Plan?.Model.Id);
        Assert.Equal(8 * GiB, result.DetectedTotalMemoryBytes);
        Assert.Equal(8 * GiB, result.AvailableFreeMemoryBytes);
    }

    [Fact]
    public void Evaluate_RanksEligibleAdaptersByFreeThenTotalThenUuid()
    {
        GpuInfo moreTotal = Gpu("NVIDIA total", "GPU-z", 64, 42);
        GpuInfo moreFree = Gpu("NVIDIA free", "GPU-b", 48, 43);
        GpuInfo sameFreeAndTotalLowerUuid = Gpu("NVIDIA tie", "GPU-a", 48, 43);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, moreTotal, moreFree, sameFreeAndTotalLowerUuid),
            LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal("GPU-a", result.SelectedGpu?.StableId);
    }

    [Theory]
    [InlineData(null, 13, LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)]
    [InlineData("GPU-cuda", 12, LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow)]
    public void Evaluate_RequiresStableIdAndCompatibleCuda(
        string? stableId,
        int cudaMajor,
        LocalInferenceEligibilityFailureCode expectedFailure)
    {
        GpuInfo gpu = Gpu("NVIDIA arbitrary", stableId, 32, 32) with
        {
            CudaMajorVersion = cudaMajor,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(expectedFailure, result.FailureCode);
    }

    [Fact]
    public void Evaluate_IdentifiedGpuWithoutMemoryIsIncompleteRatherThanAbsent()
    {
        GpuInfo gpu = Gpu("NVIDIA identified", "GPU-identified", 32, 32) with
        {
            GpuVisibleMemoryBytes = null,
            FreeGpuVisibleMemoryBytes = null,
        };

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(
            Hardware(RuntimeArchitecture.X64, gpu));

        Assert.Equal(LocalInferenceEligibilityStatus.Unsupported, result.Status);
        Assert.Equal(LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete, result.FailureCode);
        Assert.Equal("GPU-identified", result.SelectedGpu?.StableId);
    }

    [Fact]
    public void Evaluate_ReportsNoNvidiaGpu()
    {
        var hardware = new HostHardwareInfo(
            RuntimeArchitecture.X64,
            null,
            null,
            [new GpuInfo(GpuVendor.Amd, "AMD GPU")],
            false);

        LocalInferenceEligibilityResult result = LocalInferenceEligibility.Evaluate(hardware);

        Assert.Equal(LocalInferenceSelectionFailureCode.NoNvidiaGpu, result.SelectionFailureCode);
    }

    private static HostHardwareInfo Hardware(RuntimeArchitecture architecture, params GpuInfo[] gpus) =>
        new(architecture, 64 * GiB, 48 * GiB, gpus, false);

    private static GpuInfo Gpu(
        string name,
        string? stableId,
        long totalGiB,
        long freeGiB) =>
        new(
            GpuVendor.Nvidia,
            name,
            totalGiB * GiB,
            freeGiB * GiB,
            DriverVersion: "616.30",
            CudaMajorVersion: 13,
            StableId: stableId);

}
