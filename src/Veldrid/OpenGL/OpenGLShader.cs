﻿using System.Text;
using Veldrid.OpenGLBinding;
using static Veldrid.OpenGL.OpenGLUtil;
using static Veldrid.OpenGLBinding.OpenGLNative;

namespace Veldrid.OpenGL
{
    internal unsafe class OpenGLShader : Shader, OpenGLDeferredResource
    {
        private readonly OpenGLGraphicsDevice _gd;
        private readonly ShaderType _shaderType;
        private readonly StagingBlock _stagingBlock;

        private bool _disposed;
        private string _name;
        private bool _nameChanged;
        public override string Name { get => _name; set { _name = value; _nameChanged = true; } }

		public uint Shader { get; private set; }

		public OpenGLShader(OpenGLGraphicsDevice gd, ShaderStages stage, StagingBlock stagingBlock, string entryPoint)
            : base(stage, entryPoint)
        {
#if VALIDATE_USAGE
            if (stage == ShaderStages.Compute && !gd.Extensions.ComputeShaders)
            {
                if (_gd.BackendType == GraphicsBackend.OpenGLES)
                {
                    throw new VeldridException("Compute shaders require OpenGL ES 3.1.");
                }
                else
                {
                    throw new VeldridException($"Compute shaders require OpenGL 4.3 or ARB_compute_shader.");
                }
            }
#endif
            _gd = gd;
            _shaderType = OpenGLFormats.VdToGLShaderType(stage);
            _stagingBlock = stagingBlock;
        }

        public bool Created { get; private set; }

        public void EnsureResourcesCreated()
        {
            if (!Created)
            {
                CreateGLResources();
            }
            if (_nameChanged)
            {
                _nameChanged = false;
                if (_gd.Extensions.KHR_Debug)
                {
                    SetObjectLabel(ObjectLabelIdentifier.Shader, Shader, _name);
                }
            }
        }

        private void CreateGLResources()
        {
            Shader = glCreateShader(_shaderType);
            CheckLastError();

            byte* textPtr = (byte*)_stagingBlock.Data;
            int length = (int)_stagingBlock.SizeInBytes;
            byte** textsPtr = &textPtr;

            glShaderSource(Shader, 1, textsPtr, &length);
            CheckLastError();

            glCompileShader(Shader);
            CheckLastError();

            int compileStatus;
            glGetShaderiv(Shader, ShaderParameter.CompileStatus, &compileStatus);
            CheckLastError();

            if (compileStatus != 1)
            {
                int infoLogLength;
                glGetShaderiv(Shader, ShaderParameter.InfoLogLength, &infoLogLength);
                CheckLastError();

                byte* infoLog = stackalloc byte[infoLogLength];
                uint returnedInfoLength;
                glGetShaderInfoLog(Shader, (uint)infoLogLength, &returnedInfoLength, infoLog);
                CheckLastError();

                string message = infoLog != default
                    ? Encoding.UTF8.GetString(infoLog, (int)returnedInfoLength)
                    : "<null>";

                throw new VeldridException($"Unable to compile shader code for shader [{_name}] of type {_shaderType}: {message}");
            }

            _gd.StagingMemoryPool.Free(_stagingBlock);
            Created = true;
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
                if (Created)
                {
                    glDeleteShader(Shader);
                    CheckLastError();
                }
                else
                {
                    _gd.StagingMemoryPool.Free(_stagingBlock);
                }
            }
        }
    }
}
