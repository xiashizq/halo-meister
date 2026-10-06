using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinRT;

namespace HaloMeister.App.Pages;

/// <summary>
/// Direct3D 11 viewport. The mesh is uploaded once; orbiting only rewrites the camera
/// constant buffer, which is the same split Baboon uses between GPU buffers and view uniforms.
/// </summary>
public sealed class GpuModelViewport : IDisposable
{
    private const string ShaderSource = """
        #pragma pack_matrix(row_major)
        cbuffer Frame : register(b0)
        {
            float4x4 ViewProj;
            float4x4 View;
            float4 Albedo;
            float Unlit;
            float Skinning;
            float AnimFrame;
            float BoneCount;
            float FrameCount;
            float Pad0;
            float Pad1;
            float Pad2;
        };

        StructuredBuffer<float4> ClipRows : register(t0);

        struct VSIn
        {
            float3 Position : POSITION;
            float3 Normal : NORMAL;
            uint4 Joints : JOINTS;
            float4 Weights : WEIGHTS;
        };

        struct VSOut
        {
            float4 Position : SV_Position;
            float3 Normal : NORMAL;
        };

        float3 ClipAt(uint frame, uint joint, float4 hom)
        {
            uint base = (frame * (uint)BoneCount + joint) * 3;
            return float3(
                dot(ClipRows[base], hom),
                dot(ClipRows[base + 1], hom),
                dot(ClipRows[base + 2], hom));
        }

        float3 Skin(float3 value, float w, uint4 joints, float4 weights)
        {
            if (Skinning < 0.5 || BoneCount < 0.5 || dot(weights, float4(1, 1, 1, 1)) < 0.0001)
                return value;
            float last = max(FrameCount - 1.0, 0.0);
            float frame = clamp(AnimFrame, 0.0, last);
            uint frameA = (uint)frame;
            uint frameB = min(frameA + 1, (uint)last);
            float blend = frame - frameA;
            float4 hom = float4(value, w);
            float3 skinned = 0;
            [unroll]
            for (int i = 0; i < 4; i++)
            {
                if (weights[i] <= 0.0 || joints[i] >= (uint)BoneCount)
                    continue;
                float3 at = ClipAt(frameA, joints[i], hom);
                float3 bt = ClipAt(frameB, joints[i], hom);
                skinned += weights[i] * lerp(at, bt, blend);
            }
            return skinned;
        }

        VSOut VS(VSIn input)
        {
            VSOut output;
            float3 position = Skin(input.Position, 1, input.Joints, input.Weights);
            float3 normal = Skin(input.Normal, 0, input.Joints, input.Weights);
            output.Position = mul(float4(position, 1), ViewProj);
            output.Normal = mul(float4(normal, 0), View).xyz;
            return output;
        }

        float3 ToSrgb(float3 color)
        {
            return pow(saturate(color), 1.0 / 2.2);
        }

        float4 PS(VSOut input) : SV_Target
        {
            if (Unlit > 0.5)
                return float4(Albedo.rgb, 1);
            float3 normal = normalize(input.Normal);
            float key = max(dot(normal, normalize(float3(-0.35, -0.55, 0.76))), 0);
            float fill = max(dot(normal, normalize(float3(0.72, 0.22, 0.36))), 0);
            float rim = pow(saturate(1.0 - abs(normal.y)), 2);
            float overhead = saturate(normal.z * 0.5 + 0.5);
            float shade = clamp(0.42 + key * 0.46 + fill * 0.16 + rim * 0.10 + overhead * 0.08, 0.32, 1.22);
            float3 lit = Albedo.rgb * shade + key * key * (22.0 / 255.0);
            return float4(ToSrgb(lit), 1);
        }
        """;

    private readonly InputElementDescription[] _layout =
    [
        new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
        new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
        new("JOINTS", 0, Format.R16G16B16A16_UInt, 24, 0),
        new("WEIGHTS", 0, Format.R32G32B32A32_Float, 32, 0),
    ];

    private const int VertexStride = 48;

    private SwapChainPanel? _panel;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain2? _swapChain;
    private ISwapChainPanelNative? _panelNative;
    private ID3D11RenderTargetView? _color;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _depth;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _constants;
    private ID3D11Buffer? _clipBuffer;
    private ID3D11ShaderResourceView? _clipView;
    private ID3D11Buffer? _dummyClip;
    private ID3D11ShaderResourceView? _dummyClipView;
    private float _animFrame;
    private float _boneCount;
    private float _frameCount;
    private ID3D11Buffer? _meshVertices;
    private ID3D11Buffer? _meshIndices;
    private ID3D11Buffer? _gridVertices;
    private ID3D11RasterizerState? _rasterizer;
    private ID3D11DepthStencilState? _depthState;
    private int _meshIndexCount;
    private int _gridVertexCount;
    private float _floor;
    private bool _disposed;
    private bool _skinning;

