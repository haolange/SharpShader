using global::SharpGPU;

namespace SharpShader.Tests
{
    internal sealed class BindingLayoutTestDevice : RHIDevice
    {
        public Func<RHIBindingTableLayoutDescriptor, RHIBindingTableLayout> LayoutFactory { get; set; } =
            _ => throw new InvalidOperationException("A layout factory is required.");

        public override ERHIBackend BackendType =>
            ERHIBackend.Vulkan;

        public override RHICommandQueue? GetCommandQueue(
            in ERHIPipelineType pipeline,
            in int index)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return null;
        }

        public override RHISwapChain CreateSwapChain(
            in RHISwapChainDescriptor descriptor)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return Unsupported<RHISwapChain>();
        }

        public override RHIFence CreateFence()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return Unsupported<RHIFence>();
        }

        public override RHISemaphore CreateSemaphore()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return Unsupported<RHISemaphore>();
        }

        public override RHIStorageQueue CreateStorageQueue() =>
            Unsupported<RHIStorageQueue>();

        public override RHIQuery CreateQuery(
            in RHIQueryDescriptor descriptor) =>
            Unsupported<RHIQuery>();

        public override RHIHeap CreateHeap(
            in RHIHeapDescription descriptor) =>
            Unsupported<RHIHeap>();

        public override RHIBuffer CreateBuffer(
            in RHIBufferDescriptor descriptor) =>
            Unsupported<RHIBuffer>();

        public override RHITexture CreateTexture(
            in RHITextureDescriptor descriptor) =>
            Unsupported<RHITexture>();

        public override RHICapability QueryRasterAttachmentSupport(
            in RHIRasterAttachmentSupportQuery query) =>
            Unsupported<RHICapability>();

        public override RHICapability QueryFormatSupport(
            in RHIFormatSupportQuery query) =>
            Unsupported<RHICapability>();

        public override RHICapability QueryResolveSupport(
            in RHIResolveSupportQuery query) =>
            Unsupported<RHICapability>();

        public override RHIClockCalibration QueryClockCalibration(
            ERHIPipelineType queue,
            int queueIndex = 0) =>
            Unsupported<RHIClockCalibration>();

        public override RHIRasterAttachmentShaderAbi
            QueryRasterAttachmentShaderAbi(
                in RHIRasterAttachmentShaderAbiDescriptor descriptor) =>
            Unsupported<RHIRasterAttachmentShaderAbi>();

        public override RHISampler CreateSampler(
            in RHISamplerDescriptor descriptor) =>
            Unsupported<RHISampler>();

        public override RHITopLevelAccelStruct
            CreateTopAccelerationStructure(
                in RHITopLevelAccelStructDescriptor descriptor) =>
            Unsupported<RHITopLevelAccelStruct>();

        public override RHIBottomLevelAccelStruct
            CreateBottomAccelerationStructure(
                in RHIBottomLevelAccelStructDescriptor descriptor) =>
            Unsupported<RHIBottomLevelAccelStruct>();

        public override RHIBindingTableLayout
            CreateBindingTableLayout(
                in RHIBindingTableLayoutDescriptor descriptor) =>
            LayoutFactory(descriptor);

        public override RHIBindingTable CreateBindingTable(
            in RHIBindingTableDescriptor descriptor) =>
            Unsupported<RHIBindingTable>();

        public override RHIPipelineLayout CreatePipelineLayout(
            in RHIPipelineLayoutDescriptor descriptor) =>
            Unsupported<RHIPipelineLayout>();

        public override RHIFunction CreateFunction(
            in RHIFunctionDescriptor descriptor) =>
            Unsupported<RHIFunction>();

        public override RHIFunctionLibrary CreateFunctionLibrary(
            in RHIFunctionLibraryDescriptor descriptor) =>
            Unsupported<RHIFunctionLibrary>();

        public override RHIFunctionTable CreateFunctionTable() =>
            Unsupported<RHIFunctionTable>();

        public override RHIComputePipeline CreateComputePipeline(
            in RHIComputePipelineDescriptor descriptor) =>
            Unsupported<RHIComputePipeline>();

        public override RHIRaytracingPipeline
            CreateRaytracingPipeline(
                in RHIRaytracingPipelineDescriptor descriptor) =>
            Unsupported<RHIRaytracingPipeline>();

        public override RHIRasterPipeline CreateRasterPipeline(
            in RHIRasterPipelineDescriptor descriptor) =>
            Unsupported<RHIRasterPipeline>();

        public override RHIPipelineCache CreatePipelineCache() =>
            Unsupported<RHIPipelineCache>();

        public override RHIIndirectCommandLayout CreateIndirectCommandLayout(
            in RHIIndirectCommandLayoutDescriptor descriptor) =>
            Unsupported<RHIIndirectCommandLayout>();

        public override RHIMLPipeline CreateMLPipeline(
            in RHIMLPipelineDescriptor descriptor) =>
            Unsupported<RHIMLPipeline>();

        public override RHIMLBindingTable CreateMLBindingTable(
            in RHIMLBindingTableDescriptor descriptor) =>
            Unsupported<RHIMLBindingTable>();

        public override RHITensor CreateTensor(
            in RHIMLTensorDescriptor descriptor) =>
            Unsupported<RHITensor>();

        public override RHIWorkGraphPipeline
            CreateWorkGraphPipeline(
                in RHIWorkGraphPipelineDescriptor descriptor) =>
            Unsupported<RHIWorkGraphPipeline>();

        private T Unsupported<T>()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            throw new NotSupportedException(
                $"Scripted device does not create {typeof(T).Name}.");
        }
    }
}
