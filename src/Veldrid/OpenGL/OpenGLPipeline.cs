using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Veldrid.OpenGLBinding;
using static Veldrid.OpenGL.OpenGLUtil;
using static Veldrid.OpenGLBinding.OpenGLNative;

namespace Veldrid.OpenGL
{
    internal unsafe class OpenGLPipeline : Pipeline, OpenGLDeferredResource
    {
        private const uint GL_INVALID_INDEX = 0xFFFFFFFF;
        private readonly OpenGLGraphicsDevice _gd;

#if !VALIDATE_USAGE
        public ResourceLayout[] ResourceLayouts { get; }
#endif

        // Graphics Pipeline
        public Shader[] GraphicsShaders { get; }
        public VertexLayoutDescription[] VertexLayouts { get; }
        public BlendStateDescription BlendState { get; }
        public DepthStencilStateDescription DepthStencilState { get; }
        public RasterizerStateDescription RasterizerState { get; }
        public PrimitiveTopology PrimitiveTopology { get; }

        // Compute Pipeline
        public override bool IsComputePipeline { get; }
        public Shader ComputeShader { get; }

        private bool _disposed;

        private SetBindingsInfo[] _setInfos;

        public int[] VertexStrides { get; }

		public uint Program { get; private set; }

		public uint GetUniformBufferCount(uint setSlot) => _setInfos[setSlot].UniformBufferCount;
        public uint GetShaderStorageBufferCount(uint setSlot) => _setInfos[setSlot].ShaderStorageBufferCount;

        public override string Name { get; set; }

        public OpenGLPipeline(OpenGLGraphicsDevice gd, ref GraphicsPipelineDescription description)
            : base(ref description)
        {
            _gd = gd;
            GraphicsShaders = Util.ShallowClone(description.ShaderSet.Shaders);
            VertexLayouts = Util.ShallowClone(description.ShaderSet.VertexLayouts);
            BlendState = description.BlendState.ShallowClone();
            DepthStencilState = description.DepthStencilState;
            RasterizerState = description.RasterizerState;
            PrimitiveTopology = description.PrimitiveTopology;

            int numVertexBuffers = description.ShaderSet.VertexLayouts.Length;
            VertexStrides = new int[numVertexBuffers];
            for (int i = 0; i < numVertexBuffers; i++)
            {
                VertexStrides[i] = (int)description.ShaderSet.VertexLayouts[i].Stride;
            }

#if !VALIDATE_USAGE
            ResourceLayouts = Util.ShallowClone(description.ResourceLayouts);
#endif
        }

        public OpenGLPipeline(OpenGLGraphicsDevice gd, ref ComputePipelineDescription description)
            : base(ref description)
        {
            _gd = gd;
            IsComputePipeline = true;
            ComputeShader = description.ComputeShader;
            VertexStrides = Array.Empty<int>();
#if !VALIDATE_USAGE
            ResourceLayouts = Util.ShallowClone(description.ResourceLayouts);
#endif
        }

        public bool Created { get; private set; }

        public void EnsureResourcesCreated()
        {
            if (!Created)
            {
                CreateGLResources();
            }
        }

        private void CreateGLResources()
        {
            if (!IsComputePipeline)
            {
                CreateGraphicsGLResources();
            }
            else
            {
                CreateComputeGLResources();
            }

            Created = true;
        }