    public void Attach(SwapChainPanel panel)
    {
        if (_panel is not null)
            return;
        _panel = panel;
        panel.SizeChanged += (_, _) =>
        {
            Resize();
            Render(_last);
        };
        panel.CompositionScaleChanged += (_, _) =>
        {
            Resize();
            Render(_last);
        };
        CreateDevice();
        if (_pendingMesh is not null)
            Upload(_pendingMesh);
        Render(_last);
    }

    public void Restore()
    {
        if (_disposed || _panel is null || _panelNative is null || _swapChain is null)
            return;
        float scaleX = MathF.Max(1f, _panel.CompositionScaleX);
        float scaleY = MathF.Max(1f, _panel.CompositionScaleY);
        _swapChain.MatrixTransform = new Matrix3x2(1f / scaleX, 0, 0, 1f / scaleY, 0, 0);
        _panelNative.SetSwapChain(_swapChain);
        Resize();
        Render(_last);
    }

    public void SetMesh(float[] positions, float[] normals, int[] indices, Vector3 albedo, ushort[] joints, float[] weights)
    {
        var mesh = new MeshUpload(positions, normals, indices, albedo, joints, weights);
        _pendingMesh = mesh;
        if (_device is not null)
            Upload(mesh);
    }

    public void SetClip(float[] rows, int bones, int frames)
    {
        _animFrame = 0;
        ReleaseClip();
        int needed = bones * frames * 12;
        if (_device is null || bones <= 0 || frames <= 0 || rows.Length < needed)
        {
            _skinning = false;
            _boneCount = 0;
            _frameCount = 0;
            return;
        }
        float[] clip = rows.Length == needed ? rows : rows[..needed];
        _clipBuffer = _device.CreateBuffer(clip, new BufferDescription
        {
            ByteWidth = (uint)(needed * sizeof(float)),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 16,
        });
        _clipView = _device.CreateShaderResourceView(_clipBuffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = (uint)(needed / 4),
            },
        });
        _boneCount = bones;
        _frameCount = frames;
        _skinning = true;
    }

    public void SetAnimFrame(float frame) => _animFrame = frame;

    public void Render(ViewFrame frame)
    {
        _last = frame;
        if (_disposed || _context is null || _swapChain is null || _color is null || _depth is null)
            return;
        if (!TryPixelSize(out int width, out int height))
            return;

        float aspect = width / (float)Math.Max(1, height);
        float cp = MathF.Cos(frame.Pitch);
        float sp = MathF.Sin(frame.Pitch);
        float cy = MathF.Cos(frame.Yaw);
        float sy = MathF.Sin(frame.Yaw);
        Vector3 target = frame.Center + frame.Pan;
        Vector3 eye = target + new Vector3(frame.Distance * cp * sy, frame.Distance * cp * cy, frame.Distance * sp);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitZ);
        float near = MathF.Max(0.05f, frame.Radius * 0.02f);
        float far = MathF.Max(near + 1f, frame.Distance + frame.Radius * 8f);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, aspect, near, far);
        var constants = new FrameConstants
        {
            ViewProj = view * projection,
            View = view,
            Albedo = new Vector4(frame.Albedo, 1),
            Unlit = 0,
            Skinning = 0,
            AnimFrame = _animFrame,
            BoneCount = _boneCount,
            FrameCount = _frameCount,
        };
        _context.UpdateSubresource(in constants, _constants!);
        _context.OMSetRenderTargets(_color, _depth);
        _context.RSSetViewport(new Viewport(width, height));
        _context.RSSetState(_rasterizer);
        _context.OMSetDepthStencilState(_depthState);
        _context.ClearRenderTargetView(_color, new Color4(228 / 255f, 238 / 255f, 244 / 255f, 1));
        _context.ClearDepthStencilView(_depth, DepthStencilClearFlags.Depth, 1, 0);
        _context.IASetInputLayout(_inputLayout);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.VSSetShaderResource(0, _skinning ? _clipView : _dummyClipView);

        if (_gridVertices is not null && _gridVertexCount > 0)
        {
            constants.Albedo = new Vector4(0.62f, 0.67f, 0.72f, 1);
            constants.Unlit = 1;
            constants.Skinning = 0;
            constants.BoneCount = 0;
            _context.UpdateSubresource(in constants, _constants!);
            _context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            _context.IASetVertexBuffer(0, _gridVertices, VertexStride);
            _context.Draw((uint)_gridVertexCount, 0);
        }

        if (_meshVertices is not null && _meshIndices is not null && _meshIndexCount > 0)
        {
            constants.Albedo = new Vector4(frame.Albedo, 1);
            constants.Unlit = 0;
            constants.Skinning = _skinning ? 1 : 0;
            constants.AnimFrame = _animFrame;
            constants.BoneCount = _boneCount;
            constants.FrameCount = _frameCount;
            _context.UpdateSubresource(in constants, _constants!);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetVertexBuffer(0, _meshVertices, VertexStride);
            _context.IASetIndexBuffer(_meshIndices, Format.R32_UInt, 0);
            _context.DrawIndexed((uint)_meshIndexCount, 0, 0);
        }

        _swapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _meshVertices?.Dispose();
        _meshIndices?.Dispose();
        _gridVertices?.Dispose();
        ReleaseViews();
        _panelNative?.SetSwapChain(null);
        _panelNative?.Dispose();
        _swapChain?.Dispose();
        _constants?.Dispose();
        ReleaseClip();
        _dummyClipView?.Dispose();
        _dummyClip?.Dispose();
        _inputLayout?.Dispose();
        _vertexShader?.Dispose();
        _pixelShader?.Dispose();
        _rasterizer?.Dispose();
        _depthState?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    private MeshUpload? _pendingMesh;
    private ViewFrame _last = new(Vector3.Zero, Vector3.Zero, 0.8f, 0.28f, 4, 1, new Vector3(0.73f, 0.84f, 0.80f));

    private void CreateDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device device,
            out FeatureLevel _,
            out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;
        ReadOnlySpan<byte> vertexByteCode = Compile(ShaderSource, "VS", "vs_5_0");
        ReadOnlySpan<byte> pixelByteCode = Compile(ShaderSource, "PS", "ps_5_0");
        _vertexShader = device.CreateVertexShader(vertexByteCode);
        _pixelShader = device.CreatePixelShader(pixelByteCode);
        _inputLayout = device.CreateInputLayout(_layout, vertexByteCode);
        _constants = device.CreateBuffer((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default);
        float[] dummy = [1, 0, 0, 0];
        _dummyClip = device.CreateBuffer(dummy, new BufferDescription
        {
            ByteWidth = 16,
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 16,
        });
        _dummyClipView = device.CreateShaderResourceView(_dummyClip, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = 1 },
        });
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });
        _depthState = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Less,
        });
        Resize();
    }

    private void Resize()
    {
        if (_device is null || _panel is null || !TryPixelSize(out int width, out int height))
            return;
        ReleaseViews();
        if (_swapChain is null)
            CreateSwapChain(width, height);
        else
            _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);

        using ID3D11Texture2D back = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _color = _device.CreateRenderTargetView(back);
        var depthDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        };
        _depthTexture = _device.CreateTexture2D(in depthDesc);
        _depth = _device.CreateDepthStencilView(_depthTexture);
    }

    private void CreateSwapChain(int width, int height)
    {
        using IDXGIDevice dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out IDXGIAdapter adapter).CheckError();
        using IDXGIAdapter ownedAdapter = adapter;
        using IDXGIFactory2 factory = ownedAdapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
        };
        using IDXGISwapChain1 created = factory.CreateSwapChainForComposition(_device, description, null);
        _swapChain = created.QueryInterface<IDXGISwapChain2>();
        Guid iid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
        Result result = ((IWinRTObject)_panel!).NativeObject.TryAs(iid, out nint nativePointer);
        result.CheckError();
        _panelNative = new ISwapChainPanelNative(nativePointer);
        _panelNative.SetSwapChain(_swapChain).CheckError();
        float scaleX = MathF.Max(1f, _panel.CompositionScaleX);
        float scaleY = MathF.Max(1f, _panel.CompositionScaleY);
        _swapChain.MatrixTransform = new Matrix3x2(1f / scaleX, 0, 0, 1f / scaleY, 0, 0);
    }

    private void Upload(MeshUpload mesh)
    {
        if (_device is null)
            return;
        _meshVertices?.Dispose();
        _meshIndices?.Dispose();
        _gridVertices?.Dispose();
        _meshVertices = null;
        _meshIndices = null;
        _gridVertices = null;
        _meshIndexCount = 0;
        _gridVertexCount = 0;
        int vertices = mesh.Positions.Length / 3;
        if (vertices == 0 || mesh.Indices.Length < 3)
            return;

        bool hasNormals = mesh.Normals.Length >= vertices * 3;
        bool hasJoints = mesh.Joints.Length >= vertices * 4 && mesh.Weights.Length >= vertices * 4;
        var verts = new PreviewVertex[vertices];
        float minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        float minX = float.MaxValue, minY = float.MaxValue;
        for (int i = 0; i < vertices; i++)
        {
            float x = mesh.Positions[i * 3];
            float y = mesh.Positions[i * 3 + 1];
            float z = mesh.Positions[i * 3 + 2];
            verts[i].Position = new Vector3(x, y, z);
            verts[i].Normal = hasNormals
                ? new Vector3(mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2])
                : Vector3.UnitZ;
            if (hasJoints)
            {
                verts[i].J0 = mesh.Joints[i * 4];
                verts[i].J1 = mesh.Joints[i * 4 + 1];
                verts[i].J2 = mesh.Joints[i * 4 + 2];
                verts[i].J3 = mesh.Joints[i * 4 + 3];
                verts[i].Weights = new Vector4(
                    mesh.Weights[i * 4],
                    mesh.Weights[i * 4 + 1],
                    mesh.Weights[i * 4 + 2],
                    mesh.Weights[i * 4 + 3]);
            }
            minX = MathF.Min(minX, x);
            minY = MathF.Min(minY, y);
            minZ = MathF.Min(minZ, z);
            maxX = MathF.Max(maxX, x);
            maxY = MathF.Max(maxY, y);
            maxZ = MathF.Max(maxZ, z);
        }
        if (!hasNormals)
            AccumulateNormals(verts, mesh.Indices);
        _floor = minZ;
        float radius = MathF.Max(0.001f, (new Vector3(maxX, maxY, maxZ) - new Vector3(minX, minY, minZ)).Length() * 0.5f);
        _meshVertices = _device.CreateBuffer<PreviewVertex>(verts, BindFlags.VertexBuffer);
        _meshIndices = _device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer);
        _meshIndexCount = mesh.Indices.Length;

        float extent = MathF.Max(radius, 1f);
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        const int lines = 8;
        var grid = new List<PreviewVertex>((lines * 2 + 1) * 4);
        for (int i = -lines; i <= lines; i++)
        {
            float at = extent * i / lines;
            AddGridLine(grid, centerX - extent, centerY + at, centerX + extent, centerY + at);
            AddGridLine(grid, centerX + at, centerY - extent, centerX + at, centerY + extent);
        }
        _gridVertices = _device.CreateBuffer(CollectionsMarshal.AsSpan(grid), BindFlags.VertexBuffer);
        _gridVertexCount = grid.Count;
        _last = _last with { Albedo = mesh.Albedo };
    }

    private void AddGridLine(List<PreviewVertex> grid, float x0, float y0, float x1, float y1)
    {
        grid.Add(new PreviewVertex { Position = new Vector3(x0, y0, _floor), Normal = Vector3.UnitZ });
        grid.Add(new PreviewVertex { Position = new Vector3(x1, y1, _floor), Normal = Vector3.UnitZ });
    }

    private static void AccumulateNormals(PreviewVertex[] vertices, int[] indices)
    {
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];
            if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length || (uint)c >= (uint)vertices.Length)
                continue;
            Vector3 face = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            vertices[a].Normal += face;
            vertices[b].Normal += face;
            vertices[c].Normal += face;
        }
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Normal = vertices[i].Normal.LengthSquared() < 1e-8f
                ? Vector3.UnitZ
                : Vector3.Normalize(vertices[i].Normal);
        }
    }

    private void ReleaseViews()
    {
        _color?.Dispose();
        _depth?.Dispose();
        _depthTexture?.Dispose();
        _color = null;
        _depth = null;
        _depthTexture = null;
    }

    private bool TryPixelSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (_panel is null)
            return false;
        width = Math.Max(0, (int)Math.Round(_panel.ActualWidth * _panel.CompositionScaleX));
        height = Math.Max(0, (int)Math.Round(_panel.ActualHeight * _panel.CompositionScaleY));
        return width >= 2 && height >= 2;
    }

    private static byte[] Compile(string source, string entry, string profile)
    {
        return Compiler.Compile(
            source,
            entry,
            "model.hlsl",
            profile,
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None).ToArray();
    }

    private sealed record MeshUpload(
        float[] Positions,
        float[] Normals,
        int[] Indices,
        Vector3 Albedo,
        ushort[] Joints,
        float[] Weights);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PreviewVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public ushort J0;
        public ushort J1;
        public ushort J2;
        public ushort J3;
        public Vector4 Weights;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 View;
        public Vector4 Albedo;
        public float Unlit;
        public float Skinning;
        public float AnimFrame;
        public float BoneCount;
        public float FrameCount;
        public float Pad0;
        public float Pad1;
        public float Pad2;
    }

    private void ReleaseClip()
    {
        _clipView?.Dispose();
        _clipBuffer?.Dispose();
        _clipView = null;
        _clipBuffer = null;
    }
}

public readonly record struct ViewFrame(
    Vector3 Center,
    Vector3 Pan,
    float Yaw,
    float Pitch,
    float Distance,
    float Radius,
    Vector3 Albedo);
