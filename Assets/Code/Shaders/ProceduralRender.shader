Shader "Procedural Render"{
	Properties{
		[MainTexture] mainTexture("Texture",2D) = "white"{}
		[MainColor] baseColor("Outline Color",Color) = (0.0,0.7,0.0,1.0)
		[Toggle(_LIGHT_NDOTL_ON)] _LIGHT_NDOTL_ON("Angle-based diffusion",Integer) = 0
		[Toggle(_ADDITIONAL_LIGHT_NDOTL_ON)] _ADDITIONAL_LIGHT_NDOTL_ON("Angle-based diffusion (additional lights)",Integer) = 0
	}
	SubShader{
		HLSLINCLUDE
			#pragma use_dxc
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
			#pragma multi_compile_instancing
			CBUFFER_START(UnityPerMaterial)
				float4 baseColor;
				float4 mainTexture_ST;
			CBUFFER_END
			#ifdef UNITY_DOTS_INSTANCING_ENABLED
				UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
					UNITY_DOTS_INSTANCED_PROP(float4,baseColor);
					UNITY_DOTS_INSTANCED_PROP(float4,mainTexture_ST);
				UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
				static float4 unity_DOTS_Sampled_baseColor;
				static float4 unity_DOTS_Sampled_mainTexture_ST;
				void SetupDOTSMaterialPropertyCaches(){
					unity_DOTS_Sampled_baseColor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4,baseColor);
					unity_DOTS_Sampled_mainTexture_ST = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4,mainTexture_ST);
				}
				#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
				#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSMaterialPropertyCaches()
				#define baseColor unity_DOTS_Sampled_baseColor
				#define mainTexture_ST unity_DOTS_Sampled_mainTexture_ST
			#endif
			StructuredBuffer<float3x4> transforms;
			StructuredBuffer<uint> visibleOffsets;
		ENDHLSL
		Pass{
			Name "Color"
			Tags{"LightMode" = "UniversalForward"}
			Cull Off
			HLSLPROGRAM
				#pragma vertex vertexPass
				#pragma fragment pixelPass
				#pragma multi_compile _ _CLUSTER_LIGHT_LOOP _ADDITIONAL_LIGHTS
				#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
				#pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS _SHADOWS_SOFT
				#pragma shader_feature_local_fragment _ _LIGHT_NDOTL_ON _ADDITIONAL_LIGHT_NDOTL_ON
				#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
				#include "UnityIndirect.cginc"
				TEXTURE2D(mainTexture);
				SAMPLER(sampler_mainTexture);
				struct VertexInput{
					float4 position					: POSITION;
					float3 normal					: NORMAL;
					float2 uv						: TEXCOORD0;
				};
				struct VertexOutput{
					float4 position					: SV_Position;
					float3 normal					: NORMAL;
					float2 uv						: TEXCOORD0;
					float4 shadowUV					: TEXCOORD1;
					float3 positionWorld			: TEXCOORD2;
				};
				half3 ProcessAdditionalLight(uint index,VertexOutput interpolated,InputData inputData){
					half4 shadowMask = CalculateShadowMask(inputData);
					Light light = GetAdditionalLight(index,interpolated.positionWorld,shadowMask);
					#if _ADDITIONAL_LIGHT_NDOTL_ON
						half NdotL = saturate(dot(interpolated.normal,light.direction));
						return light.color * NdotL * light.distanceAttenuation * light.shadowAttenuation;
					#else
						return light.color * light.distanceAttenuation * light.shadowAttenuation;
					#endif
				}
				VertexOutput vertexPass(VertexInput input,uint instanceID : SV_InstanceID){
					VertexOutput output = (VertexOutput)0;
					InitIndirectDrawArgs(0);
					instanceID = GetIndirectInstanceID_Base(instanceID); //For D3D12. Use GetIndirectInstanceID() for Vulkan.
					instanceID = visibleOffsets[instanceID];
					output.positionWorld = mul(transforms[instanceID],input.position);
					output.position = TransformWorldToHClip(output.positionWorld);
					output.normal = TransformObjectToWorldNormal(input.normal);
					output.uv = input.uv;
					output.shadowUV = TransformWorldToShadowCoord(output.positionWorld);
					return output;
				}
				half4 pixelPass(VertexOutput input) : SV_Target{
					Light light = GetMainLight(input.shadowUV);
					half4 textureColor = SAMPLE_TEXTURE2D(mainTexture,sampler_mainTexture,TRANSFORM_TEX(input.uv,mainTexture));
					half3 sphericalHarmonics = SampleSH(input.normal);
					half4 color = half4(1,1,1,1);
					color.xyz *= light.color * light.distanceAttenuation * light.shadowAttenuation;
					#if _LIGHT_NDOTL_ON
						half NdotL = saturate(dot(input.normal,light.direction));
						color.xyz *= NdotL;
					#endif
					color.xyz += (1 - light.shadowAttenuation) * sphericalHarmonics;
					uint pixelLightCount = GetAdditionalLightsCount();
					#if defined(_ADDITIONAL_LIGHTS)
						InputData inputData = (InputData)0;
						inputData.positionWS = input.position;
						inputData.normalWS = input.normal;
						inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWorld);
						inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.position);
						#if USE_CLUSTER_LIGHT_LOOP
							[loop] for(uint lightIndex = 0;lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT,MAX_VISIBLE_LIGHTS);lightIndex++){
								color.xyz += ProcessAdditionalLight(lightIndex,input,inputData);
							}
						#endif
						LIGHT_LOOP_BEGIN(pixelLightCount)
							color.xyz += ProcessAdditionalLight(lightIndex,input,inputData);
						LIGHT_LOOP_END
					#endif
					return color * baseColor * textureColor;
				}
			ENDHLSL
		}
		Pass{
			Name "ShadowCaster"
			Tags{"LightMode" = "ShadowCaster"}
			ZWrite On
			ZTest LEqual
			HLSLPROGRAM
				#pragma vertex ShadowPassVertex
				#pragma fragment ShadowPassFragment
				#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
				#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
				#include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
			ENDHLSL
		}
	}
}