        private void CreateGraphicsGLResources()
        {
            Program = glCreateProgram();
            CheckLastError();
            foreach (Shader stage in GraphicsShaders)
            {
                OpenGLShader glShader = Util.AssertSubtype<Shader, OpenGLShader>(stage);
                glShader.EnsureResourcesCreated();
                glAttachShader(Program, glShader.Shader);
                CheckLastError();
            }

            uint slot = 0;
            foreach (VertexLayoutDescription layoutDesc in VertexLayouts)
            {
                for (int i = 0; i < layoutDesc.Elements.Length; i++)
                {
                    ReadOnlySpan<char> elementName = layoutDesc.Elements[i].Name;
                    int byteCount = Encoding.UTF8.GetByteCount(elementName) + 1;
                    Span<byte> elementNamePtr = stackalloc byte[byteCount];
                    int bytesWritten = Encoding.UTF8.GetBytes(elementName, elementNamePtr);
                    Debug.Assert(bytesWritten == byteCount - 1);
                    elementNamePtr[byteCount - 1] = 0; // Add null terminator.

                    fixed (byte* bytePtr = elementNamePtr)
                    {
                        glBindAttribLocation(Program, slot, bytePtr);
                        CheckLastError();
                    }

                    slot++;
                }
            }

            glLinkProgram(Program);
            CheckLastError();

#if DEBUG && GL_VALIDATE_VERTEX_INPUT_ELEMENTS
            slot = 0;
            foreach (VertexLayoutDescription layoutDesc in VertexLayouts)
            {
                for (int i = 0; i < layoutDesc.Elements.Length; i++)
                {
                    ReadOnlySpan<char> elementName = layoutDesc.Elements[i].Name;
                    int byteCount = Encoding.UTF8.GetByteCount(elementName) + 1;
                    Span<byte> elementNamePtr = stackalloc byte[byteCount];
                    int bytesWritten = Encoding.UTF8.GetBytes(elementName, elementNamePtr);
                    Debug.Assert(bytesWritten == byteCount - 1);
                    elementNamePtr[byteCount - 1] = 0; // Add null terminator.

                    fixed (byte* bytePtr = elementNamePtr)
                    {
                        int location = glGetAttribLocation(_program, bytePtr);
                        if (location == -1)
                        {
                            throw new VeldridException("There was no attribute variable with the name " + layoutDesc.Elements[i].Name);
                        }
                    }

                    slot ++;
                }
            }
#endif

            int linkStatus;
            glGetProgramiv(Program, GetProgramParameterName.LinkStatus, &linkStatus);
            CheckLastError();
            if (linkStatus == 1)
            {
                Span<byte> infoLog = stackalloc byte[4096];
                uint bytesWritten;
                fixed (byte* infoLogPtr = infoLog)
                {
                    glGetProgramInfoLog(Program, 4096, &bytesWritten, infoLogPtr);
                    CheckLastError();
                }
                string log = Encoding.UTF8.GetString(infoLog);
                throw new VeldridException($"Error linking GL program: {log}");
            }

            foreach (Shader stage in GraphicsShaders)
            {
                OpenGLShader glShader = Util.AssertSubtype<Shader, OpenGLShader>(stage);
                glDetachShader(_program, glShader.Shader);
                CheckLastError();
            }

            ProcessResourceSetLayouts(ResourceLayouts);
        }

