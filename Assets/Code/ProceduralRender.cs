using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.UnifiedRayTracing;
using static UnityEngine.GraphicsBuffer;
namespace ProceduralRender{
	public class ProceduralRender : MonoBehaviour{
		public MeshFilter terrain;
		public Texture2D generationTexture;
		[Range(0,1000)] public float drawDistance = 100;
		public List<ProceduralAssets> assets = new();
		[Header("Shaders")]
		[Tooltip("Compute version of UnifiedRayTracing Shader")]
		public ComputeShader raycastShader;
		[Tooltip("RayTracing version of UnifiedRayTracing Shader")]
		public RayTracingShader raytraceShader;
		public ComputeShader setupShader;
		public string setupRaycastsKernel = "SetupRaycasts";
		public string fillTransformsKernel = "FillTransforms";
		public ComputeShader cullShader;
		public string cullKernel = "Cull";
		[HideInInspector] public GraphicsBuffer indirectBuffer;
		[HideInInspector] public GraphicsBuffer rays;
		[HideInInspector] public GraphicsBuffer transforms;
		[HideInInspector] public GraphicsBuffer frustumPlanes;
		[HideInInspector] public GraphicsBuffer visibleOffsets;
		[HideInInspector] public GraphicsBuffer visibleOffsetCounters;
		[HideInInspector] public GraphicsBuffer objectRadii;
		[HideInInspector] public Mesh combined;
		[HideInInspector] public int totalInstances;
		[HideInInspector] public bool canRender;
		[HideInInspector] public RenderParams[] renderParams;
		[HideInInspector] public IndirectDrawIndexedArgs[] indirectData;
		public void OnEnable(){
			this.OnDestroy();
			var assetCount = this.assets.Count;
			this.indirectBuffer = new(Target.IndirectArguments,assetCount,IndirectDrawIndexedArgs.size);
			this.indirectBuffer.name = "ProceduralRenderIndirect";
			this.indirectData = new IndirectDrawIndexedArgs[assetCount];
			var meshes = new CombineInstance[assetCount];
			this.objectRadii = new(Target.Structured,UsageFlags.LockBufferForWrite,assetCount,sizeof(float));
			this.objectRadii.name = "ProceduralRenderObjectRadii";
			var objectRadii = this.objectRadii.LockBufferForWrite<float>(0,this.objectRadii.count).AsSpan();
			for(var index = 0;index < assetCount;index += 1){
				var mesh = meshes[index].mesh = this.assets[index].mesh.sharedMesh;
				objectRadii[index] = mesh.bounds.extents.magnitude;
			}
			this.objectRadii.UnlockBufferAfterWrite<float>(this.objectRadii.count);
			this.combined = new();
			this.combined.CombineMeshes(meshes,false,false,false);
			for(var index = 0;index < this.indirectData.Length;index += 1){
				this.indirectData[index].indexCountPerInstance = this.combined.GetIndexCount(index);
				this.indirectData[index].startIndex = this.combined.GetIndexStart(index);
				this.indirectData[index].baseVertexIndex = this.combined.GetBaseVertex(index);
			}
			var (textureWidth,textureHeight) = (this.generationTexture.width,this.generationTexture.height);
			var texels = this.generationTexture.GetPixelData<ColorRG16>(0);
			var populatedTexels = new NativeReference<int>(Allocator.TempJob);
			var instancesPerType = new NativeList<int>(Allocator.TempJob){0};
			var findCounts = new FindCounts(texels,instancesPerType,populatedTexels);
			var jobFence = IJobExtensions.ScheduleByRef(ref findCounts,default);
			jobFence.Complete();
			var typeOffsets = new NativeArray<int>(instancesPerType.Length,Allocator.TempJob);
			for(var index = 0; index < instancesPerType.Length; index += 1){
				this.indirectData[index].instanceCount = (uint)instancesPerType[index];
				typeOffsets[index] = index == 0 ? 0 : typeOffsets[index - 1] + instancesPerType[index - 1];
				this.indirectData[index].startInstance = (uint)typeOffsets[index];
				this.totalInstances += instancesPerType[index];
			}
			var instancesPerTexel = new GraphicsBuffer(Target.Structured,UsageFlags.LockBufferForWrite,findCounts.populatedTexels.Value,sizeof(int));
			var origins = new GraphicsBuffer(Target.Structured,UsageFlags.LockBufferForWrite,findCounts.populatedTexels.Value,Marshal.SizeOf<float3>());
			var indexOffsets = new GraphicsBuffer(Target.Structured,UsageFlags.LockBufferForWrite,findCounts.populatedTexels.Value,sizeof(int));
			instancesPerTexel.name = "ProceduralRenderInstancesPerTexel";
			origins.name = "ProceduralRenderOrigins";
			indexOffsets.name = "ProceduralRenderIndexOffsets";
			var originsMapped = origins.LockBufferForWrite<float3>(0,origins.count);
			var indexOffsetsMapped = indexOffsets.LockBufferForWrite<int>(0,indexOffsets.count);
			var instancesPerTexelMapped = instancesPerTexel.LockBufferForWrite<int>(0,instancesPerTexel.count);
			var makeSortedOffsets = new MakeSortedOffsets(texels,typeOffsets,instancesPerTexelMapped,indexOffsetsMapped,originsMapped,textureWidth);
			jobFence = IJobExtensions.ScheduleByRef(ref makeSortedOffsets,jobFence);
			jobFence.Complete();
			origins.UnlockBufferAfterWrite<float3>(origins.count);
			indexOffsets.UnlockBufferAfterWrite<int>(indexOffsets.count);
			instancesPerTexel.UnlockBufferAfterWrite<int>(instancesPerTexel.count);
			var worldTransform = (float4x4)this.terrain.transform.localToWorldMatrix;
			var bounds = this.terrain.sharedMesh.bounds;
			bounds.center = math.transform(worldTransform,bounds.center);
			bounds.extents = math.abs(worldTransform.c0.xyz) * bounds.extents.x + math.abs(worldTransform.c1.xyz) * bounds.extents.y + math.abs(worldTransform.c2.xyz) * bounds.extents.z;
			this.rays = new(Target.Structured,this.totalInstances,Marshal.SizeOf<Ray>());
			var cbuffer = new GraphicsBuffer(Target.Constant,UsageFlags.LockBufferForWrite,1,Marshal.SizeOf<SetupRaycastCBuffer>());
			this.rays.name = "ProceduralRenderRays";
			cbuffer.name = "ProceduralRenderSetupRaycastsCbuffer";
			var cbufferSpan = cbuffer.LockBufferForWrite<SetupRaycastCBuffer>(0,cbuffer.count).AsSpan();
			cbufferSpan[0].terrainBoundsMin = bounds.min;
			cbufferSpan[0].terrainBoundsMax = bounds.max;
			cbufferSpan[0].terrainHeight = math.abs(bounds.max.y - bounds.min.y) + 1;
			cbufferSpan[0].textureSize = new(textureWidth,1,textureHeight);
			cbufferSpan[0].aspectRatio = (float)textureWidth / textureHeight;
			cbufferSpan[0].worldCellSize = (bounds.max - bounds.min) / (float3)cbufferSpan[0].textureSize;
			cbuffer.UnlockBufferAfterWrite<SetupRaycastCBuffer>(cbuffer.count);
			var setupRaycastsKernel = this.setupShader.FindKernel(this.setupRaycastsKernel);
			this.setupShader.GetKernelThreadGroupSizes(setupRaycastsKernel,out var threadsX,out var threadsY,out var threadsZ);
			this.setupShader.SetConstantBuffer("SetupRaycastCBuffer",cbuffer,0,Marshal.SizeOf<SetupRaycastCBuffer>());
			this.setupShader.SetBuffer(setupRaycastsKernel,"origins",origins);
			this.setupShader.SetBuffer(setupRaycastsKernel,"indexOffsets",indexOffsets);
			this.setupShader.SetBuffer(setupRaycastsKernel,"instancesPerTexel",instancesPerTexel);
			this.setupShader.SetBuffer(setupRaycastsKernel,"rays",this.rays);
			this.setupShader.Dispatch(setupRaycastsKernel,Mathf.CeilToInt((float)indexOffsets.count / threadsX),(int)threadsY,(int)threadsZ);
			this.DispatchRays();
			this.transforms = new(Target.Structured,this.totalInstances,Marshal.SizeOf<float3x4>());
			this.transforms.name = "ProceduralRenderTransforms";
			var fillTransformsKernel = this.setupShader.FindKernel(this.fillTransformsKernel);
			this.setupShader.GetKernelThreadGroupSizes(fillTransformsKernel,out threadsX,out threadsY,out threadsZ);
			this.setupShader.SetBuffer(fillTransformsKernel,"rays",this.rays);
			this.setupShader.SetBuffer(fillTransformsKernel,"transforms",this.transforms);
			this.setupShader.Dispatch(fillTransformsKernel,Mathf.CeilToInt((float)this.rays.count / threadsX),(int)threadsY,(int)threadsZ);
			this.frustumPlanes = new(Target.Structured,UsageFlags.LockBufferForWrite,6,Marshal.SizeOf<Plane>());
			this.visibleOffsets = new(Target.Structured,this.totalInstances,sizeof(uint));
			this.visibleOffsetCounters = new(Target.Structured,assetCount,sizeof(uint));
			this.frustumPlanes.name = "ProceduralRenderFrustumPlanes";
			this.visibleOffsets.name = "ProceduralRenderVisibleOffsets";
			this.visibleOffsetCounters.name = "ProceduralRenderVisibleOffsetCounters";
			var cullKernel = this.cullShader.FindKernel(this.cullKernel);
			this.cullShader.SetBuffer(cullKernel,"transforms",this.transforms);
			this.cullShader.SetBuffer(cullKernel,"frustumPlanes",this.frustumPlanes);
			this.cullShader.SetBuffer(cullKernel,"visibleOffsets",this.visibleOffsets);
			this.cullShader.SetBuffer(cullKernel,"indirectBuffer",this.indirectBuffer);
			this.cullShader.SetBuffer(cullKernel,"visibleOffsetCounters",this.visibleOffsetCounters);
			this.cullShader.SetBuffer(cullKernel,"objectRadii",this.objectRadii);
			this.renderParams = new RenderParams[assetCount];
			for(var index = 0;index < this.assets.Count;index += 1){
				this.renderParams[index] = new(this.assets[index].material);
				this.renderParams[index].material.SetBuffer("transforms",this.transforms);
				this.renderParams[index].material.SetBuffer("visibleOffsets",this.visibleOffsets);
				this.renderParams[index].receiveShadows = true;
				this.renderParams[index].worldBounds = bounds;
				this.renderParams[index].shadowCastingMode = ShadowCastingMode.Off;
			}
			this.canRender = true;
			populatedTexels.Dispose();
			instancesPerType.Dispose();
			typeOffsets.Dispose();
			instancesPerTexel.Dispose();
			origins.Release();
			indexOffsets.Release();
			cbuffer.Release();
			this.rays.Release();
		}
		public void OnDisable() => this.OnDestroy();
		public void LateUpdate(){
			var keyboard = Keyboard.current;
			if(Application.isEditor && keyboard != null){
				var control = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed; 
				var alt = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
				if(control && alt && keyboard.gKey.wasPressedThisFrame){this.OnEnable();}
			}
			if(!this.canRender){return;}
			var camera = Camera.main;
			var planes = this.frustumPlanes.LockBufferForWrite<Plane>(0,this.frustumPlanes.count);
			GeometryUtility.CalculateFrustumPlanes(camera,planes.AsSpan());
			this.frustumPlanes.UnlockBufferAfterWrite<Plane>(this.frustumPlanes.count);
			this.indirectBuffer.SetData(this.indirectData);
			this.visibleOffsetCounters.SetData(new int[this.visibleOffsetCounters.count]);
			var cullKernel = this.cullShader.FindKernel(this.cullKernel);
			this.cullShader.SetVector("cameraPosition",new float4(camera.transform.position,0));
			this.cullShader.SetFloat("drawDistance",this.drawDistance);
			this.cullShader.GetKernelThreadGroupSizes(cullKernel,out var threadsX,out var threadsY,out var threadsZ);
			for(var index = 0;index < this.indirectData.Length;index += 1){
				if(this.indirectData[index].instanceCount < 1){continue;}
				this.cullShader.SetInt("drawID",index);
				this.cullShader.SetInt("totalThreads",(int)this.indirectData[index].instanceCount);
				this.cullShader.Dispatch(cullKernel,Mathf.CeilToInt((float)this.indirectData[index].instanceCount / threadsX),(int)threadsY,(int)threadsZ);
			}
			for(var index = 0;index < this.indirectBuffer.count;index += 1){
				Graphics.RenderMeshIndirect(this.renderParams[index],this.combined,this.indirectBuffer,startCommand:index);
			}
		}
		public void DispatchRays(){
			var resources = new RayTracingResources();
			var results = resources.LoadFromRenderPipelineResources();
			var hardwareAcceleration = RayTracingContext.IsBackendSupported(RayTracingBackend.Hardware);
			var backend = hardwareAcceleration ? RayTracingBackend.Hardware : RayTracingBackend.Compute;
			var context = new RayTracingContext(backend,resources);
			var bvhOptions = new AccelerationStructureOptions();
			bvhOptions.buildFlags = BuildFlags.PreferFastBuild;
			var accelerationStructure = context.CreateAccelerationStructure(bvhOptions);
			var terrainMesh = this.terrain.sharedMesh;
			var submeshCount = terrainMesh.subMeshCount;
			var meshInstances = new MeshInstanceDesc[submeshCount];
			for(var index = 0;index < submeshCount;index += 1){
				meshInstances[index] = new(terrainMesh,index);
				meshInstances[index].localToWorldMatrix = this.terrain.transform.localToWorldMatrix;
				meshInstances[index].instanceID = (uint)index;
				accelerationStructure.AddInstance(meshInstances[index]);
			}
			var urtShader = hardwareAcceleration ? (UnityEngine.Object)this.raytraceShader : this.raycastShader;
			var shader = context.CreateRayTracingShader(urtShader);
			var commandBuffer = new CommandBuffer();
			var scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(accelerationStructure,shader,(uint)this.totalInstances,1,1);
			commandBuffer.name = "RaycastCommandBuffer";
			if(scratchBuffer is not null){scratchBuffer.name = "RaycastScratchBuffer";}
			accelerationStructure.Build(commandBuffer,scratchBuffer);
			shader.SetAccelerationStructure(commandBuffer,"_AccelStruct",accelerationStructure);
			shader.SetBufferParam(commandBuffer,Shader.PropertyToID("rays"),this.rays);
			shader.Dispatch(commandBuffer,scratchBuffer,(uint)this.totalInstances,1,1);
			Graphics.ExecuteCommandBuffer(commandBuffer);
			commandBuffer.Release();
			scratchBuffer?.Release();
			accelerationStructure.Dispose();
			context.Dispose();
		}
		public void OnDestroy(){
			this.totalInstances = 0;
			this.canRender = false;
			this.indirectBuffer?.Release();
			this.transforms?.Release();
			this.frustumPlanes?.Release();
			this.visibleOffsets?.Release();
			this.visibleOffsetCounters?.Release();
			this.objectRadii?.Release();
		}
	}
	[BurstCompile]
	public struct FindCounts : IJob{
		[ReadOnly] public NativeArray<ColorRG16> texels;
		public NativeList<int> instancesPerType;
		public NativeReference<int> populatedTexels;
		public int maxType;
		public FindCounts(NativeArray<ColorRG16> texels,NativeList<int> instancesPerType,NativeReference<int> populatedTexels){
			this.texels = texels;
			this.instancesPerType = instancesPerType;
			this.populatedTexels = populatedTexels;
			this.maxType = 0;
		}
		public void Execute(){
			var texelsSpan = this.texels.AsReadOnlySpan();
			for(var index = 0;index < texelsSpan.Length;index += 1){
				var (type,instances) = (texelsSpan[index].r,texelsSpan[index].g);
				if(type < 1){continue;}
				var typeIndex = type - 1;
				if(this.maxType < typeIndex){
					this.maxType = typeIndex;
					this.instancesPerType.Add(0);
				}
				this.populatedTexels.Value += 1;
				this.instancesPerType[typeIndex] += instances;
			}
		}
	}
	[BurstCompile]
	public struct MakeSortedOffsets : IJob{
		[ReadOnly] public NativeArray<ColorRG16> texels;
		public NativeArray<int> offsetPerType;
		[WriteOnly] public NativeArray<int> instancesPerTexel;
		[WriteOnly] public NativeArray<int> offsets;
		[WriteOnly] public NativeArray<float3> origins;
		[ReadOnly] public int textureWidth;
		public int offsetIndex;
		public MakeSortedOffsets(NativeArray<ColorRG16> texels,NativeArray<int> offsetPerType,NativeArray<int> instancesPerTexel,NativeArray<int> offsets,NativeArray<float3> origins,int textureWidth){
			this.texels = texels;
			this.offsetPerType = offsetPerType;
			this.instancesPerTexel = instancesPerTexel;
			this.origins = origins;
			this.offsets = offsets;
			this.textureWidth = textureWidth;
			this.offsetIndex = 0;
		}
		public void Execute(){
			var texelsSpan = this.texels.AsReadOnlySpan();
			var offsetsSpan = this.offsets.AsSpan();
			var originsSpan = this.origins.AsSpan();
			for(var index = 0;index < texelsSpan.Length;index += 1){
				var (type,instances) = (texelsSpan[index].r,texelsSpan[index].g);
				if(type < 1){continue;}
				var offset = this.offsetPerType[type - 1];
				offsetsSpan[this.offsetIndex] = offset;
				originsSpan[this.offsetIndex] = new(index % this.textureWidth,1f,index / this.textureWidth);
				this.instancesPerTexel[this.offsetIndex] = instances;
				this.offsetPerType[type - 1] += instances;
				this.offsetIndex += 1;
			}
		}
	}
	[Serializable]
	public class ProceduralAssets{
		public MeshFilter mesh;
		public Material material;
	}
	public struct ColorRG16{
		public byte r;
		public byte g;
	}
	public struct Ray{
		public float3 origin;
		public float distance;
	}
	[StructLayout(LayoutKind.Explicit)]
	public struct SetupRaycastCBuffer{
		[FieldOffset(0)] public Vector3 terrainBoundsMin;
		[FieldOffset(16)] public Vector3 terrainBoundsMax;
		[FieldOffset(28)] public float terrainHeight;
		[FieldOffset(32)] public Vector3 textureSize;
		[FieldOffset(44)] public float aspectRatio;
		[FieldOffset(48)] public Vector3 worldCellSize;
	}
	#if UNITY_EDITOR
	public class RayTracingStripping: IRenderPipelineGraphicsSettingsStripper<RayTracingRenderPipelineResources>{
		public bool active => true;
		public bool CanRemoveSettings(RayTracingRenderPipelineResources settings) => false;
	}
	#endif
}