        private void ProcessResourceSetLayouts(ResourceLayout[] layouts)
        {
            int resourceLayoutCount = layouts.Length;
            _setInfos = new SetBindingsInfo[resourceLayoutCount];
            int relativeTextureIndex = -1;
            int relativeImageIndex = -1;
            uint storageBlockIndex = 0; // Tracks OpenGL ES storage buffers.
            for (uint setSlot = 0; setSlot < resourceLayoutCount; setSlot++)
            {
                ResourceLayout setLayout = layouts[setSlot];
                OpenGLResourceLayout glSetLayout = Util.AssertSubtype<ResourceLayout, OpenGLResourceLayout>(setLayout);
                Span<ResourceLayoutElementDescription> resources = glSetLayout.Elements;

                Dictionary<uint, OpenGLUniformBinding> uniformBindings = new Dictionary<uint, OpenGLUniformBinding>();
                Dictionary<uint, OpenGLTextureBindingSlotInfo> textureBindings = new Dictionary<uint, OpenGLTextureBindingSlotInfo>();
                Dictionary<uint, OpenGLSamplerBindingSlotInfo> samplerBindings = new Dictionary<uint, OpenGLSamplerBindingSlotInfo>();
                Dictionary<uint, OpenGLShaderStorageBinding> storageBufferBindings = new Dictionary<uint, OpenGLShaderStorageBinding>();

                List<int> samplerTrackedRelativeTextureIndices = new List<int>();
                for (uint i = 0; i < resources.Length; i++)
                {
                    ResourceLayoutElementDescription resource = resources[(int)i];
                    if (resource.Kind == ResourceKind.UniformBuffer)
                    {
                        ReadOnlySpan<char> resourceName = resource.Name;
                        int byteCount = Encoding.UTF8.GetByteCount(resourceName) + 1;
                        Span<byte> resourceNamePtr = stackalloc byte[byteCount];
                        int bytesWritten = Encoding.UTF8.GetBytes(resourceName, resourceNamePtr);
                        Debug.Assert(bytesWritten == byteCount - 1);
                        resourceNamePtr[byteCount - 1] = 0; // Add null terminator.

                        fixed (byte* bytePtr = resourceNamePtr)
                        {
                            uint blockIndex = glGetUniformBlockIndex(Program, bytePtr);
                            CheckLastError();

                            if (blockIndex != GL_INVALID_INDEX)
                            {
                                int blockSize;
                                glGetActiveUniformBlockiv(Program, blockIndex, ActiveUniformBlockParameter.UniformBlockDataSize, &blockSize);
                                CheckLastError();
                                uniformBindings[i] = new OpenGLUniformBinding(Program, blockIndex, (uint)blockSize);
                            }
                        }
                    }
                    else if (resource.Kind == ResourceKind.TextureReadOnly)
                    {
                        ReadOnlySpan<char> resourceName = resource.Name;
                        int byteCount = Encoding.UTF8.GetByteCount(resourceName) + 1;
                        Span<byte> resourceNamePtr = stackalloc byte[byteCount];
                        int bytesWritten = Encoding.UTF8.GetBytes(resourceName, resourceNamePtr);
                        Debug.Assert(bytesWritten == byteCount - 1);
                        resourceNamePtr[byteCount - 1] = 0; // Add null terminator.

                        fixed (byte* bytePtr = resourceNamePtr)
                        {
                            int location = glGetUniformLocation(Program, bytePtr);
                            CheckLastError();
                            relativeTextureIndex++;
                            textureBindings[i] = new OpenGLTextureBindingSlotInfo() { RelativeIndex = relativeTextureIndex, UniformLocation = location };
                            samplerTrackedRelativeTextureIndices.Add(relativeTextureIndex);
                        }
                    }
                    else if (resource.Kind == ResourceKind.TextureReadWrite)
                    {
                        ReadOnlySpan<char> resourceName = resource.Name;
                        int byteCount = Encoding.UTF8.GetByteCount(resourceName) + 1;
                        Span<byte> resourceNamePtr = stackalloc byte[byteCount];
                        int bytesWritten = Encoding.UTF8.GetBytes(resourceName, resourceNamePtr);
                        Debug.Assert(bytesWritten == byteCount - 1);
                        resourceNamePtr[byteCount - 1] = 0; // Add null terminator.

                        fixed (byte* bytePtr = resourceNamePtr)
                        {
                            int location = glGetUniformLocation(Program, bytePtr);
                            CheckLastError();
                            relativeImageIndex++;
                            textureBindings[i] = new OpenGLTextureBindingSlotInfo() { RelativeIndex = relativeImageIndex, UniformLocation = location };
                        }
                    }
                    else if (resource.Kind == ResourceKind.StructuredBufferReadOnly
                        || resource.Kind == ResourceKind.StructuredBufferReadWrite)
                    {
                        uint storageBlockBinding;
                        if (_gd.BackendType == GraphicsBackend.OpenGL)
                        {
                            ReadOnlySpan<char> resourceName = resource.Name;
                            int byteCount = Encoding.UTF8.GetByteCount(resourceName) + 1;
                            Span<byte> resourceNamePtr = stackalloc byte[byteCount];
                            int bytesWritten = Encoding.UTF8.GetBytes(resourceName, resourceNamePtr);
                            Debug.Assert(bytesWritten == byteCount - 1);
                            resourceNamePtr[byteCount - 1] = 0; // Add null terminator.

                            fixed (byte* bytePtr = resourceNamePtr)
                            {
                                storageBlockBinding = glGetProgramResourceIndex(
                                Program,
                                ProgramInterface.ShaderStorageBlock,
                                bytePtr);
                                CheckLastError();
                            }
                        }
                        else
                        {
                            storageBlockBinding = storageBlockIndex;
                            storageBlockIndex++;
                        }

                        storageBufferBindings[i] = new OpenGLShaderStorageBinding(storageBlockBinding);
                    }
                    else
                    {
                        Debug.Assert(resource.Kind == ResourceKind.Sampler);

                        samplerBindings[i] = new OpenGLSamplerBindingSlotInfo()
                        {
                            RelativeIndices = samplerTrackedRelativeTextureIndices.ToArray()
                        };
                        samplerTrackedRelativeTextureIndices.Clear();
                    }
                }

                _setInfos[setSlot] = new SetBindingsInfo(uniformBindings, textureBindings, samplerBindings, storageBufferBindings);
            }
        }

        private void CreateComputeGLResources()
        {
            Program = glCreateProgram();
            CheckLastError();
            OpenGLShader glShader = Util.AssertSubtype<Shader, OpenGLShader>(ComputeShader);
            glShader.EnsureResourcesCreated();
            glAttachShader(Program, glShader.Shader);
            CheckLastError();

            glLinkProgram(Program);
            CheckLastError();

            int linkStatus;
            glGetProgramiv(Program, GetProgramParameterName.LinkStatus, &linkStatus);
            CheckLastError();
            if (linkStatus != 1)
            {
                Span<byte> infoLog = stackalloc byte[4096];
                uint bytesWritten;
                fixed (byte* infoLogPtr = infoLog)
                {
                    glGetProgramInfoLog(Program, 4096, &bytesWritten, infoLogPtr);
                    CheckLastError();
                }
                string log = Encoding.UTF8.GetString(infoLog);
                throw new VeldridException($"Error linking GL program: {log}");
            }

            ProcessResourceSetLayouts(ResourceLayouts);
        }

        public bool GetUniformBindingForSlot(uint set, uint slot, out OpenGLUniformBinding binding)
        {
            Debug.Assert(_setInfos != null, "EnsureResourcesCreated must be called before accessing resource set information.");
            SetBindingsInfo setInfo = _setInfos[set];
            return setInfo.GetUniformBindingForSlot(slot, out binding);
        }

        public bool GetTextureBindingInfo(uint set, uint slot, out OpenGLTextureBindingSlotInfo binding)
        {
            Debug.Assert(_setInfos != null, "EnsureResourcesCreated must be called before accessing resource set information.");
            SetBindingsInfo setInfo = _setInfos[set];
            return setInfo.GetTextureBindingInfo(slot, out binding);
        }

        public bool GetSamplerBindingInfo(uint set, uint slot, out OpenGLSamplerBindingSlotInfo binding)
        {
            Debug.Assert(_setInfos != null, "EnsureResourcesCreated must be called before accessing resource set information.");
            SetBindingsInfo setInfo = _setInfos[set];
            return setInfo.GetSamplerBindingInfo(slot, out binding);
        }

        public bool GetStorageBufferBindingForSlot(uint set, uint slot, out OpenGLShaderStorageBinding binding)
        {
            Debug.Assert(_setInfos != null, "EnsureResourcesCreated must be called before accessing resource set information.");
            SetBindingsInfo setInfo = _setInfos[set];
            return setInfo.GetStorageBufferBindingForSlot(slot, out binding);
        }

        public override void Dispose()
        {
            _gd.EnqueueDisposal(this);
        }

        public void DestroyGLResources()
        {
            if (!_disposed)
            {
                _disposed = true;
                glDeleteProgram(Program);
                CheckLastError();
            }
        }
    }

    internal struct SetBindingsInfo
    {
        private readonly Dictionary<uint, OpenGLUniformBinding> _uniformBindings;
        private readonly Dictionary<uint, OpenGLTextureBindingSlotInfo> _textureBindings;
        private readonly Dictionary<uint, OpenGLSamplerBindingSlotInfo> _samplerBindings;
        private readonly Dictionary<uint, OpenGLShaderStorageBinding> _storageBufferBindings;

        public uint UniformBufferCount { get; }
        public uint ShaderStorageBufferCount { get; }

        public SetBindingsInfo(
            Dictionary<uint, OpenGLUniformBinding> uniformBindings,
            Dictionary<uint, OpenGLTextureBindingSlotInfo> textureBindings,
            Dictionary<uint, OpenGLSamplerBindingSlotInfo> samplerBindings,
            Dictionary<uint, OpenGLShaderStorageBinding> storageBufferBindings)
        {
            _uniformBindings = uniformBindings;
            UniformBufferCount = (uint)uniformBindings.Count;
            _textureBindings = textureBindings;
            _samplerBindings = samplerBindings;
            _storageBufferBindings = storageBufferBindings;
            ShaderStorageBufferCount = (uint)storageBufferBindings.Count;
        }

        public bool GetTextureBindingInfo(uint slot, out OpenGLTextureBindingSlotInfo binding)
        {
            return _textureBindings.TryGetValue(slot, out binding);
        }

        public bool GetSamplerBindingInfo(uint slot, out OpenGLSamplerBindingSlotInfo binding)
        {
            return _samplerBindings.TryGetValue(slot, out binding);
        }

        public bool GetUniformBindingForSlot(uint slot, out OpenGLUniformBinding binding)
        {
            return _uniformBindings.TryGetValue(slot, out binding);
        }

        public bool GetStorageBufferBindingForSlot(uint slot, out OpenGLShaderStorageBinding binding)
        {
            return _storageBufferBindings.TryGetValue(slot, out binding);
        }
    }

    internal struct OpenGLTextureBindingSlotInfo
    {
        /// <summary>
        /// The relative index of this binding with relation to the other textures used by a shader.
        /// Generally, this is the texture unit that the binding will be placed into.
        /// </summary>
        public int RelativeIndex;

        /// <summary>
        /// The uniform location of the binding in the shader program.
        /// </summary>
        public int UniformLocation;
    }

    internal struct OpenGLSamplerBindingSlotInfo
    {
        /// <summary>
        /// The relative indices of this binding with relation to the other textures used by a shader.
        /// Generally, these are the texture units that the sampler will be bound to.
        /// </summary>
        public int[] RelativeIndices;
    }

    internal class OpenGLUniformBinding
    {
        public uint Program { get; }
        public uint BlockLocation { get; }
        public uint BlockSize { get; }

        public OpenGLUniformBinding(uint program, uint blockLocation, uint blockSize)
        {
            Program = program;
            BlockLocation = blockLocation;
            BlockSize = blockSize;
        }
    }

    internal class OpenGLShaderStorageBinding
    {
        public uint StorageBlockBinding { get; }

        public OpenGLShaderStorageBinding(uint storageBlockBinding)
        {
            StorageBlockBinding = storageBlockBinding;
        }
    }
}
